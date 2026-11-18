using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using Xunit.Abstractions;

namespace DocumentTextRecognition.AppTests;

/// <summary>Настоящий App host, отдельные процессы и оба OCR; результаты не подменяются.</summary>
public sealed class ProcessWorkerTests(ITestOutputHelper output)
{
    [Fact]
    public void Process_pool_is_opt_in_and_explicit_worker_count_is_preserved()
    {
        var settings = new RunSettings("templates", "source", false, Workers: 7);
        Assert.False(settings.UseProcessWorkers);
        Assert.Equal(7, (settings with { UseProcessWorkers = true }).EffectiveWorkers);
    }

    [Theory]
    [InlineData(1, 16, 1)]
    [InlineData(4, 16, 4)]
    [InlineData(8, 8, 4)]
    [InlineData(16, 16, 8)]
    [InlineData(8, 4, 2)]
    [InlineData(8, 2, 1)]
    [InlineData(4, 0, 4)]
    public void Automatic_process_count_respects_cpu_and_memory(int cpus, int memoryGiB, int expected) =>
        Assert.Equal(expected, RunSettings.RecommendProcessWorkers(cpus, memoryGiB * 1024L * 1024 * 1024));

    [Fact]
    public void Template_snapshot_preserves_pdf_and_markup_bytes_and_removes_its_directory()
    {
        using var wb = new Workbench();
        CopyTemplates(wb.Templates);
        File.WriteAllText(Path.Combine(wb.Templates, "unrelated.txt"), "Not a template.");
        var originals = Directory.GetFiles(wb.Templates)
            .Where(path => !path.EndsWith(".txt", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Assert.Contains(originals.Keys, name => name.EndsWith(".template.json", StringComparison.Ordinal));
        string capturedFolder;
        using (var snapshot = TemplateSnapshot.Create(wb.Templates))
        {
            capturedFolder = snapshot.Folder;
            Assert.Equal(TemplateLibrary.ListFiles(wb.Templates).Order(StringComparer.Ordinal),
                snapshot.SourceFiles.Order(StringComparer.Ordinal));
            Assert.Equal(originals.Keys.Order(StringComparer.Ordinal),
                Directory.GetFiles(snapshot.Folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            foreach (var (name, bytes) in originals)
            {
                var captured = Path.Combine(snapshot.Folder, name);
                Assert.Equal(bytes, File.ReadAllBytes(captured));
                File.WriteAllText(Path.Combine(wb.Templates, name), "Original changed after capture.");
                Assert.Equal(bytes, File.ReadAllBytes(captured));
            }
            var firstPdf = Directory.GetFiles(snapshot.Folder, "*.pdf").First();
            File.SetAttributes(firstPdf, File.GetAttributes(firstPdf) | FileAttributes.ReadOnly);
        }
        Assert.False(Directory.Exists(capturedFolder));
        Assert.All(originals.Keys, name =>
            Assert.Equal("Original changed after capture.", File.ReadAllText(Path.Combine(wb.Templates, name))));
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_real_workers_export_one_configuration_and_rename_serially_without_losing_bytes(bool useOnnx)
    {
        using var wb = new Workbench();
        var templateFolder = useOnnx ? Path.Combine(wb.Source, "nested-templates") : wb.Templates;
        Directory.CreateDirectory(templateFolder);
        CopyTemplates(templateFolder);
        var originalTemplates = Directory.GetFiles(templateFolder);
        var changedTemplates = new Dictionary<string, string>();
        var samples = TrackedSamples();
        var assembly = samples.Single(s => s.File.Contains("02-assembly", StringComparison.Ordinal));
        var schematic = samples.Single(s => s.File.Contains("03-schematic", StringComparison.Ordinal));
        CopyScan(assembly, Path.Combine(wb.Source, "a.pdf"));
        CopyScan(assembly, Path.Combine(wb.Source, "b.pdf"));
        CopyScan(schematic, Path.Combine(wb.Source, "c.pdf"));
        var expected = new Dictionary<string, string>
        {
            [assembly.ExpectedName] = Hash(assembly.Path),
            [FileNames.Numbered(assembly.ExpectedName, 2)] = Hash(assembly.Path),
            [schematic.ExpectedName] = Hash(schematic.Path),
        };
        var before = WorkerProcessMetrics.Capture();
        var factories = 0;
        var sink = new Results();
        var settings = Settings(wb, NameTakenPolicy.AddNumber) with
        {
            TemplatesFolder = templateFolder,
            Recursive = true,
        };
        using var stop = new CancellationTokenSource();
        using var children = new ObservedChildren();
        var run = Task.Run(() =>
        {
            var callerThread = Environment.CurrentManagedThreadId;
            var summary = OcrRenameRun.Execute(settings, () =>
            {
                Interlocked.Increment(ref factories);
                var pair = OcrPair.CreateReal(OcrComponents.FindTessdata()!, useOnnx: useOnnx);
                pair.BatchFields = false;
                pair.PreferKnownLines = false;
                var exported = pair.ExportWorkerConfiguration(templateFolder);
                Assert.Equal(useOnnx, exported.UseOnnx);
                Assert.False(exported.BatchFields);
                Assert.False(exported.KnownLines);
                // Execute has already captured and validated the templates. Children
                // must use those exact bytes even when the source folder changes now.
                foreach (var original in originalTemplates)
                {
                    File.WriteAllText(original, "Original template replaced after capture.");
                    changedTemplates.Add(original, Hash(original));
                }
                return pair;
            }, sink, stop.Token);
            return (summary, callerThread);
        });
        try
        {
            var (summary, callerThread) = await Observe(run, children);
            Assert.Null(summary.Fatal);
            Assert.False(summary.Stopped);
            Assert.Equal(3, summary.Found);
            Assert.Equal(3, summary.Processed);
            Assert.Equal(3, summary.Renamed);
            Assert.Equal(0, summary.Skipped);
            Assert.Equal(1, factories);
            Assert.Equal(callerThread, Assert.Single(sink.FileThreads.Keys));
            Assert.Equal(3, sink.Entries.Count);
            Assert.All(sink.Entries, entry => Assert.Equal(FileStatus.Renamed, entry.Status));
            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(wb.Source).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            foreach (var (name, hash) in expected) Assert.Equal(hash, Hash(Path.Combine(wb.Source, name)));
            Assert.Equal(originalTemplates.Length, changedTemplates.Count);
            foreach (var (path, hash) in changedTemplates) Assert.Equal(hash, Hash(path));
            Assert.Equal(before.StartedCount + 2, WorkerProcessMetrics.Capture().StartedCount);
            Assert.Equal(before.ActiveCount, WorkerProcessMetrics.Capture().ActiveCount);
            children.AssertExited();
            output.WriteLine($"backend={(useOnnx ? "ONNX" : "Paddle")}; factoryCalls={factories}; childStarts=2; exactNamesAndHashes=3; renameThreads=1; originalTemplatesMutated={changedTemplates.Count}; nestedTemplatesExcluded={useOnnx}");
        }
        finally
        {
            stop.Cancel();
            children.Capture();
            children.StopSurvivors();
            await ObserveFailure(run);
        }
    }

    [OcrFact]
    public void Real_worker_reads_all_five_saved_templates_including_parentheses_exactly()
    {
        var root = SamplesRoot();
        var before = WorkerProcessMetrics.Capture();
        var configuration = Configuration(Path.Combine(root, "templates"));
        using var client = new OcrWorkerClient(configuration, launchCommand: HostCommand());
        using var child = Process.GetProcessById(client.ProcessId);
        try
        {
            foreach (var sample in TrackedSamples())
            {
                var hash = Hash(sample.Path);
                var result = client.Read(sample.Path, CancellationToken.None);
                Assert.True(result.Values is not null, $"{sample.File}: {result.Problem}");
                Assert.Equal(sample.ExpectedName, FileNames.Build(result.Values).Name);
                Assert.Equal(hash, Hash(sample.Path));
                output.WriteLine($"{sample.File}: {sample.ExpectedName}; unchangedSHA256={hash}");
            }
            Assert.Equal(before.StartedCount + 1, WorkerProcessMetrics.Capture().StartedCount);
            client.Dispose();
            Assert.True(child.WaitForExit(5000), "OCR child remains alive after disposal.");
            Assert.Equal(before.ActiveCount, WorkerProcessMetrics.Capture().ActiveCount);
        }
        finally { client.Dispose(); Stop(child); }
    }

    [OcrFact]
    public async Task Failed_real_worker_startup_propagates_error_and_reaps_the_child()
    {
        using var wb = new Workbench();
        var missingTemplates = Path.Combine(wb.Root, "missing-templates");
        Assert.False(Directory.Exists(missingTemplates));
        var configuration = Configuration(Path.Combine(SamplesRoot(), "templates")) with
        {
            TemplatesFolder = missingTemplates,
        };
        var before = WorkerProcessMetrics.Capture();
        using var stop = new CancellationTokenSource();
        using var children = new ObservedChildren();
        var startup = Task.Run(() => Record.Exception(() =>
        {
            using var client = new OcrWorkerClient(configuration, stop.Token, HostCommand());
        }));
        try
        {
            var error = await Observe(startup, children);
            Assert.IsType<OcrWorkerException>(error);
            Assert.Equal(before.StartedCount + 1, WorkerProcessMetrics.Capture().StartedCount);
            Assert.Equal(before.ActiveCount, WorkerProcessMetrics.Capture().ActiveCount);
            children.AssertExited();
            Assert.False(Directory.Exists(missingTemplates));
            Assert.Empty(Directory.GetFiles(wb.Source));
            output.WriteLine($"startupFailure=missing templates; childStarts=1; activeChildrenRestored=true; observedPids={string.Join(',', children.ProcessIds)}");
        }
        finally
        {
            stop.Cancel();
            children.Capture();
            children.StopSurvivors();
            await ObserveFailure(startup);
        }
    }

    [OcrFact]
    public async Task Cancelling_run_after_first_rename_leaves_other_pdfs_unchanged_and_reaps_both_children()
    {
        using var wb = new Workbench();
        CopyTemplates(wb.Templates);
        var sample = TrackedSamples().Single(s => s.File.Contains("02-assembly", StringComparison.Ordinal));
        const int count = 12;
        for (var i = 0; i < count; i++) CopyScan(sample, Path.Combine(wb.Source, $"scan-{i:D2}.pdf"));
        var hash = Hash(sample.Path);
        var before = WorkerProcessMetrics.Capture();
        var factories = 0;
        using var stop = new CancellationTokenSource();
        var sink = new Results(entry => { if (entry.Status == FileStatus.Renamed) stop.Cancel(); });
        using var children = new ObservedChildren();
        var run = Task.Run(() => OcrRenameRun.Execute(Settings(wb, NameTakenPolicy.AddNumber), () =>
        {
            Interlocked.Increment(ref factories);
            return OcrPair.CreateReal(OcrComponents.FindTessdata()!);
        }, sink, stop.Token));
        try
        {
            var summary = await Observe(run, children);
            Assert.Null(summary.Fatal);
            Assert.True(summary.Stopped);
            Assert.Equal(1, summary.Renamed);
            Assert.Equal(1, factories);
            Assert.Single(sink.Entries, e => e.Status == FileStatus.Renamed);
            var remaining = Directory.GetFiles(wb.Source);
            Assert.Equal(count, remaining.Length);
            Assert.Equal(count - 1, remaining.Count(p => Path.GetFileName(p).StartsWith("scan-", StringComparison.Ordinal)));
            Assert.All(remaining, file => Assert.Equal(hash, Hash(file)));
            Assert.Equal(before.StartedCount + 2, WorkerProcessMetrics.Capture().StartedCount);
            Assert.Equal(before.ActiveCount, WorkerProcessMetrics.Capture().ActiveCount);
            children.AssertExited();
        }
        finally
        {
            stop.Cancel();
            children.Capture();
            children.StopSurvivors();
            await ObserveFailure(run);
        }
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_real_child_does_not_return_fabricated_fields_restart_or_remain_alive(bool crash)
    {
        using var wb = new Workbench();
        CopyTemplates(wb.Templates);
        using var page = SyntheticForms.Render(FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус", 200, docType: "Сборочный чертеж");
        var file = wb.AddPages("long.pdf", Enumerable.Repeat(page, DocumentReader.MaxPages).ToArray(), 200);
        var hash = Hash(file);
        var before = WorkerProcessMetrics.Capture();
        using var stop = new CancellationTokenSource();
        using var client = new OcrWorkerClient(Configuration(wb.Templates), launchCommand: HostCommand());
        using var child = Process.GetProcessById(client.ProcessId);
        child.Refresh();
        var idleCpu = child.TotalProcessorTime;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ReadStarted += () => started.TrySetResult();
        var read = Task.Run(() => client.Read(file, stop.Token));
        try
        {
            // The acknowledgement establishes which request is active; CPU progress then
            // confirms real work started. There is no assumed model-startup sleep.
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var watch = Stopwatch.StartNew();
            while (true)
            {
                child.Refresh();
                if (child.TotalProcessorTime - idleCpu >= TimeSpan.FromMilliseconds(20)) break;
                Assert.False(read.IsCompleted, "The test did not interrupt an in-flight native read.");
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "OCR child did not begin processing the PDF.");
                await Task.Delay(10);
            }
            Assert.False(read.IsCompleted, "The test did not interrupt an in-flight native read.");
            if (crash)
            {
                child.Kill(entireProcessTree: true);
                await Assert.ThrowsAnyAsync<OcrWorkerException>(() => read.WaitAsync(TimeSpan.FromSeconds(15)));
                var repeated = Record.Exception(() => client.Read(file, CancellationToken.None));
                Assert.True(repeated is OcrWorkerException or ObjectDisposedException, repeated?.ToString() ?? "Dead worker returned a result.");
            }
            else
            {
                stop.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(15)));
            }
            Assert.Equal(hash, Hash(file));
            Assert.Single(Directory.GetFiles(wb.Source));
            Assert.Equal(before.StartedCount + 1, WorkerProcessMetrics.Capture().StartedCount);
            client.Dispose();
            Assert.True(child.WaitForExit(5000), "Interrupted OCR child remains alive after disposal.");
            Assert.Equal(before.ActiveCount, WorkerProcessMetrics.Capture().ActiveCount);
            output.WriteLine($"interruption={(crash ? "external process kill" : "cancellation")}; childPid={child.Id}; noRestart=true; sourceUnchanged=true");
        }
        finally
        {
            stop.Cancel();
            client.Dispose();
            Stop(child);
            await ObserveFailure(read);
        }
    }

    private static RunSettings Settings(Workbench wb, NameTakenPolicy policy) =>
        new(wb.Templates, wb.Source, Recursive: false, Workers: 2, NameTaken: policy)
        {
            UseProcessWorkers = true,
            WorkerHostOverride = HostCommand(),
        };

    private static OcrWorkerConfiguration Configuration(string templates)
    {
        OcrComponents.ConfigureRuntime();
        using var pair = OcrPair.CreateReal(OcrComponents.FindTessdata()!);
        pair.PreferKnownLines = true;
        return pair.ExportWorkerConfiguration(templates);
    }

    private static OcrWorkerLaunchCommand HostCommand()
    {
        var assembly = typeof(global::DocumentTextRecognition.Program).Assembly.Location;
        Assert.True(File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")));
        Assert.True(File.Exists(Path.ChangeExtension(assembly, ".deps.json")));
        return new OcrWorkerLaunchCommand("dotnet", [assembly]);
    }

    private static async Task<T> Observe<T>(Task<T> run, ObservedChildren children)
    {
        var watch = Stopwatch.StartNew();
        while (!run.IsCompleted)
        {
            children.Capture();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(90), "Process OCR run did not finish.");
            await Task.Delay(20);
        }
        children.Capture();
        return await run;
    }

    private static async Task ObserveFailure(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { /* Observe completion while preserving the original assertion/exception. */ }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string SamplesRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "test-samples");
            if (File.Exists(Path.Combine(path, "verification.json"))) return path;
        }
        throw new DirectoryNotFoundException("Tracked PDF samples were not found.");
    }

    private static Sample[] TrackedSamples()
    {
        var root = SamplesRoot();
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "verification.json")));
        var samples = json.RootElement.GetProperty("Samples").EnumerateArray().Select(s =>
        {
            var file = s.GetProperty("File").GetString()!;
            return new Sample(file, Path.Combine(root, file), s.GetProperty("ExpectedName").GetString()!);
        }).ToArray();
        Assert.Equal(5, samples.Length);
        return samples;
    }

    private static void CopyTemplates(string destination)
    {
        OcrComponents.ConfigureRuntime();
        foreach (var file in Directory.GetFiles(Path.Combine(SamplesRoot(), "templates")))
            if (file.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    private static void CopyScan(Sample sample, string path) => File.Copy(sample.Path, path);
    private sealed record Sample(string File, string Path, string ExpectedName);

    private sealed class Results(Action<JournalEntry>? onFile = null) : IRunSink
    {
        internal ConcurrentQueue<JournalEntry> Entries { get; } = new();
        internal ConcurrentDictionary<int, bool> FileThreads { get; } = new();
        public void Message(string text) { }
        public void Found(int count) { }
        public void File(JournalEntry entry)
        {
            FileThreads.TryAdd(Environment.CurrentManagedThreadId, true);
            Entries.Enqueue(entry);
            onFile?.Invoke(entry);
        }
    }

    private sealed class ObservedChildren : IDisposable
    {
        private readonly Dictionary<int, Process> _processes = [];
        internal IEnumerable<int> ProcessIds => _processes.Keys;
        internal void Capture()
        {
            foreach (var id in WorkerProcessMetrics.GetActiveProcessIds())
            {
                if (_processes.ContainsKey(id)) continue;
                try { _processes[id] = Process.GetProcessById(id); }
                catch (ArgumentException) { /* Already exited before inspection. */ }
            }
        }
        internal void AssertExited() => Assert.All(_processes.Values,
            process => Assert.True(process.WaitForExit(5000), $"OCR child {process.Id} remains alive."));
        internal void StopSurvivors() { foreach (var process in _processes.Values) Stop(process); }
        public void Dispose()
        {
            StopSurvivors();
            foreach (var process in _processes.Values) process.Dispose();
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
