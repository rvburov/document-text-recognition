using System.Diagnostics;
using System.IO.Pipes;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>Ранний служебный вход того же App/Performance. Обычный вызов явно разрешает
/// запуск этой точки входа; имя сборки само по себе не считается такой регистрацией.</summary>
internal static class OcrWorkerHost
{
    internal const string Argument = "--ocr-worker";
    private static OcrWorkerLaunchCommand? _registered;
    internal static bool IsRegistered => Volatile.Read(ref _registered) is not null;

    internal static bool TryRunWorker(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (!args.Contains(Argument))
        {
            var entry = Assembly.GetEntryAssembly()?.Location;
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(entry) && !string.IsNullOrEmpty(executable))
            {
                var isDotnet = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase);
                Volatile.Write(ref _registered, new OcrWorkerLaunchCommand(executable, isDotnet ? [entry] : []));
            }
            return false;
        }
        if (args.Length != 3 || args[0] != Argument || string.IsNullOrWhiteSpace(args[1]) || args[1].Length > 120 ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0)
        {
            Console.Error.WriteLine("Некорректные аргументы служебного OCR worker.");
            exitCode = 64;
            return true;
        }
        exitCode = Run(args[1], parentId);
        return true;
    }

    internal static OcrWorkerLaunchCommand ResolveLaunchCommand(OcrWorkerLaunchCommand? launchCommand = null)
    {
        if (launchCommand is not null) return Validate(launchCommand);
        var registered = Volatile.Read(ref _registered)
            ?? throw new NotSupportedException("Текущая точка входа не зарегистрирована как OCR worker host.");
        // dotnet exec with the published app's deps/runtimeconfig may run a probe DLL
        // without its own runtimeconfig. The actual published app is the supported host.
        var app = Path.Combine(OcrComponents.ProgramDir, OperatingSystem.IsWindows()
            ? "DocumentTextRecognition.exe" : "DocumentTextRecognition");
        return Validate(File.Exists(app) ? new OcrWorkerLaunchCommand(app, []) : registered);
    }

    private static OcrWorkerLaunchCommand Validate(OcrWorkerLaunchCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.FileName) || command.Arguments is null ||
            command.Arguments.Any(arg => arg is null))
            throw new ArgumentException("Неверная команда запуска OCR worker.", nameof(command));
        return command with { Arguments = (string[])command.Arguments.Clone() };
    }

    internal static string CurrentCoreSha256()
    {
        using var source = File.OpenRead(typeof(OcrPair).Assembly.Location);
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static int Run(string pipeName, int parentId)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var stopWatchdog = new CancellationTokenSource();
        Process? parent = null;
        Task? watchdog = null;
        try
        {
            parent = Process.GetProcessById(parentId);
            watchdog = WatchParent(parent, stopWatchdog.Token);
            pipe.Connect(30_000);
            var hello = Read(pipe);
            if (hello is not { Kind: "configure", Configuration: { } configuration })
                throw new InvalidDataException("OCR worker не получил конфигурацию.");
            OcrComponents.ConfigureRuntime();
            return RunConfigured(pipe, configuration);
        }
        catch (Exception ex)
        {
            TryWrite(pipe, new OcrWorkerMessage("error", Error: Describe(ex)));
            Console.Error.WriteLine("OCR worker: " + Describe(ex));
            return 70;
        }
        finally
        {
            stopWatchdog.Cancel();
            try { watchdog?.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            parent?.Dispose();
        }
    }

    private static async Task WatchParent(Process parent, CancellationToken ct)
    {
        try
        {
            await parent.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!ct.IsCancellationRequested) Environment.Exit(71);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Console.Error.WriteLine("OCR worker parent monitor: " + Describe(ex));
                Environment.Exit(71); // No dump; OS releases native state if the parent dies mid-inference.
            }
        }
    }

    // Do not inline model/native initialization into the pre-runtime-configuration method.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunConfigured(NamedPipeClientStream pipe, OcrWorkerConfiguration configuration)
    {
        DocTemplate[] templates = [];
        OcrPair? pair = null;
        var stopped = false;
        var exitCode = 0;
        string? shutdownError = null;
        try
        {
            TemplateMatcher.UseAvx2 = configuration.UseAvx2;
            var loads = TemplateLibrary.LoadFolder(configuration.TemplatesFolder);
            templates = loads.Where(load => load.Template is not null).Select(load => load.Template!).ToArray();
            if (templates.Length == 0) throw new InvalidOperationException("Нет пригодных шаблонов в OCR worker.");
            pair = OcrPair.CreateReal(configuration.Tessdata, configuration.UseOneDnn, configuration.ReusePredictors,
                configuration.UseOnnx, configuration.OnnxModelsDir);
            pair.PreferKnownLines = configuration.KnownLines;
            pair.BatchFields = configuration.BatchFields;
            pair.UsePdfText = configuration.UsePdfText;
            pair.UseGostFallback = configuration.UseGostFallback;
            Write(pipe, new OcrWorkerMessage("ready", Configuration: pair.ExportWorkerConfiguration(configuration.TemplatesFolder),
                CoreSha256: CurrentCoreSha256(), Avx2Available: TemplateMatcher.HammingAvx2Available));

            // This entire method, including inference and Dispose, stays on the Main OS
            // thread. Async pipe operations are synchronously waited, never awaited here.
            while (Read(pipe) is { } request)
            {
                if (request.Kind == "stop") { stopped = true; break; }
                if (request.Kind != "read" || request.Path is null || request.RequestId <= 0)
                    throw new InvalidDataException("Неверный запрос OCR worker.");
                Write(pipe, new OcrWorkerMessage("reading", request.RequestId));
                try
                {
                    var result = DocumentReader.Read(request.Path, templates, pair, CancellationToken.None);
                    Write(pipe, new OcrWorkerMessage("result", request.RequestId, Result: result));
                }
                catch (Exception ex)
                {
                    Write(pipe, new OcrWorkerMessage("error", request.RequestId, Error: Describe(ex)));
                }
            }
        }
        catch (Exception ex)
        {
            TryWrite(pipe, new OcrWorkerMessage("error", Error: Describe(ex)));
            Console.Error.WriteLine("OCR worker: " + Describe(ex));
            exitCode = 70;
        }
        finally
        {
            try { pair?.Dispose(); }
            catch (Exception ex) { shutdownError = Describe(ex); Console.Error.WriteLine("OCR worker dispose: " + shutdownError); exitCode = 70; }
            foreach (var template in templates)
            {
                try { template.Dispose(); }
                catch (Exception ex) { shutdownError ??= Describe(ex); Console.Error.WriteLine("OCR template dispose: " + Describe(ex)); exitCode = 70; }
            }
        }
        if (stopped)
        {
            using var process = Process.GetCurrentProcess();
            TryWrite(pipe, new OcrWorkerMessage("stopped", Error: shutdownError, CpuSeconds: process.TotalProcessorTime.TotalSeconds));
        }
        return exitCode;
    }

    private static OcrWorkerMessage? Read(Stream pipe) =>
        OcrWorkerProtocol.ReadAsync(pipe, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    private static void Write(Stream pipe, OcrWorkerMessage message) =>
        OcrWorkerProtocol.WriteAsync(pipe, message, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    private static void TryWrite(Stream pipe, OcrWorkerMessage message)
    {
        try { if (pipe.CanWrite) Write(pipe, message); }
        catch (Exception) { /* The parent may already have closed or killed the worker. */ }
    }
    private static string Describe(Exception ex) => ex.GetType().Name + ": " + ex.Message;
}
