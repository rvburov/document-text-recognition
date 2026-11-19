using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Ocr;
using DocumentTextRecognition.Tests.Samples;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.RealDocuments;

/// <summary>
/// Регрессия на настоящих документах ЕСКД: исходный PDF и его растровые «сканы» читаются тем же
/// кодом, что и в программе (шаблон с сохраненными областями → совмещение → два OCR → имя).
/// <list type="bullet">
/// <item>принятое неверное значение — всегда провал: это главное требование программы;</item>
/// <item>при <c>DTR_REAL_DOCS_BASELINE=enforce</c> вариант, который в базовой линии читался
/// точно, а теперь получил отказ, — провал (регрессия). Так проверки идут на Linux, где базовая
/// линия записана; на других системах расхождения только попадают в отчет;</item>
/// <item>стал читаться точно — тест подсказывает обновить базовую линию в manifest.json.</item>
/// </list>
/// Отчет с ответами каждого движка по каждой графе и вырезками пишется в <c>DTR_REAL_DOCS_OUTPUT</c>.
/// <c>DTR_REAL_DOCS_ONLY</c> — список id (начала id) через запятую для быстрого прогона части корпуса.
/// </summary>
[Collection("OCR")]
public sealed class RealDocumentTests(OcrFixture ocr, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal enum Outcome { Exact, Refused, Wrong }

    [OcrFact]
    public void Real_eskd_documents_are_read_exactly_or_refused_never_wrong()
    {
        var corpora = RealCorpus.Discover();
        Assert.NotEmpty(corpora);
        var enforceBaseline = Environment.GetEnvironmentVariable("DTR_REAL_DOCS_BASELINE") == "enforce";
        var only = (Environment.GetEnvironmentVariable("DTR_REAL_DOCS_ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outputRoot = Environment.GetEnvironmentVariable("DTR_REAL_DOCS_OUTPUT");
        var failures = new List<string>();
        var regressions = new List<string>();
        var improvements = new List<string>();
        var reports = new List<object>();
        var read = 0;

        foreach (var corpus in corpora)
        {
            using var temp = new TempDir("real");
            var rows = new List<object>();
            var missing = new List<object>();
            var stats = new SortedDictionary<string, int>();
            var engines = new EngineStats();
            var suggested = new SortedDictionary<string, Dictionary<string, RealBaseline>>();
            foreach (var document in corpus.Documents)
            {
                if (only.Length > 0 && !only.Any(p => document.Id.StartsWith(p, StringComparison.Ordinal))) continue;
                var template = document.Template is null ? document : corpus.Documents.Single(d => d.Id == document.Template);
                var problem = corpus.Ensure(document) ?? (template == document ? null : corpus.Ensure(template));
                if (problem is not null)
                {
                    missing.Add(new { Document = document.Id, Problem = problem });
                    output.WriteLine($"{document.Id,-52} пропущен: {problem}");
                    continue;
                }
                var templates = PrepareTemplate(corpus, template, Path.Combine(temp.Path, "Шаблоны", document.Id));
                try
                {
                    foreach (var variant in RealVariant.All)
                    {
                        var key = $"{document.Id}/{variant.Key}";
                        var file = PrepareDocument(corpus, document, variant, Path.Combine(temp.Path, "Документы"));
                        var original = SHA256.HashData(File.ReadAllBytes(file));
                        var diagnostics = new List<RecognitionDiagnostic>();
                        var result = DocumentReader.Read(file, templates, ocr.Pair, CancellationToken.None, diagnostics.Add);
                        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(file)));
                        read++;

                        var expected = new FieldValues(document.Expected.Designation, document.Expected.Name, document.Expected.Type);
                        var outcome = result.Values is null ? Outcome.Refused
                            : document.MustRefuse is null && result.Values == expected ? Outcome.Exact : Outcome.Wrong;
                        stats[$"{variant.Key}.{outcome}"] = stats.GetValueOrDefault($"{variant.Key}.{outcome}") + 1;
                        var baseline = document.Baseline.TryGetValue(variant.Key, out var b) ? b : (RealBaseline?)null;
                        if (outcome == Outcome.Exact)
                            (suggested.TryGetValue(document.Id, out var s) ? s : suggested[document.Id] = [])[variant.Key] = RealBaseline.Exact;

                        if (outcome == Outcome.Wrong)
                            failures.Add($"{key}: принято НЕВЕРНОЕ значение «{Describe(result.Values!)}», эталон «{Describe(expected)}»"
                                + (document.MustRefuse is null ? "" : $" (верного имени нет: {document.MustRefuse})"));
                        else if (outcome == Outcome.Refused && baseline == RealBaseline.Exact)
                            regressions.Add($"{key}: раньше читалось точно, теперь отказ: {result.Problem}");
                        else if (outcome == Outcome.Exact && baseline != RealBaseline.Exact)
                            improvements.Add($"{key}: теперь читается точно");
                        if (outcome == Outcome.Exact && FileNames.Build(result.Values!).Problem is { } nameProblem)
                            failures.Add($"{key}: имя файла не собрано: {nameProblem}");

                        foreach (var field in diagnostics.Where(d => d.Field is not null).Select(d => d.Field!))
                            engines.Add(variant.Key, field, expected, document.MustRefuse is null);
                        var crops = SaveCrops(outputRoot, corpus, key, diagnostics);
                        rows.Add(new
                        {
                            Document = document.Id,
                            Variant = variant.Key,
                            Baseline = baseline,
                            Outcome = outcome,
                            Expected = expected,
                            Actual = result.Values,
                            result.Problem,
                            document.MustRefuse,
                            Fields = diagnostics.Where(d => d.Field is not null).Select((d, i) => new
                            {
                                d.Stage,
                                d.Field!.Role,
                                Crop = crops.ElementAtOrDefault(i),
                                d.Field.First,
                                d.Field.Second,
                            }).ToArray(),
                            Stages = diagnostics.Where(d => d.Field is null).Select(d => $"{d.Stage}: {d.Message}").ToArray(),
                        });
                        output.WriteLine($"{key,-65} {outcome,-8} {(result.Values is null ? result.Problem : Describe(result.Values))}");
                    }
                }
                finally
                {
                    foreach (var t in templates) t.Dispose();
                }
            }

            foreach (var line in stats) output.WriteLine($"{corpus.Title}: {line.Key} = {line.Value}");
            foreach (var line in engines.Lines()) output.WriteLine(line);
            reports.Add(new
            {
                Corpus = corpus.Title,
                Documents = corpus.Documents.Count,
                Missing = missing,
                Variants = RealVariant.All.Select(v => v.Key).ToArray(),
                Summary = stats,
                EngineFieldAccuracy = engines.Table(),
                SuggestedBaseline = suggested,
                Results = rows,
            });
        }

        foreach (var line in improvements) output.WriteLine("Улучшение (обновите baseline в manifest.json): " + line);
        foreach (var line in regressions) output.WriteLine("Регрессия: " + line);
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(Path.Combine(outputRoot, "real-documents-report.json"), JsonSerializer.Serialize(new
            {
                Test = nameof(Real_eskd_documents_are_read_exactly_or_refused_never_wrong),
                VerifiedUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    DotNet = RuntimeInformation.FrameworkDescription,
                    Tesseract = TesseractEngine.Version(),
                    Engines = new[] { ocr.Pair.First.Name, ocr.Pair.Second.Name },
                    BaselineEnforced = enforceBaseline,
                },
                Failures = failures,
                Regressions = regressions,
                Improvements = improvements,
                Corpora = reports,
            }, ReportJson));
        }
        Assert.True(read > 0, "ни один документ корпуса не прочитан");
        if (enforceBaseline) failures.AddRange(regressions.Select(r => "регрессия: " + r));
        Assert.True(failures.Count == 0, "Реальные документы:\n" + string.Join("\n", failures));
    }

    private static List<DocTemplate> PrepareTemplate(RealCorpus corpus, RealDocument template, string folder)
    {
        Directory.CreateDirectory(folder);
        // Разметка создается заново рядом с копией: отпечаток PDF всегда совпадает.
        var path = Path.Combine(folder, template.Id + ".pdf");
        File.Copy(corpus.PathOf(template), path, overwrite: true);
        var problem = TemplateMarkupStore.Save(path, template.Page, template.Regions!, template.TypeAbsent);
        Assert.True(problem is null, $"{template.Id}: {problem}");
        var loads = TemplateLibrary.LoadFolder(folder);
        var broken = loads.Where(l => l.Template is null).Select(l => $"{l.FileName}: {l.Problem}").ToArray();
        Assert.True(broken.Length == 0, string.Join("; ", broken));
        return loads.Select(l => l.Template!).ToList();
    }

    private static string PrepareDocument(RealCorpus corpus, RealDocument document, RealVariant variant, string folder)
    {
        Directory.CreateDirectory(folder);
        // Нейтральное имя: эталон нельзя получить из имени файла.
        var target = Path.Combine(folder, $"doc-{StableHash(document.Id):x8}-{variant.Key}.pdf");
        if (variant == RealVariant.Vector)
            File.Copy(corpus.PathOf(document), target, overwrite: true);
        else
            variant.Rasterize(corpus.PathOf(document), document.Page, target, StableHash(document.Id + variant.Key));
        return target;
    }

    private static int StableHash(string text) =>
        BitConverter.ToInt32(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)), 0) & 0x7fffffff;

    private static string Describe(FieldValues v) => $"{v.Designation} | {v.Name} | {v.Type}";

    private static List<string> SaveCrops(string? outputRoot, RealCorpus corpus, string key, List<RecognitionDiagnostic> diagnostics)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(outputRoot)) return names;
        var folder = Path.Combine(outputRoot, "crops", Sanitize(corpus.Title));
        Directory.CreateDirectory(folder);
        var index = 0;
        foreach (var d in diagnostics.Where(d => d.Field is not null))
        {
            var name = $"{Sanitize(key)}-{index++:00}-{d.Field!.Role}.png";
            File.WriteAllBytes(Path.Combine(folder, name), d.Field.CropPng);
            names.Add($"crops/{Sanitize(corpus.Title)}/{name}");
        }
        return names;
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    /// <summary>
    /// Точность каждого движка по каждой графе отдельно от итоговой сверки: сколько раз движок
    /// прочитал графу верно, сколько раз дал ответ без отказа, но неверный (такие ответы
    /// останавливает только второй движок), и сколько раз отказался сам.
    /// </summary>
    private sealed class EngineStats
    {
        private readonly SortedDictionary<string, int[]> _counts = new(StringComparer.Ordinal);

        public void Add(string variant, FieldDiagnostic field, FieldValues expected, bool hasTruth)
        {
            if (field.Role == FieldRole.Combined || !hasTruth) return;
            var truth = field.Role switch
            {
                FieldRole.Designation => expected.Designation,
                FieldRole.Name => expected.Name,
                _ => expected.Type,
            };
            foreach (var engine in new[] { field.First, field.Second })
            {
                var key = $"{variant}|{engine.Engine}|{field.Role}";
                if (!_counts.TryGetValue(key, out var c)) _counts[key] = c = new int[3];
                if (engine.Problem is not null) c[2]++;
                else if (engine.ParsedText == truth) c[0]++;
                else c[1]++;
            }
        }

        public IEnumerable<string> Lines() => _counts.Select(p =>
        {
            var k = p.Key.Split('|');
            return $"  {k[0],-14} {k[1],-18} {k[2],-12} верно {p.Value[0],4}  неверно без отказа {p.Value[1],4}  отказ {p.Value[2],4}";
        });

        public object Table() => _counts.Select(p =>
        {
            var k = p.Key.Split('|');
            return new { Variant = k[0], Engine = k[1], Role = k[2], Correct = p.Value[0], ConfidentWrong = p.Value[1], Refused = p.Value[2] };
        }).ToArray();
    }
}
