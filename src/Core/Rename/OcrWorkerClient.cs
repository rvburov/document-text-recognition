using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using System.Text;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>Один последовательный распознаватель в отдельном процессе. Родитель сохраняет
/// контроль над именами файлов; канал передает только запрос чтения и DocumentResult.</summary>
internal sealed class OcrWorkerClient : IDocumentRecognizer
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);
    private readonly Lock _gate = new();
    private readonly Lock _diagnosticGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly StringBuilder _diagnostics = new();
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private WorkerProcessMetrics.Registration? _registration;
    private Task _stdout = Task.CompletedTask;
    private Task _stderr = Task.CompletedTask;
    private long _request;
    private int _disposeRequested;
    private bool _broken;
    private bool _cleaned;
    private double? _finalCpu;

    internal int ProcessId { get; private set; }
    internal OcrWorkerConfiguration? ActualConfiguration { get; private set; }
    internal string? CoreSha256 { get; private set; }
    internal bool HammingAvx2Available { get; private set; }
    internal event Action? ReadStarted;

    internal OcrWorkerClient(OcrWorkerConfiguration configuration, CancellationToken startupCancellation = default,
        OcrWorkerLaunchCommand? launchCommand = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        startupCancellation.ThrowIfCancellationRequested();
        var command = OcrWorkerHost.ResolveLaunchCommand(launchCommand);
        var expectedCore = OcrWorkerHost.CurrentCoreSha256();
        var name = "dtr-ocr-" + Guid.NewGuid().ToString("N");
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(startupCancellation);
        startup.CancelAfter(StartupTimeout);
        try
        {
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var start = new ProcessStartInfo(command.FileName)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = OcrComponents.ProgramDir,
            };
            foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(OcrWorkerHost.Argument);
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            _process = new Process { StartInfo = start };
            if (!_process.Start()) throw new OcrWorkerException("Не удалось создать OCR worker.");
            ProcessId = _process.Id;
            _registration = WorkerProcessMetrics.Register(_process);
            _stdout = DrainAsync(_process.StandardOutput, "stdout");
            _stderr = DrainAsync(_process.StandardError, "stderr");

            var connected = _pipe.WaitForConnectionAsync(startup.Token);
            var exited = _process.WaitForExitAsync(CancellationToken.None);
            Task.WhenAny(connected, exited).GetAwaiter().GetResult();
            startupCancellation.ThrowIfCancellationRequested();
            if (exited.IsCompleted && !connected.IsCompletedSuccessfully)
                throw new OcrWorkerException("OCR worker завершился до подключения.");
            connected.GetAwaiter().GetResult();
            Send(new OcrWorkerMessage("configure", Configuration: configuration), startup.Token);
            var reply = Receive(startup.Token);
            if (reply is not { Kind: "ready" })
                throw new OcrWorkerException("OCR worker не запустился: " + (reply?.Error ?? "канал закрыт"));
            ActualConfiguration = reply.Configuration;
            CoreSha256 = reply.CoreSha256;
            HammingAvx2Available = reply.Avx2Available == true;
            if (ActualConfiguration != configuration || !string.Equals(expectedCore, CoreSha256, StringComparison.OrdinalIgnoreCase) ||
                reply.Avx2Available != TemplateMatcher.HammingAvx2Available)
                throw new OcrWorkerException("OCR worker подтвердил другую конфигурацию, Core или доступность AVX2.");
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested)
        {
            TryCleanup(graceful: false);
            _shutdown.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            TryCleanup(graceful: false);
            _shutdown.Dispose();
            throw new OcrWorkerException("Ошибка запуска OCR worker: " + ex.Message + DiagnosticSuffix(), ex);
        }
    }

    public DocumentResult Read(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            if (_broken || _cleaned) throw new OcrWorkerException("OCR worker больше недоступен; повторный запуск не выполняется.");
            OcrWorkerMessage reply;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            try
            {
                var id = ++_request;
                Send(new OcrWorkerMessage("read", id, Path: Path.GetFullPath(path)), operation.Token);
                var started = false;
                while (true)
                {
                    reply = Receive(operation.Token) ?? throw new EndOfStreamException("OCR worker закрыл канал.");
                    if (reply.RequestId != id) throw new InvalidDataException("OCR worker ответил на другой запрос.");
                    if (reply.Kind == "reading" && !started)
                    {
                        started = true;
                        ReadStarted?.Invoke();
                        continue;
                    }
                    if (reply.Kind == "result" && reply.Result is not null || reply.Kind == "error") break;
                    throw new InvalidDataException("Неверный ответ OCR worker: " + reply.Kind);
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                _broken = true;
                TryCleanup(graceful: false);
                throw new OperationCanceledException("Чтение OCR worker остановлено.", ex, ct);
            }
            catch (Exception ex)
            {
                _broken = true;
                TryCleanup(graceful: false);
                throw new OcrWorkerException("Сбой связи с OCR worker: " + ex.Message + DiagnosticSuffix(), ex);
            }
            // A reported per-document error is not a broken transport. As with an in-process
            // recognizer, the caller records Error; no request is retried.
            if (reply.Kind == "error") throw new OcrWorkerException("OCR worker: " + reply.Error);
            return reply.Result!;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        // Cancellation only interrupts pending IPC. Kill+Wait occurs on the Read/Dispose
        // thread, never inside a caller's (potentially UI-thread) ct.Cancel callback.
        _shutdown.Cancel();
        lock (_gate)
        {
            try { Cleanup(graceful: !_broken); }
            finally { _shutdown.Dispose(); }
        }
    }

    private void Cleanup(bool graceful)
    {
        if (_cleaned) return;
        var process = _process;
        var exited = process is null || ProcessId == 0;
        string? shutdownError = null;
        try
        {
            if (process is not null && ProcessId != 0)
            {
                var started = Stopwatch.GetTimestamp();
                if (graceful && !HasExited(process) && _pipe?.IsConnected == true)
                {
                    using var stop = new CancellationTokenSource(ShutdownGrace);
                    try
                    {
                        Send(new OcrWorkerMessage("stop"), stop.Token);
                        var reply = Receive(stop.Token);
                        if (reply is { Kind: "stopped" }) { _finalCpu = reply.CpuSeconds; shutdownError = reply.Error; }
                    }
                    catch (Exception) { /* Grace expiry/EOF is followed by Kill+Wait below. */ }
                }
                var remaining = graceful ? Math.Max(0, (int)(ShutdownGrace - Stopwatch.GetElapsedTime(started)).TotalMilliseconds) : 0;
                if (!HasExited(process) && (remaining == 0 || !process.WaitForExit(remaining)))
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception) when (HasExited(process)) { }
                }
                if (!process.WaitForExit(10_000))
                    throw new OcrWorkerException($"OCR worker PID {ProcessId} не завершился после Kill; регистрация оставлена активной.");
                exited = true;
                if (graceful && (shutdownError is not null || process.ExitCode != 0))
                    throw new OcrWorkerException($"Ошибка завершения OCR worker (код {process.ExitCode}): {shutdownError ?? "процесс завершился без штатного подтверждения"}");
            }
        }
        finally
        {
            exited |= process is not null && ProcessId != 0 && HasExited(process);
            _pipe?.Dispose();
            _pipe = null;
            try { Task.WhenAll(_stdout, _stderr).Wait(TimeSpan.FromSeconds(1)); } catch (Exception) { }
            if (exited)
            {
                try { _registration?.Complete(_finalCpu); }
                finally
                {
                    _registration = null;
                    process?.Dispose();
                    _process = null;
                    _cleaned = true;
                }
            }
            // An OS-level failure to kill must not be counted as a completed child.
            // The registry retains the Process for diagnosis until an exit is confirmed.
        }
    }

    private void TryCleanup(bool graceful)
    {
        try { Cleanup(graceful); }
        catch (Exception ex)
        {
            lock (_diagnosticGate)
            {
                _diagnostics.Append(" cleanup: ").Append(ex.Message);
                if (_diagnostics.Length > 4096) _diagnostics.Remove(0, _diagnostics.Length - 4096);
            }
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private async Task DrainAsync(StreamReader reader, string source)
    {
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) is var count && count > 0)
            {
                lock (_diagnosticGate)
                {
                    _diagnostics.Append(source).Append(": ").Append(buffer, 0, count);
                    if (_diagnostics.Length > 4096) _diagnostics.Remove(0, _diagnostics.Length - 4096);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private string DiagnosticSuffix()
    {
        lock (_diagnosticGate) return _diagnostics.Length == 0 ? "." : ": " + _diagnostics.ToString().Trim();
    }
    private void Send(OcrWorkerMessage message, CancellationToken ct) =>
        OcrWorkerProtocol.WriteAsync(_pipe!, message, ct).AsTask().GetAwaiter().GetResult();
    private OcrWorkerMessage? Receive(CancellationToken ct) =>
        OcrWorkerProtocol.ReadAsync(_pipe!, ct).AsTask().GetAwaiter().GetResult();
}
