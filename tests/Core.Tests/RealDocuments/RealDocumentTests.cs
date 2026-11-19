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
/// <item>принятое неверное значение — всегда провал (главное требование программы);</item>
/// <item>вариант, который в базовой линии читался точно, а теперь отказ, — провал (регрессия);</item>
/// <item>отказ там, где базовая линия его допускает, — не провал, но попадает в отчет;
/// стал читаться точно — тест подсказывает обновить базовую линию.</item>
/// </list>
/// Отчет с ответами каждого движка по каждой графе пишется в <c>DTR_REAL_DOCS_OUTPUT</c>.
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
        var failures = new List<string>();
        var improvements = new List<string>();
        var reports = new List<object>();
        var outputRoot = Environment.GetEnvironmentVariable("DTR_REAL_DOCS_OUTPUT");

        foreach (var corpus in corpora)
        {
            Assert.NotEmpty(corpus.Documents);
            using var temp = new TempDir("real");
            var templates = PrepareTemplates(corpus, Path.Combine(temp.Path, "Шаблоны"));
            try
            {
                var rows = new List<object>();
                var stats = new Dictionary<string, int>();
                var engines = new EngineStats();
                foreach (var document in corpus.Documents)
                foreach (var variant in RealVariant.All)
                {
                    var file = PrepareDocument(corpus, document, variant, Path.Combine(temp.Path, "Документы"));
                    var original = SHA256.HashData(File.ReadAllBytes(file));
                    var diagnostics = new List<RecognitionDiagnostic>();
                    var result = DocumentReader.Read(file, templates, ocr.Pair, CancellationToken.None, diagnostics.Add);
                    Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(file)));

                    var expected = new FieldValues(document.Expected.Designation, document.Expected.Name, document.Expected.Type);
                    var outcome = result.Values is null ? Outcome.Refused : result.Values == expected ? Outcome.Exact : Outcome.Wrong;
                    var key = $"{document.Id}/{variant.Key}";
                    stats[$"{variant.Key}.{outcome}"] = stats.GetValueOrDefault($"{variant.Key}.{outcome}") + 1;
                    var baseline = document.Baseline.TryGetValue(variant.Key, out var b) ? b : (RealBaseline?)null;

                    if (outcome == Outcome.Wrong)
                        failures.Add($"{key}: принято НЕВЕРНОЕ значение «{Describe(result.Values!)}», эталон «{Describe(expected)}»");
                    else if (outcome == Outcome.Refused && baseline == RealBaseline.Exact)
                        failures.Add($"{key}: регрессия, раньше читалось точно, теперь отказ: {result.Problem}");
                    else if (outcome == Outcome.Exact && baseline != RealBaseline.Exact)
                        improvements.Add($"{key}: теперь читается точно (обновите baseline в manifest.json)");
                    if (outcome == Outcome.Exact)
                    {
                        var built = FileNames.Build(result.Values!);
                        if (built.Problem is not null) failures.Add($"{key}: имя файла не собрано: {built.Problem}");
                    }

                    var fields = diagnostics.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
                    foreach (var field in fields) engines.Add(variant.Key, field, expected);
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
                        result.Template,
                        Fields = diagnostics.Where(d => d.Field is not null).Select((d, i) => new
                        {
                            d.Stage,
                            Role = d.Field!.Role,
                            Crop = crops.ElementAtOrDefault(i),
                            First = d.Field.First,
                            Second = d.Field.Second,
                        }).ToArray(),
                        Stages = diagnostics.Where(d => d.Field is null).Select(d => $"{d.Stage}: {d.Message}").ToArray(),
                    });
                    output.WriteLine($"{key,-40} {outcome,-8} {(result.Values is null ? result.Problem : Describe(result.Values))}");
                }

                foreach (var line in stats.OrderBy(p => p.Key)) output.WriteLine($"{corpus.Title}: {line.Key} = {line.Value}");
                foreach (var line in engines.Lines()) output.WriteLine(line);
                reports.Add(new
                {
                    Corpus = corpus.Title,
                    Documents = corpus.Documents.Count,
                    Variants = RealVariant.All.Select(v => v.Key).ToArray(),
                    Summary = stats.OrderBy(p => p.Key).ToDictionary(),
                    EngineFieldAccuracy = engines.Table(),
                    Results = rows,
                });
            }
            finally
            {
                foreach (var t in templates) t.Dispose();
            }
        }

        foreach (var line in improvements) output.WriteLine("Улучшение: " + line);
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
                },
                Failures = failures,
                Improvements = improvements,
                Corpora = reports,
            }, ReportJson));
        }
        Assert.True(failures.Count == 0, "Реальные документы:\n" + string.Join("\n", failures));
    }

    private static List<DocTemplate> PrepareTemplates(RealCorpus corpus, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var template in corpus.Templates)
        {
            // Разметка создается заново рядом с копией: отпечаток PDF всегда совпадает.
            var path = Path.Combine(folder, template.Name + ".pdf");
            File.Copy(Path.Combine(corpus.Root, template.File), path);
            var problem = TemplateMarkupStore.Save(path, template.Page, template.Regions, template.TypeAbsent);
            Assert.True(problem is null, $"{corpus.Title}: шаблон «{template.Name}»: {problem}");
        }
        var loads = TemplateLibrary.LoadFolder(folder);
        var broken = loads.Where(l => l.Template is null).Select(l => $"{l.FileName}: {l.Problem}").ToArray();
        Assert.True(broken.Length == 0, string.Join("; ", broken));
        return loads.Select(l => l.Template!).ToList();
    }

    private static string PrepareDocument(RealCorpus corpus, RealDocument document, RealVariant variant, string folder)
    {
        Directory.CreateDirectory(folder);
        var source = Path.Combine(corpus.Root, document.File);
        // Нейтральное имя: эталон нельзя получить из имени файла.
        var target = Path.Combine(folder, $"doc-{Math.Abs(StableHash(document.Id)):x8}-{variant.Key}.pdf");
        if (variant == RealVariant.Vector)
            File.Copy(source, target, overwrite: true);
        else
            variant.Rasterize(source, 0, target, StableHash(document.Id + variant.Key));
        return target;
    }

    private static int StableHash(string text)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
        return BitConverter.ToInt32(bytes, 0) & 0x7fffffff;
    }

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
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    /// <summary>
    /// Точность каждого движка по каждой графе отдельно от итоговой сверки: сколько раз движок
    /// прочитал графу верно, сколько раз дал уверенный, но неверный ответ (такие ответы
    /// останавливает только второй движок) и сколько раз сам отказался.
    /// </summary>
    private sealed class EngineStats
    {
        private readonly Dictionary<(string Variant, string Engine, FieldRole Role), int[]> _counts = [];

        public void Add(string variant, FieldDiagnostic field, FieldValues expected)
        {
            if (field.Role == FieldRole.Combined) return;
            var truth = field.Role switch
            {
                FieldRole.Designation => expected.Designation,
                FieldRole.Name => expected.Name,
                _ => expected.Type,
            };
            foreach (var engine in new[] { field.First, field.Second })
            {
                var key = (variant, engine.Engine, field.Role);
                if (!_counts.TryGetValue(key, out var c)) _counts[key] = c = new int[3];
                if (engine.Problem is not null) c[2]++;
                else if (engine.ParsedText == truth) c[0]++;
                else c[1]++;
            }
        }

        public IEnumerable<string> Lines() => _counts.OrderBy(p => p.Key.Variant).ThenBy(p => p.Key.Engine).ThenBy(p => p.Key.Role)
            .Select(p => $"  {p.Key.Variant,-14} {p.Key.Engine,-22} {p.Key.Role,-12} верно {p.Value[0],3}  неверно без отказа {p.Value[1],3}  отказ {p.Value[2],3}");

        public object Table() => _counts.OrderBy(p => p.Key.Variant).ThenBy(p => p.Key.Engine).ThenBy(p => p.Key.Role)
            .Select(p => new { p.Key.Variant, p.Key.Engine, Role = p.Key.Role.ToString(), Correct = p.Value[0], ConfidentWrong = p.Value[1], Refused = p.Value[2] })
            .ToArray();
    }
}
