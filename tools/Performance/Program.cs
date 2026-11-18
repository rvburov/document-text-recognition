using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

// Every timed document uses the production OcrRenameRun and both real OCR engines.
// Repeated sample bytes are disclosed in the report; there is no fake recognizer or result cache here.
var workerBootstrap = typeof(OcrPair).Assembly.GetType("DocumentTextRecognition.Core.Rename.OcrWorkerHost")?
    .GetMethod("TryRunWorker", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (workerBootstrap is not null)
{
    object?[] bootstrapArguments = [args, 0];
    if (workerBootstrap.Invoke(null, bootstrapArguments) is true) return (int)bootstrapArguments[1]!;
}
var options = Options.Parse(args);
if (options is null) return 0;
var hammingSelector = typeof(TemplateMatcher).GetProperty("UseAvx2", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
var hammingAvailable = typeof(TemplateMatcher).GetProperty("HammingAvx2Available", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
if (options.HammingAvx2 is { } useAvx2)
{
    if (useAvx2 && hammingSelector is null) throw new NotSupportedException("This core build has no AVX2 Hamming option.");
    hammingSelector?.SetValue(null, useAvx2);
}
var resolvedHammingAvx2 = hammingSelector is null ? (bool?)null :
    hammingSelector.GetValue(null) is true && hammingAvailable?.GetValue(null) is true;
var configureRuntime = typeof(OcrComponents).GetMethod("ConfigureRuntime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
configureRuntime?.Invoke(null, null);
if (options.OpenCvThreads is { } threads) Cv2.SetNumThreads(threads);
var samplesRoot = Path.GetFullPath(options.Samples);
var templateFolder = Path.Combine(samplesRoot, "templates");
var samples = ReadSamples(samplesRoot);
var datasetHash = HashDataset(samplesRoot);
var scratch = Path.Combine(Path.GetTempPath(), "dtr-perf-" + Guid.NewGuid().ToString("N")[..10]);
Directory.CreateDirectory(scratch);
var source = Path.Combine(scratch, "scans");
Directory.CreateDirectory(source);
var expectations = new Dictionary<string, Sample>();
try
{
    for (var i = 0; i < options.Count; i++)
    {
        var sample = samples[i % samples.Length];
        var slot = Path.Combine(source, i.ToString("D6"));
        Directory.CreateDirectory(slot);
        File.Copy(Path.Combine(samplesRoot, sample.File), Path.Combine(slot, "scan.pdf"));
        expectations.Add(Path.Combine(i.ToString("D6"), "scan.pdf"), sample);
    }

    var startup = Stopwatch.StartNew();
    var profile = Profile(samplesRoot, samples, templateFolder, options.ProfilePasses, options.BatchFields, options.OneDnn, options.ReusePredictors, options.KnownLines, options.Onnx, options.OnnxModels);
    startup.Stop();
    Console.WriteLine($"Warmup/profile {startup.Elapsed.TotalSeconds:F2}s; documents={options.Count}, workers={options.Workers}, OpenCV={Cv2.GetNumThreads()}");

    var process = Process.GetCurrentProcess();
    var childrenBefore = ChildResources.Capture();
    process.Refresh();
    var cpuStart = process.TotalProcessorTime;
    var runClock = Stopwatch.StartNew();
    var sink = new Sink(expectations, runClock);
    double engineInitMs = 0;
    using var sampler = new ResourceSampler(process);
    var settings = new RunSettings(templateFolder, source, Recursive: true, Workers: options.Workers);
    var processWorkersProperty = typeof(RunSettings).GetProperty("UseProcessWorkers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (options.ProcessWorkers is { } useProcesses)
    {
        if (useProcesses && processWorkersProperty is null) throw new NotSupportedException("This core build has no process worker option.");
        processWorkersProperty?.SetValue(settings, useProcesses);
    }
    var resolvedProcessWorkers = processWorkersProperty?.GetValue(settings) as bool?;
    if (resolvedProcessWorkers == true && !ChildResources.Available)
        throw new NotSupportedException("Process workers require aggregate child resource measurements.");
    var dedicatedProperty = typeof(RunSettings).GetProperty("DedicatedWorkers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (options.DedicatedWorkers is { } dedicated)
    {
        if (dedicated && dedicatedProperty is null) throw new NotSupportedException("This core build has no dedicated worker option.");
        dedicatedProperty?.SetValue(settings, dedicated);
    }
    var summary = OcrRenameRun.Execute(settings, () =>
    {
        var clock = Stopwatch.StartNew();
        var pair = CreatePair(options.BatchFields, options.OneDnn, options.ReusePredictors, options.KnownLines, options.Onnx, options.OnnxModels);
        engineInitMs += clock.Elapsed.TotalMilliseconds;
        return pair;
    }, sink, CancellationToken.None);
    runClock.Stop();
    sampler.Stop();
    process.Refresh();
    var childrenAfter = ChildResources.Capture();
    var parentCpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
    var childCpuSeconds = childrenAfter.TotalChildCpuSeconds - childrenBefore.TotalChildCpuSeconds;
    var cpuSeconds = parentCpuSeconds + childCpuSeconds;

    var errors = sink.Errors.ToList();
    var verifiedHashes = 0;
    if (summary.Fatal is not null) errors.Add(summary.Fatal);
    if (childrenAfter.ActiveCount != childrenBefore.ActiveCount) errors.Add("Worker processes remain registered after the run.");
    if (resolvedProcessWorkers == true && childrenAfter.StartedCount - childrenBefore.StartedCount != Math.Min(settings.EffectiveWorkers, options.Count))
        errors.Add("The expected number of real worker processes did not start.");
    if (summary.Found != options.Count || summary.Processed != options.Count || summary.Renamed != options.Count)
        errors.Add($"Expected {options.Count} renamed; found={summary.Found}, processed={summary.Processed}, renamed={summary.Renamed}, skipped={summary.Skipped}.");
    // Verification is outside the timed interval and checks every output PDF, not a sample.
    foreach (var (relative, sample) in expectations)
    {
        var target = Path.Combine(source, Path.GetDirectoryName(relative)!, sample.ExpectedName);
        if (!File.Exists(target)) errors.Add($"Missing output {relative}: {sample.ExpectedName}");
        else if (Sha(target) != sample.Sha256) errors.Add($"PDF bytes changed: {relative}");
        else verifiedHashes++;
    }

    var report = new
    {
        SchemaVersion = 1,
        Label = options.Label,
        RecordedUtc = DateTimeOffset.UtcNow,
        Environment = new
        {
            OS = RuntimeInformation.OSDescription,
            DotNet = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            Cpu = Environment.GetEnvironmentVariable("DTR_BENCHMARK_CPU") ?? CpuName(),
            CgroupCpuMax = ReadText("/sys/fs/cgroup/cpu.max"),
            CgroupMemoryMax = ReadText("/sys/fs/cgroup/memory.max"),
            GcAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Tesseract = TesseractEngine.Version(),
            OpenCvThreads = Cv2.GetNumThreads(),
            HammingAvx2Available = hammingAvailable?.GetValue(null) as bool?,
            OmpThreadLimit = Environment.GetEnvironmentVariable("OMP_THREAD_LIMIT"),
            OmpNumThreads = Environment.GetEnvironmentVariable("OMP_NUM_THREADS"),
            OpenBlasNumThreads = Environment.GetEnvironmentVariable("OPENBLAS_NUM_THREADS"),
            CoreAssemblySha256 = Sha(typeof(OcrPair).Assembly.Location),
            PaddleNativeLibraries = NativeLibraries(),
        },
        Dataset = new
        {
            Description = $"{options.Count} synthetic PDFs created by cycling over {samples.Length} source documents; local filesystem. Each file uses both real OCR engines. Not distinct production scans.",
            options.Count,
            UniqueDocuments = samples.Length,
            Templates = Directory.EnumerateFiles(templateFolder, "*.pdf").Count(),
            DatasetSha256 = datasetHash,
            Samples = samples,
            CopiedBytes = samples.Select((s, i) => (long)Enumerable.Range(0, options.Count).Count(n => n % samples.Length == i) * new FileInfo(Path.Combine(samplesRoot, s.File)).Length).Sum(),
        },
        Configuration = new
        {
            RequestedWorkers = options.Workers,
            EffectiveWorkers = settings.EffectiveWorkers,
            RuntimeDefaultsApplied = configureRuntime is not null,
            options.ProfilePasses, options.BatchFields, options.OneDnn, options.ReusePredictors, options.KnownLines, options.Onnx, options.OnnxModels,
            options.DedicatedWorkers,
            options.HammingAvx2,
            options.ProcessWorkers,
            ResolvedProcessWorkers = resolvedProcessWorkers,
            ResolvedHammingAvx2 = resolvedHammingAvx2,
            ResolvedDedicatedWorkers = dedicatedProperty?.GetValue(settings) as bool?,
            ResolvedBatchFields = SelectedBackend.BatchFields,
            ResolvedKnownLines = SelectedBackend.KnownLines,
            ResolvedUsesOnnx = SelectedBackend.UsesOnnx,
            ResolvedOneDnn = SelectedBackend.UsesOnnx == true ? (bool?)null : FactoryFlag(1, options.OneDnn),
            ResolvedReusePredictors = SelectedBackend.UsesOnnx == true ? (bool?)null : FactoryFlag(2, options.ReusePredictors),
            Warmup = "One untimed read of each unique document, then optional phase profiles; new real OCR engines are still initialized inside the timed full run.",
        },
        Measurement = new
        {
            WallSeconds = runClock.Elapsed.TotalSeconds,
            CpuSeconds = cpuSeconds,
            ParentCpuSeconds = parentCpuSeconds,
            ChildCpuSeconds = childCpuSeconds,
            ResourceScope = "Parent plus registered OCR child processes; working sets and threads sampled together every100ms; shared file-backed pages are counted per process, not unique physical RAM. Final child CPU uses OS/report values, or the last available sample after an exit race.",
            WorkerProcessesStarted = childrenAfter.StartedCount - childrenBefore.StartedCount,
            WorkerProcessesActiveAfterRun = childrenAfter.ActiveCount,
            RetiredWorkersWithoutFinalCpu = childrenAfter.RetiredWithoutFinalCpuCount - childrenBefore.RetiredWithoutFinalCpuCount,
            ChildResourceSampleFailures = childrenAfter.SampleFailures - childrenBefore.SampleFailures,
            SampledPeakWorkerProcesses = sampler.PeakWorkerProcesses,
            MeanCpuCoresUsed = cpuSeconds / runClock.Elapsed.TotalSeconds,
            SampledPeakWorkingSetBytes = sampler.PeakWorkingSet,
            SampledPeakThreads = sampler.PeakThreads,
            DocumentsPerSecond = options.Count / runClock.Elapsed.TotalSeconds,
            SuccessfullyRenamedPerSecond = summary.Renamed / runClock.Elapsed.TotalSeconds,
            Projected8000Minutes = 8000 / (options.Count / runClock.Elapsed.TotalSeconds) / 60,
            Measured8000Minutes = options.Count == 8000 ? runClock.Elapsed.TotalMinutes : (double?)null,
            ProjectionWarning = options.Count == 8000
                ? $"Measured 8000-document run; all files are copies of the disclosed {samples.Length} synthetic single-page samples, each processed by both real OCR engines."
                : "Linear extrapolation from repeated synthetic single-page files; not a measured 8000-document run.",
            TemplateLoadAndEnumerationMilliseconds = sink.FoundAtMilliseconds,
            EngineInitializationMilliseconds = engineInitMs,
            FirstResultMilliseconds = sink.FirstResultMilliseconds,
            WarmupAndProfileSeconds = startup.Elapsed.TotalSeconds,
            summary.Found, summary.Processed, summary.Renamed, summary.AlreadyNamed, summary.Skipped,
        },
        PhaseProfile = profile,
        Quality = new { Passed = errors.Count == 0, VerifiedPdfHashes = verifiedHashes, IncorrectNames = sink.IncorrectNames, Errors = errors },
    };
    var output = Path.GetFullPath(options.Output);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(report, JsonOptions()) + Environment.NewLine);
    Console.WriteLine($"RESULT {options.Label}: {runClock.Elapsed.TotalSeconds:F3}s, {options.Count / runClock.Elapsed.TotalSeconds:F3} PDF/s, CPU {cpuSeconds:F2}s, peak {sampler.PeakWorkingSet / 1048576.0:F0}MiB, quality={(errors.Count == 0 ? "PASS" : "FAIL")}");
    Console.WriteLine(output);
    return errors.Count == 0 ? 0 : 1;
}
finally
{
    if (!options.KeepWorkdir) Directory.Delete(scratch, true);
    else Console.WriteLine("Dataset retained: " + scratch);
}

static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

static Sample[] ReadSamples(string root)
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "verification.json")));
    var samples = json.RootElement.GetProperty("Samples").EnumerateArray()
        .Where(s => s.GetProperty("RecognitionRequired").GetBoolean())
        .Select(s => new Sample(s.GetProperty("File").GetString()!, s.GetProperty("ExpectedName").GetString()!, ""))
        .Select(s => s with { Sha256 = Sha(Path.Combine(root, s.File)) }).ToArray();
    if (samples.Length == 0) throw new InvalidOperationException("No required-positive samples in verification.json");
    return samples;
}

static object Profile(string root, Sample[] samples, string templatesFolder, int passes, bool? batchFields, bool? oneDnn, bool? reusePredictors, bool? knownLines, bool? onnx, string? onnxModels)
{
    var loaded = TemplateLibrary.LoadFolder(templatesFolder);
    if (loaded.Any(t => t.Template is null)) throw new InvalidOperationException(string.Join("; ", loaded.Select(t => t.Problem)));
    var templates = loaded.Select(t => t.Template!).ToArray();
    try
    {
        using var pair = CreatePair(batchFields, oneDnn, reusePredictors, knownLines, onnx, onnxModels);
        foreach (var sample in samples) Check(DocumentReader.Read(Path.Combine(root, sample.File), templates, pair, CancellationToken.None), sample);
        var phases = new Dictionary<string, (double Milliseconds, int Calls)>();
        Action<string, TimeSpan> timing = (key, elapsed) =>
        {
            phases.TryGetValue(key, out var previous);
            phases[key] = (previous.Milliseconds + elapsed.TotalMilliseconds, previous.Calls + 1);
        };
        var readMethod = typeof(DocumentReader).GetMethod("Read", BindingFlags.Static | BindingFlags.Public)!;
        var instrumented = readMethod.GetParameters().Length >= 6;
        var rows = new List<object>();
        for (var pass = 0; pass < passes; pass++)
            foreach (var sample in samples)
            {
                var clock = Stopwatch.StartNew();
                var result = instrumented
                    ? (DocumentResult)readMethod.Invoke(null, [Path.Combine(root, sample.File), templates, pair, CancellationToken.None, null, timing])!
                    : DocumentReader.Read(Path.Combine(root, sample.File), templates, pair, CancellationToken.None);
                clock.Stop();
                Check(result, sample);
                rows.Add(new { Pass = pass + 1, sample.File, WallMilliseconds = clock.Elapsed.TotalMilliseconds });
            }
        return new
        {
            Instrumented = instrumented,
            Documents = samples.Length * passes,
            Scope = "Sequential warmed DocumentReader reads with both real engines; timing callbacks enabled only for these profile reads, not full throughput run.",
            Stages = phases.OrderByDescending(p => p.Value.Milliseconds).Select(p => new
            {
                Stage = p.Key, p.Value.Calls, TotalMilliseconds = p.Value.Milliseconds,
                MeanMillisecondsPerDocument = p.Value.Milliseconds / (samples.Length * Math.Max(1, passes)),
            }).ToArray(),
            Reads = rows,
        };
    }
    finally { foreach (var t in templates) t.Dispose(); }

    static void Check(DocumentResult result, Sample sample)
    {
        if (result.Values is null) throw new InvalidOperationException($"Warm/profile OCR failed {sample.File}: {result.Problem}");
        var built = FileNames.Build(result.Values);
        if (built.Name != sample.ExpectedName) throw new InvalidOperationException($"Warm/profile incorrect name {sample.File}: {built.Name}");
    }
}

static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
static bool FactoryFlag(int parameter, bool? requested)
{
    if (requested.HasValue) return requested.Value;
    var parameters = typeof(OcrPair).GetMethod("CreateReal", BindingFlags.Static | BindingFlags.Public)!.GetParameters();
    return parameter < parameters.Length && parameters[parameter].DefaultValue is true;
}
static OcrPair CreatePair(bool? batchFields, bool? oneDnn, bool? reusePredictors, bool? knownLines, bool? onnx, string? onnxModels)
{
    var tessdata = OcrComponents.FindTessdata() ?? throw new InvalidOperationException("tessdata missing");
    var factory = typeof(OcrPair).GetMethod("CreateReal", BindingFlags.Static | BindingFlags.Public)!;
    var parameters = factory.GetParameters();
    var parameterCount = parameters.Length;
    if (oneDnn == true && parameterCount < 2) throw new NotSupportedException("This core build has no oneDNN option.");
    if (reusePredictors == true && parameterCount < 3) throw new NotSupportedException("This core build has no persistent predictor option.");
    if (onnx == true && parameterCount < 4) throw new NotSupportedException("This core build has no ONNX option.");
    if (onnxModels is not null && parameterCount < 5) throw new NotSupportedException("This core build has no ONNX model path option.");
    // Reflection does not apply optional defaults automatically. Fill every declared
    // parameter so the harness also works with older one-/two-/three-argument factories.
    var arguments = parameters.Select(p => p.HasDefaultValue ? p.DefaultValue : null).ToArray();
    arguments[0] = tessdata;
    if (parameterCount > 1) arguments[1] = FactoryFlag(1, oneDnn);
    if (parameterCount > 2) arguments[2] = FactoryFlag(2, reusePredictors);
    if (parameterCount > 3) arguments[3] = FactoryFlag(3, onnx);
    if (parameterCount > 4) arguments[4] = onnxModels;
    var pair = (OcrPair)factory.Invoke(null, arguments)!;
    if (batchFields is { } enabled)
    {
        var property = typeof(OcrPair).GetProperty("BatchFields", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null && enabled)
        {
            pair.Dispose();
            throw new NotSupportedException("This core build does not support batched fields.");
        }
        property?.SetValue(pair, enabled);
    }
    var knownLinesProperty = typeof(OcrPair).GetProperty("PreferKnownLines", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    if (knownLines is { } preferKnownLines)
    {
        if (knownLinesProperty is null && preferKnownLines)
        {
            pair.Dispose();
            throw new NotSupportedException("This core build does not support known text lines.");
        }
        knownLinesProperty?.SetValue(pair, preferKnownLines);
    }
    SelectedBackend.BatchFields = typeof(OcrPair).GetProperty("BatchFields", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pair) as bool?;
    SelectedBackend.KnownLines = knownLinesProperty?.GetValue(pair) as bool?;
    SelectedBackend.UsesOnnx = pair.Second.GetType().GetProperty("UsesOnnx", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(pair.Second) as bool?;
    return pair;
}
static string HashDataset(string root) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => f.EndsWith(".pdf") || f.EndsWith(".template.json"))
        .OrderBy(f => f, StringComparer.Ordinal).Select(f => Path.GetRelativePath(root, f) + ":" + Sha(f))))));
static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
static string? CpuName() => File.Exists("/proc/cpuinfo")
    ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name"))?.Split(':', 2).Last().Trim() : null;
static object[] NativeLibraries()
{
    var names = OperatingSystem.IsWindows() ? new[] { "paddle_inference_c.dll", "onnxruntime.dll" } : new[] { "libpaddle_inference_c.so", "libonnxruntime.so" };
    return Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
        .Where(p => names.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
        .Select(p => (object)new { File = Path.GetRelativePath(AppContext.BaseDirectory, p), Sha256 = Sha(p), Bytes = new FileInfo(p).Length })
        .ToArray();
}

internal sealed record Sample(string File, string ExpectedName, string Sha256);

internal static class SelectedBackend
{
    public static bool? BatchFields { get; set; }
    public static bool? KnownLines { get; set; }
    public static bool? UsesOnnx { get; set; }
}

internal sealed class Sink(IReadOnlyDictionary<string, Sample> expected, Stopwatch clock) : IRunSink
{
    public readonly ConcurrentQueue<string> Errors = new();
    public int IncorrectNames { get; private set; }
    public double FoundAtMilliseconds { get; private set; }
    public double? FirstResultMilliseconds { get; private set; }
    private int _processed;
    private int _failed;
    public void Message(string text) { }
    public void Found(int count) => FoundAtMilliseconds = clock.Elapsed.TotalMilliseconds;
    public void File(JournalEntry entry)
    {
        FirstResultMilliseconds ??= clock.Elapsed.TotalMilliseconds;
        if (!expected.TryGetValue(entry.RelativePath, out var sample)) Errors.Enqueue("Unexpected source " + entry.RelativePath);
        else if (entry.Status != FileStatus.Renamed || entry.NewName != sample.ExpectedName)
        {
            var failure = $"{entry.RelativePath}: {entry.Status}; {entry.NewName}; {entry.Reason}";
            Errors.Enqueue(failure);
            if (++_failed <= 10 || _failed % 100 == 0)
                Console.Error.WriteLine($"FAILURE {_failed}, elapsed {clock.Elapsed.TotalSeconds:F1}s: {failure}");
            if (entry.NewName is not null && entry.NewName != sample.ExpectedName) IncorrectNames++;
        }
        if (++_processed % Math.Max(10, expected.Count / 40) == 0)
            Console.WriteLine($"processed {_processed}/{expected.Count}, failures {_failed}, elapsed {clock.Elapsed.TotalSeconds:F1}s");
    }
}

internal sealed class ResourceSampler : IDisposable
{
    private readonly Process _process;
    private readonly Timer _timer;
    private readonly object _gate = new();
    public long PeakWorkingSet { get; private set; }
    public int PeakThreads { get; private set; }
    public int PeakWorkerProcesses { get; private set; }
    public ResourceSampler(Process process)
    {
        _process = process;
        Sample();
        _timer = new Timer(_ => Sample(), null, 100, 100);
    }
    private void Sample()
    {
        lock (_gate)
        {
            _process.Refresh();
            var children = ChildResources.Capture();
            PeakWorkingSet = Math.Max(PeakWorkingSet, _process.WorkingSet64 + children.CurrentWorkingSetBytes);
            PeakThreads = Math.Max(PeakThreads, _process.Threads.Count + children.CurrentThreads);
            PeakWorkerProcesses = Math.Max(PeakWorkerProcesses, children.ActiveCount);
        }
    }
    public void Stop()
    {
        using var complete = new ManualResetEvent(false);
        _timer.Dispose(complete);
        complete.WaitOne();
        Sample();
    }
    public void Dispose() => _timer.Dispose();
}

internal sealed record ChildResources(double TotalChildCpuSeconds, long CurrentWorkingSetBytes, int CurrentThreads,
    int ActiveCount, long StartedCount, long RetiredWithoutFinalCpuCount, long SampleFailures)
{
    private static readonly MethodInfo? CaptureMethod = typeof(OcrPair).Assembly
        .GetType("DocumentTextRecognition.Core.Rename.WorkerProcessMetrics")?
        .GetMethod("Capture", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
    internal static bool Available => CaptureMethod is not null;
    internal static ChildResources Capture()
    {
        var value = CaptureMethod?.Invoke(null, null);
        if (value is null) return new(0, 0, 0, 0, 0, 0, 0);
        object? Property(string name) => value.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
        return new(Convert.ToDouble(Property("TotalChildCpuSeconds")), Convert.ToInt64(Property("CurrentWorkingSetBytes")),
            Convert.ToInt32(Property("CurrentThreads")), Convert.ToInt32(Property("ActiveCount")),
            Convert.ToInt64(Property("StartedCount")), Convert.ToInt64(Property("RetiredWithoutFinalCpuCount")),
            Convert.ToInt64(Property("SampleFailures")));
    }
}

internal sealed record Options(string Samples, int Count, int Workers, int? OpenCvThreads, int ProfilePasses, bool? BatchFields, bool? OneDnn, bool? ReusePredictors,
    bool? KnownLines, bool? Onnx, string? OnnxModels, bool? DedicatedWorkers, bool? HammingAvx2, bool? ProcessWorkers, string Output, string Label, bool KeepWorkdir)
{
    public static Options? Parse(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("dotnet run --project tools/Performance -c Release -- --count 100 --workers 4 --opencv-threads 1 --profile-passes 2 --batch-fields false --label candidate --output artifacts/performance/candidate.json [--samples docs/test-samples] [--known-lines true|false] [--onnx true|false --onnx-models directory] [--hamming-avx2 true|false] [--process-workers true|false] [--keep-workdir]");
            return null;
        }
        var values = new Dictionary<string, string>();
        bool keep = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--keep-workdir") { keep = true; continue; }
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) throw new ArgumentException("Expected --option value");
            if (args[i] is not ("--samples" or "--count" or "--workers" or "--opencv-threads" or "--profile-passes" or "--batch-fields" or "--one-dnn" or "--reuse-predictors" or "--known-lines" or "--onnx" or "--onnx-models" or "--dedicated-workers" or "--hamming-avx2" or "--process-workers" or "--output" or "--label"))
                throw new ArgumentException("Unknown option " + args[i]);
            values[args[i][2..]] = args[++i];
        }
        if (values.ContainsKey("onnx-models") && (!values.TryGetValue("onnx", out var selected) || !bool.Parse(selected)))
            throw new ArgumentException("--onnx-models requires --onnx true.");
        int Number(string key, int fallback, int min, int max)
        {
            var n = values.TryGetValue(key, out var s) ? int.Parse(s) : fallback;
            return n >= min && n <= max ? n : throw new ArgumentOutOfRangeException(key);
        }
        return new(values.GetValueOrDefault("samples", "docs/test-samples"), Number("count", 100, 1, 100000),
            Number("workers", 4, 0, 64), values.ContainsKey("opencv-threads") ? Number("opencv-threads", 1, 1, 64) : null,
            Number("profile-passes", 1, 0, 20), values.TryGetValue("batch-fields", out var batch) ? bool.Parse(batch) : null,
            values.TryGetValue("one-dnn", out var oneDnn) ? bool.Parse(oneDnn) : null,
            values.TryGetValue("reuse-predictors", out var reuse) ? bool.Parse(reuse) : null,
            values.TryGetValue("known-lines", out var known) ? bool.Parse(known) : null,
            values.TryGetValue("onnx", out var onnx) ? bool.Parse(onnx) : null,
            values.GetValueOrDefault("onnx-models"),
            values.TryGetValue("dedicated-workers", out var dedicated) ? bool.Parse(dedicated) : null,
            values.TryGetValue("hamming-avx2", out var avx2) ? bool.Parse(avx2) : null,
            values.TryGetValue("process-workers", out var processWorkers) ? bool.Parse(processWorkers) : null,
            values.GetValueOrDefault("output", "artifacts/performance/result.json"),
            values.GetValueOrDefault("label", "working-tree"), keep);
    }
}
