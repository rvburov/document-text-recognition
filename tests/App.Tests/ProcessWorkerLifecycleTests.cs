using System.Collections.Concurrent;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Tests;

namespace DocumentTextRecognition.AppTests;

/// <summary>Проверяет только жизненный цикл обработчиков и ограниченных очередей; OCR здесь подменен.</summary>
public sealed class ProcessWorkerLifecycleTests
{
    private sealed class Recognizer(Func<string, CancellationToken, DocumentResult> read, Action? dispose = null) : IDocumentRecognizer
    {
        private int _reads;
        private int _disposals;
        public int Reads => Volatile.Read(ref _reads);
        public int Disposals => Volatile.Read(ref _disposals);

        public DocumentResult Read(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            return read(path, ct);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref _disposals);
            dispose?.Invoke();
        }
    }

    private sealed class Sink(Action<JournalEntry>? onFile = null) : IRunSink
    {
        public ConcurrentQueue<JournalEntry> Entries { get; } = new();
        public ConcurrentQueue<string> Messages { get; } = new();
        public void Found(int count) { }
        public void Message(string text) => Messages.Enqueue(text);
        public void File(JournalEntry entry)
        {
            Entries.Enqueue(entry);
            onFile?.Invoke(entry);
        }
    }

    [Fact]
    public void Factory_failure_still_disposes_every_created_recognizer_when_first_disposal_throws()
    {
        using var folder = new TempDir("worker-lifecycle-init");
        var files = Enumerable.Range(0, 3).Select(i => folder.Touch($"{i:D3}.pdf")).ToArray();
        var first = new Recognizer((_, _) => DocumentResult.Fail("unused"),
            () => throw new InvalidOperationException("dispose-first"));
        var second = new Recognizer((_, _) => DocumentResult.Fail("unused"));
        var calls = 0;
        var sink = new Sink();

        var summary = OcrRenameRun.ProcessFiles(new RunSettings(folder.Path, folder.Path, false, Workers: 3), files,
            () => ++calls switch
            {
                1 => first,
                2 => second,
                _ => throw new InvalidOperationException("factory-third"),
            }, sink, CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Equal(0, first.Reads + second.Reads);
        Assert.Equal(0, summary.Processed);
        Assert.Equal(0, summary.Renamed);
        Assert.Contains("factory-third", summary.Fatal);
        Assert.Contains("dispose-first", summary.Fatal);
        Assert.Empty(sink.Entries);
        Assert.All(files, file => Assert.True(File.Exists(file)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Disposal_failure_is_fatal_but_preserves_completed_rename_counts(bool dedicated)
    {
        using var folder = new TempDir("worker-lifecycle-dispose");
        var files = Enumerable.Range(0, 2).Select(i => folder.Touch($"{i:D3}.pdf")).ToArray();
        for (var i = 0; i < files.Length; i++) File.WriteAllText(files[i], $"lifecycle fixture {i}");
        var recognizer = new Recognizer((path, _) =>
                new DocumentResult(new FieldValues($"АБВГ.123456.{Path.GetFileNameWithoutExtension(path)}", "Деталь", "Спецификация"), null),
            () => throw new InvalidOperationException("dispose-worker"));
        var sink = new Sink();
        var settings = new RunSettings(folder.Path, folder.Path, false, Workers: 1) { DedicatedWorkers = dedicated };

        var summary = OcrRenameRun.ProcessFiles(settings, files, () => recognizer, sink, CancellationToken.None);

        Assert.Contains("dispose-worker", summary.Fatal);
        Assert.Equal(2, summary.Found);
        Assert.Equal(2, summary.Processed);
        Assert.Equal(2, summary.Renamed);
        Assert.Equal(0, summary.Skipped);
        Assert.False(summary.Stopped);
        Assert.Equal(2, recognizer.Reads);
        Assert.Equal(1, recognizer.Disposals);
        Assert.Equal(2, sink.Entries.Count);
        Assert.All(sink.Entries, entry => Assert.Equal(FileStatus.Renamed, entry.Status));
        for (var i = 0; i < files.Length; i++)
        {
            Assert.False(File.Exists(files[i]));
            Assert.Equal($"lifecycle fixture {i}", File.ReadAllText(Path.Combine(folder.Path, $"АБВГ.123456.{i:D3} Деталь Спецификация.pdf")));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Sink_failure_cancels_full_result_queue_and_disposes_every_recognizer(bool dedicated)
    {
        using var folder = new TempDir("worker-lifecycle-sink");
        var files = Enumerable.Range(0, 256).Select(i => folder.Touch($"{i:D3}.pdf")).ToArray();
        using var queued = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        const int workers = 3;
        // One result is held by the sink, 64 fill the channel, and each worker has another
        // result waiting to write. The sink failure must release those blocked writers.
        const int saturatedReads = 1 + 64 + workers;
        var reads = 0;
        var recognizers = new List<Recognizer>();
        var failure = new InvalidOperationException("sink-file-failed");
        var sink = new Sink(_ =>
        {
            if (!queued.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("result queue did not fill");
            throw failure;
        });
        var settings = new RunSettings(folder.Path, folder.Path, false, Workers: workers) { DedicatedWorkers = dedicated };
        var run = Task.Run(() => OcrRenameRun.ProcessFiles(settings, files, () =>
        {
            var recognizer = new Recognizer((_, _) =>
            {
                if (Interlocked.Increment(ref reads) >= saturatedReads) queued.Set();
                return DocumentResult.Fail("lifecycle fixture, no OCR");
            });
            recognizers.Add(recognizer);
            return recognizer;
        }, sink, cancellation.Token));

        try
        {
            // A deadlock watchdog, not a throughput assertion.
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await run.WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Same(failure, actual);
            Assert.True(queued.IsSet);
            Assert.True(Volatile.Read(ref reads) >= saturatedReads);
            Assert.Equal(workers, recognizers.Count);
            Assert.All(recognizers, recognizer => Assert.Equal(1, recognizer.Disposals));
            Assert.Single(sink.Entries);
            Assert.All(files, file => Assert.True(File.Exists(file)));
        }
        finally
        {
            cancellation.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { } // Preserve the assertion failure while cleaning up a regressed pipeline.
        }
    }
}
