using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

return RunBenchmark(args);

static int RunBenchmark(string[] args)
{
    try
    {
        var options = Options.Parse(args);
        if (options is null) return 0;
        OcrComponents.ConfigureRuntime();
        Cv2.SetNumThreads(1);
        var root = Path.GetFullPath(options.Samples);
        var loaded = TemplateLibrary.LoadFolder(Path.Combine(root, "templates"));
        var templates = loaded.Where(t => t.Template is not null).Select(t => t.Template!).ToArray();
        var pages = new List<(string File, PageFeatures Features)>();
        try
        {
            if (loaded.Count == 0 || loaded.Any(t => t.Template is null))
                throw new InvalidOperationException("Templates failed: " + string.Join("; ", loaded.Select(t => t.Problem)));
            using var verification = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "verification.json")));
            var samples = verification.RootElement.GetProperty("Samples").EnumerateArray()
                .Where(s => s.GetProperty("RecognitionRequired").GetBoolean())
                .Select(s => s.GetProperty("File").GetString()!).ToArray();
            if (samples.Length == 0) throw new InvalidOperationException("No required-positive PDF samples.");
            foreach (var sample in samples)
            {
                using var pdf = PdfDocument.Open(Path.Combine(root, sample));
                var size = pdf.GetPageSize(0);
                using var render = pdf.RenderPage(0, PageFeatures.LongSide);
                var gray = PageImage.ToGray(render);
                pages.Add((sample, PageFeatures.Build(gray, gray.Width / size.Width)));
            }
            var cases = pages.SelectMany(page => templates.Select(template => new DescriptorCase(
                page.File, template.Name, page.Features.Descriptors, template.Features.Descriptors))).ToArray();
            var quality = new Quality();
            var measurements = new List<Measurement>();
            using var process = Process.GetCurrentProcess();
            foreach (var threads in options.NativeThreads)
            {
                Cv2.SetNumThreads(threads);
                using var bf = new BFMatcher(NormTypes.Hamming);
                foreach (var c in cases) Verify(c.Page + " -> " + c.Template, c.Query, c.Train, bf, quality);
                VerifyAdversarial(bf, quality);
                if (!quality.Passed || options.QualityOnly) continue;
                for (var warmup = 0; warmup < options.Warmups; warmup++)
                {
                    _ = Measure(true, threads, -1, -1, cases, bf, process);
                    _ = Measure(false, threads, -1, -1, cases, bf, process);
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                // Two invocations of each implementation per block, with symmetric order.
                for (var block = 0; block < options.Repeats / 2; block++)
                {
                    var order = new[] { true, false, false, true }; // scalar, native, native, scalar
                    for (var sequence = 0; sequence < order.Length; sequence++)
                        measurements.Add(Measure(order[sequence], threads, block, sequence, cases, bf, process));
                }
                Console.WriteLine($"OpenCV threads={threads}; quality={quality.Passed}; runs/method={options.Repeats}");
            }
            var summaries = measurements.GroupBy(m => (m.Method, m.NativeThreads)).Select(group => new
            {
                group.Key.Method, group.Key.NativeThreads, Runs = group.Count(),
                MeanWallMilliseconds = group.Average(m => m.WallMilliseconds),
                MedianWallMilliseconds = Median(group.Select(m => m.WallMilliseconds)),
                MinWallMilliseconds = group.Min(m => m.WallMilliseconds),
                MaxWallMilliseconds = group.Max(m => m.WallMilliseconds),
                MeanCpuMilliseconds = group.Average(m => m.CpuMilliseconds),
                MeanCpuCores = group.Sum(m => m.CpuMilliseconds) / group.Sum(m => m.WallMilliseconds),
                MeanAllocatedBytes = group.Average(m => (double)m.AllocatedBytes),
                PerPair = cases.Select((c, i) => new
                {
                    c.Page, c.Template, MeanWallMilliseconds = group.Average(m => m.PairWallMilliseconds[i]),
                }).ToArray(),
            }).ToArray();
            var report = new
            {
                SchemaVersion = 1, options.Label, RecordedUtc = DateTimeOffset.UtcNow,
                Scope = "Exact Hamming nearest-two stage only. PDFs/templates/ORB are prepared once. No OCR, renaming or document-throughput measurement.",
                Environment = new
                {
                    OS = RuntimeInformation.OSDescription, DotNet = RuntimeInformation.FrameworkDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    LogicalProcessors = Environment.ProcessorCount, Cpu = CpuBrand(), Vendor = CpuVendor(),
                    CpuSignature = X86Base.IsSupported ? $"0x{X86Base.CpuId(1, 0).Item1:X8}" : null,
                    Popcnt64 = Popcnt.X64.IsSupported, Avx2 = Avx2.IsSupported,
                    Avx512F = Avx512F.IsSupported, Avx512BW = Avx512BW.IsSupported,
                    OpenCv = Cv2.GetVersionString(),
                    OpenCvBuild = Cv2.GetBuildInformation().Split('\n').Where(line =>
                        line.Contains("Baseline:") || line.Contains("Dispatched code generation:") ||
                        line.Contains("Parallel framework:") || line.Contains("Intel IPP:")).Select(line => line.Trim()).ToArray(),
                    CoreAssemblySha256 = FileHash(typeof(TemplateMatcher).Assembly.Location),
                    HardwareIntrinsicOverride = System.Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic"),
                    TieredCompilationOverride = System.Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"),
                },
                Configuration = new
                {
                    options.Repeats, options.Warmups, options.NativeThreads, options.QualityOnly,
                    Order = "ABBA; A=production NearestTwo, B=native BFMatcher.KnnMatch(k=2)",
                    NativeMatcherLifetime = "One BFMatcher reused per native-thread variant; explicit query/train matrices on every call.",
                    ScalarThreading = "Single managed thread per invocation; production hardware/descriptor fallback remains enabled.",
                    SetupExcluded = "PDF rendering, ORB, descriptor/file hashes, full-result verification and warmups.",
                },
                Dataset = new
                {
                    Pdfs = pages.Count, Templates = templates.Length, PairsPerRun = cases.Length,
                    DistanceComparisonsPerRun = cases.Sum(c => (long)c.Query.Rows * c.Train.Rows),
                    Files = samples.Concat(loaded.Select(t => Path.GetRelativePath(root, t.FilePath)))
                        .Concat(loaded.Select(t => Path.GetRelativePath(root, t.FilePath + ".template.json"))
                            .Where(file => File.Exists(Path.Combine(root, file))))
                        .Order(StringComparer.Ordinal).Select(file => new
                        {
                            File = file.Replace('\\', '/'), Sha256 = FileHash(Path.Combine(root, file)),
                        }).ToArray(),
                    Descriptors = cases.Select(c => new
                    {
                        c.Page, c.Template, QueryRows = c.Query.Rows, TrainRows = c.Train.Rows,
                        WidthBytes = c.Query.Cols, QuerySha256 = DescriptorHash(c.Query), TrainSha256 = DescriptorHash(c.Train),
                    }).ToArray(),
                },
                Quality = quality, Summaries = summaries, Measurements = measurements,
            };
            var output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + System.Environment.NewLine);
            Console.WriteLine($"Quality={(quality.Passed ? "PASS" : "FAIL")}; compared rows={quality.ComparedQueryRows}; report={output}");
            foreach (var s in summaries)
                Console.WriteLine($"{s.Method}, native threads={s.NativeThreads}: wall {s.MeanWallMilliseconds:F3}ms; CPU {s.MeanCpuMilliseconds:F3}ms");
            return quality.Passed ? 0 : 2;
        }
        finally
        {
            foreach (var (_, features) in pages) features.Dispose();
            foreach (var template in templates) template.Dispose();
            Cv2.SetNumThreads(1);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

static Measurement Measure(bool scalar, int threads, int block, int sequence, DescriptorCase[] cases,
    BFMatcher bf, Process process)
{
    var pairTimes = new double[cases.Length];
    var collections = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
    process.Refresh();
    var cpu = process.TotalProcessorTime;
    var allocated = GC.GetAllocatedBytesForCurrentThread();
    long checksum = 0;
    var started = Stopwatch.GetTimestamp();
    for (var i = 0; i < cases.Length; i++)
    {
        var c = cases[i];
        var pairStarted = Stopwatch.GetTimestamp();
        if (scalar)
        {
            var result = TemplateMatcher.NearestTwo(c.Query, c.Train);
            if (result.Length > 0) checksum += result[0].FirstIndex + result[0].FirstDistance + result[0].SecondIndex + result[0].SecondDistance;
            GC.KeepAlive(result);
        }
        else
        {
            var result = bf.KnnMatch(c.Query, c.Train, 2);
            if (result.Length > 0 && result[0].Length == 2)
                checksum += result[0][0].TrainIdx + (int)result[0][0].Distance + result[0][1].TrainIdx + (int)result[0][1].Distance;
            GC.KeepAlive(result);
        }
        pairTimes[i] = Stopwatch.GetElapsedTime(pairStarted).TotalMilliseconds;
    }
    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
    process.Refresh();
    var cpuElapsed = (process.TotalProcessorTime - cpu).TotalMilliseconds;
    return new Measurement(scalar ? "production-nearest-two" : "native-bf-knn", threads, block, sequence,
        elapsed, cpuElapsed, bytes, [GC.CollectionCount(0) - collections[0], GC.CollectionCount(1) - collections[1],
            GC.CollectionCount(2) - collections[2]], checksum, pairTimes);
}

static void Verify(string name, Mat query, Mat train, BFMatcher bf, Quality quality)
{
    var expected = bf.KnnMatch(query, train, 2);
    var actual = TemplateMatcher.NearestTwo(query, train);
    var rows = query.Rows;
    quality.Cases++;
    if (actual.Length != rows) quality.Fail(name + ": incorrect query row count");
    for (var row = 0; row < rows; row++)
    {
        quality.ComparedQueryRows++;
        var pair = row < expected.Length ? expected[row] : [];
        var reference = new TemplateMatcher.NearestDescriptor(pair.Length > 0 ? pair[0].TrainIdx : -1,
            pair.Length > 0 ? (int)pair[0].Distance : int.MaxValue,
            pair.Length > 1 ? pair[1].TrainIdx : -1, pair.Length > 1 ? (int)pair[1].Distance : int.MaxValue);
        if (row >= actual.Length || reference != actual[row])
            quality.Fail($"{name}: row {row}; BF={reference}; actual={(row < actual.Length ? actual[row].ToString() : "missing")}");
    }
}

static void VerifyAdversarial(BFMatcher bf, Quality quality)
{
    using var query = new Mat(5, 32, MatType.CV_8UC1, Scalar.All(0));
    using var identical = new Mat(7, 32, MatType.CV_8UC1, Scalar.All(0));
    Verify("all-identical/tied-zero-distance", query, identical, bf, quality);
    using var tied = new Mat(7, 32, MatType.CV_8UC1, Scalar.All(255));
    foreach (var row in new[] { 0, 1, 2, 5 })
    {
        var line = new byte[32];
        line[0] = (byte)(row is 0 or 5 ? 1 : 2);
        Marshal.Copy(line, 0, tied.Data + checked((int)(row * tied.Step())), line.Length);
    }
    Verify("equal-distance/interleaved-duplicate-groups", query, tied, bf, quality);
    using var single = new Mat(1, 32, MatType.CV_8UC1, Scalar.All(255));
    Verify("one-training-row/maximal-distance", query, single, bf, quality);
    using var queryStorage = new Mat(11, 40, MatType.CV_8UC1);
    using var trainStorage = new Mat(17, 40, MatType.CV_8UC1);
    var random = new Random(7123);
    foreach (var storage in new[] { queryStorage, trainStorage })
    {
        var data = new byte[storage.Rows * storage.Cols];
        random.NextBytes(data);
        Marshal.Copy(data, 0, storage.Data, data.Length);
    }
    using var stridedQuery = new Mat(queryStorage, new Rect(3, 0, 32, 11));
    using var stridedTrain = new Mat(trainStorage, new Rect(3, 0, 32, 17));
    Verify("unaligned/strided-rows", stridedQuery, stridedTrain, bf, quality);
}

static string DescriptorHash(Mat matrix)
{
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var row = new byte[matrix.Cols];
    var rows = matrix.Rows;
    var step = matrix.Step();
    for (var i = 0; i < rows; i++)
    {
        Marshal.Copy(matrix.Data + checked((int)(i * step)), row, 0, row.Length);
        hash.AppendData(row);
    }
    return Convert.ToHexString(hash.GetHashAndReset());
}

static string FileHash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2; }
static string? CpuBrand()
{
    if (!X86Base.IsSupported || (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Item1 < 0x80000004) return null;
    var data = new byte[48];
    for (var leaf = 0; leaf < 3; leaf++)
    {
        var registers = X86Base.CpuId(unchecked((int)(0x80000002u + leaf)), 0);
        var words = new[] { registers.Item1, registers.Item2, registers.Item3, registers.Item4 };
        for (var word = 0; word < 4; word++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(leaf * 16 + word * 4, 4), words[word]);
    }
    return Encoding.ASCII.GetString(data).Trim('\0', ' ');
}
static string? CpuVendor()
{
    if (!X86Base.IsSupported) return null;
    var registers = X86Base.CpuId(0, 0);
    var data = new byte[12];
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), registers.Item2);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), registers.Item4);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), registers.Item3);
    return Encoding.ASCII.GetString(data);
}

internal sealed record DescriptorCase(string Page, string Template, Mat Query, Mat Train);
internal sealed record Measurement(string Method, int NativeThreads, int Block, int Sequence,
    double WallMilliseconds, double CpuMilliseconds, long AllocatedBytes, int[] GarbageCollections,
    long Checksum, double[] PairWallMilliseconds);
internal sealed class Quality
{
    public bool Passed => Mismatches == 0;
    public int Cases { get; set; }
    public long ComparedQueryRows { get; set; }
    public long Mismatches { get; private set; }
    public List<string> Errors { get; } = [];
    public void Fail(string error) { Mismatches++; if (Errors.Count < 20) Errors.Add(error); }
}
internal sealed record Options(string Samples, int Repeats, int Warmups, int[] NativeThreads, string Label, string Output, bool QualityOnly)
{
    public static Options? Parse(string[] args)
    {
        string samples = "docs/test-samples", label = "exact-matching", output = "artifacts/performance/exact-matching.json";
        int repeats = 20, warmups = 2;
        int[] threads = [1];
        bool qualityOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--help" or "-h")
            {
                Console.WriteLine("--samples DIR --repeats 20 --warmups 2 --native-threads 1[,2,4] --label NAME --output FILE [--quality-only]");
                return null;
            }
            if (args[i] == "--quality-only") { qualityOnly = true; continue; }
            if (i + 1 >= args.Length) throw new ArgumentException("Missing value: " + args[i]);
            var option = args[i++];
            var value = args[i];
            switch (option)
            {
                case "--samples": samples = value; break;
                case "--label": label = value; break;
                case "--output": output = value; break;
                case "--repeats": repeats = int.Parse(value); break;
                case "--warmups": warmups = int.Parse(value); break;
                case "--native-threads": threads = value.Split(',').Select(int.Parse).Distinct().ToArray(); break;
                default: throw new ArgumentException("Unknown argument: " + option);
            }
        }
        if (repeats <= 0 || repeats % 2 != 0 || warmups < 1 || threads.Length == 0 || threads.Any(n => n <= 0))
            throw new ArgumentException("Repeats must be positive and even; warmups and native-thread counts must be positive.");
        return new Options(samples, repeats, warmups, threads, label, output, qualityOnly);
    }
}
