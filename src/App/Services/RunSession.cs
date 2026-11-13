using System.Collections.Concurrent;
using System.Diagnostics;
using DocumentTextRecognition.Common;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;

namespace DocumentTextRecognition.Services;

/// <summary>
/// Прогон в фоне: шаблоны, список файлов, распознавание в несколько потоков, переименование.
/// Окно забирает новые строки и счетчики по таймеру (<see cref="Drain"/>), поэтому тысячи
/// файлов не перегружают окно.
/// </summary>
public sealed class RunSession : IRunSinkPublic
{
    private readonly ConcurrentQueue<ResultRow> _rows = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private int _found = -1;

    public int Processed, Renamed, Already, Skipped, Errors;

    public bool IsRunning { get; private set; }
    public bool Stopped => _cts.IsCancellationRequested;
    public TimeSpan Elapsed => _clock.Elapsed;
    public int Found => Volatile.Read(ref _found);
    public string? Fatal { get; private set; }

    /// <summary>Строки, пришедшие с прошлого вызова.</summary>
    public List<ResultRow> Drain()
    {
        var list = new List<ResultRow>();
        while (_rows.TryDequeue(out var r)) list.Add(r);
        return list;
    }

    public Task Start(AppSettings settings)
    {
        IsRunning = true;
        _clock.Start();
        var run = new RunSettings(settings.TemplatesFolder, settings.SourceFolder, settings.Recursive, settings.Workers,
            settings.NumberWhenTaken ? NameTakenPolicy.AddNumber : NameTakenPolicy.Skip);
        AppLog.Info($"Запуск: шаблоны «{run.TemplatesFolder}», PDF «{run.SourceFolder}», подпапки {run.Recursive}, потоков {run.EffectiveWorkers}");
        return Task.Run(() =>
        {
            try
            {
                var tessdata = OcrComponents.FindTessdata();
                OcrComponents.RegisterNativeResolver();
                var summary = OcrRenameRun.Execute(run, () =>
                {
                    if (tessdata is null) throw new FileNotFoundException("нет языковых данных Tesseract (папка ocr\\tessdata)");
                    return OcrPair.CreateReal(tessdata);
                }, new Sink(this), _cts.Token);
                Fatal = summary.Fatal;
                if (summary.Fatal is null && _found < 0) Interlocked.Exchange(ref _found, summary.Found);
                AppLog.Info($"Итог: найдено {summary.Found}, обработано {summary.Processed}, переименовано {summary.Renamed}, " +
                            $"имя уже верное {summary.AlreadyNamed}, пропущено {summary.Skipped}, остановлено {summary.Stopped}");
            }
            catch (Exception ex)
            {
                Fatal = $"Сбой прогона: {ex.Message}";
                AppLog.Error("Сбой прогона", ex);
            }
            finally
            {
                _clock.Stop();
                IsRunning = false;
            }
        });
    }

    public void Stop() => _cts.Cancel();

    void IRunSinkPublic.Message(string text) => AppLog.Info(text);
    void IRunSinkPublic.Found(int count) => Interlocked.Exchange(ref _found, count);

    void IRunSinkPublic.File(ResultRow row)
    {
        Interlocked.Increment(ref Processed);
        switch (row.Kind)
        {
            case StatusKind.Renamed: Interlocked.Increment(ref Renamed); break;
            case StatusKind.AlreadyNamed: Interlocked.Increment(ref Already); break;
            case StatusKind.Error: Interlocked.Increment(ref Errors); break;
            default: Interlocked.Increment(ref Skipped); break;
        }
        if (row.Kind is StatusKind.Skipped or StatusKind.Error)
            AppLog.Info($"{row.Was}: {row.StatusText}, {row.Reason}");
        _rows.Enqueue(row);
    }

    private sealed class Sink(IRunSinkPublic owner) : IRunSink
    {
        public void Message(string text) => owner.Message(text);
        public void Found(int count) => owner.Found(count);
        public void File(JournalEntry entry) => owner.File(new ResultRow(entry));
    }
}

/// <summary>Куда прогон сообщает о ходе (в окне).</summary>
public interface IRunSinkPublic
{
    void Message(string text);
    void Found(int count);
    void File(ResultRow row);
}
