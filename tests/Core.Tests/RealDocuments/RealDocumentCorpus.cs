using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.RealDocuments;

/// <summary>
/// Корпус настоящих документов конструкторской документации: PDF, эталонные значения граф
/// основной надписи (заданы человеком по самому документу, не по ответам OCR), области шаблонов
/// и базовая линия результатов. Хранится в <c>tests/real-documents</c>; дополнительный локальный
/// корпус с тем же форматом подключается переменной <c>DTR_REAL_DOCS_DIR</c> и в git не попадает.
/// </summary>
internal sealed record RealCorpus(string Root, string Title, IReadOnlyList<RealTemplate> Templates, IReadOnlyList<RealDocument> Documents)
{
    public const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Прочитать manifest.json; любая ошибка формата — исключение с понятным текстом.</summary>
    public static RealCorpus Load(string root)
    {
        var path = Path.Combine(root, ManifestName);
        var manifest = JsonSerializer.Deserialize<RealManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path}: пустой манифест");
        if (manifest.Version != 1) throw new InvalidDataException($"{path}: неподдерживаемая версия {manifest.Version}");
        var templates = manifest.Templates ?? [];
        var documents = manifest.Documents ?? [];
        foreach (var t in templates)
        {
            if (!File.Exists(Path.Combine(root, t.File))) throw new FileNotFoundException($"{path}: нет файла шаблона", t.File);
            var problem = TemplateMarkupStore.Validate(t.Regions, t.TypeAbsent);
            if (problem is not null) throw new InvalidDataException($"{path}: шаблон «{t.Name}»: {problem}");
        }
        foreach (var d in documents)
        {
            if (!File.Exists(Path.Combine(root, d.File))) throw new FileNotFoundException($"{path}: нет документа", d.File);
            if (!templates.Any(t => t.Name == d.Template))
                throw new InvalidDataException($"{path}: документ {d.Id}: нет шаблона «{d.Template}»");
            if (string.IsNullOrWhiteSpace(d.Expected.Designation) || string.IsNullOrWhiteSpace(d.Expected.Name)
                || string.IsNullOrWhiteSpace(d.Expected.Type))
                throw new InvalidDataException($"{path}: документ {d.Id}: эталон должен содержать обозначение, наименование и тип");
        }
        var duplicate = documents.GroupBy(d => d.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"{path}: повторяется id {duplicate.Key}");
        return new RealCorpus(root, manifest.Title ?? Path.GetFileName(root), templates, documents);
    }

    /// <summary>Корпус в репозитории и, если задан, локальный корпус пользователя.</summary>
    public static IReadOnlyList<RealCorpus> Discover()
    {
        var result = new List<RealCorpus>();
        var tracked = FindTracked();
        if (tracked is not null) result.Add(Load(tracked));
        var local = Environment.GetEnvironmentVariable("DTR_REAL_DOCS_DIR");
        if (!string.IsNullOrWhiteSpace(local))
        {
            if (!File.Exists(Path.Combine(local, ManifestName)))
                throw new FileNotFoundException($"DTR_REAL_DOCS_DIR={local}: нет {ManifestName}");
            result.Add(Load(Path.GetFullPath(local)));
        }
        return result;
    }

    public static string? FindTracked()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "real-documents");
            if (File.Exists(Path.Combine(candidate, ManifestName))) return candidate;
        }
        return null;
    }
}

internal sealed record RealManifest
{
    public int Version { get; init; }
    public string? Title { get; init; }
    public RealTemplate[]? Templates { get; init; }
    public RealDocument[]? Documents { get; init; }
}

/// <summary>Шаблон: PDF-образец формы и области граф в долях страницы (как сохраняет редактор).</summary>
internal sealed record RealTemplate
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public int Page { get; init; }
    public bool TypeAbsent { get; init; }
    public required TemplateRegion[] Regions { get; init; }
}

internal sealed record RealExpected(string Designation, string Name, string Type);

/// <summary>Ожидаемый исход варианта: точное чтение обязательно или допустим отказ.</summary>
internal enum RealBaseline { Exact, Refused }

internal sealed record RealDocument
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Template { get; init; }
    public required RealExpected Expected { get; init; }
    public string? Source { get; init; }
    public string? License { get; init; }
    public string? Note { get; init; }
    /// <summary>Базовая линия по вариантам. Нет записи — допускается отказ, неверное имя запрещено всегда.</summary>
    public Dictionary<string, RealBaseline> Baseline { get; init; } = [];
}

/// <summary>
/// Варианты одного документа. «vector» — исходный PDF (текстовый слой подтверждается OCR).
/// Растровые варианты — тот же лист без текстового слоя, как после сканера: проверяется
/// чистое распознавание изображения. Шум задается своим зерном: прогоны повторяемы.
/// </summary>
internal sealed record RealVariant(string Key, int Dpi, double SkewDegrees, double Blur, double Noise, int JpegQuality, bool Gray)
{
    public static readonly RealVariant Vector = new("vector", 0, 0, 0, 0, 0, false);

    public static readonly IReadOnlyList<RealVariant> All =
    [
        Vector,
        new("scan300", 300, 0.0, 0.0, 0.0, 90, true),
        new("scan200-skew", 200, 0.6, 0.5, 0.02, 75, true),
    ];

    /// <summary>Создать растровую копию страницы документа.</summary>
    public string Rasterize(string sourcePdf, int page, string targetPdf, int seed)
    {
        using var pdf = PdfDocument.Open(sourcePdf);
        var size = pdf.GetPageSize(page);
        var longSide = (int)Math.Round(Math.Max(size.WidthMm, size.HeightMm) / 25.4 * Dpi);
        using var bgr = pdf.RenderPage(page, longSide);
        using var scanned = Degrade(bgr, seed);
        SyntheticForms.SavePdf(targetPdf, [scanned], Dpi, JpegQuality);
        return targetPdf;
    }

    private Mat Degrade(Mat bgr, int seed)
    {
        var page = bgr.Clone();
        if (Gray)
        {
            using var gray = new Mat();
            Cv2.CvtColor(page, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(gray, page, ColorConversionCodes.GRAY2BGR);
        }
        if (SkewDegrees != 0)
        {
            using var rot = Cv2.GetRotationMatrix2D(new Point2f(page.Width / 2f, page.Height / 2f), SkewDegrees, 1.0);
            using var warped = new Mat();
            Cv2.WarpAffine(page, warped, rot, page.Size(), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
            warped.CopyTo(page);
        }
        if (Blur > 0) Cv2.GaussianBlur(page, page, new Size(0, 0), Blur);
        if (Noise > 0)
        {
            using var noise = new Mat(page.Size(), MatType.CV_16SC3);
            var rng = new RNG((ulong)seed);
            rng.Fill(noise, DistributionType.Normal, Scalar.All(0), Scalar.All(Noise * 255));
            using var wide = new Mat();
            page.ConvertTo(wide, MatType.CV_16SC3);
            Cv2.Add(wide, noise, wide);
            wide.ConvertTo(page, MatType.CV_8UC3);
        }
        return page;
    }
}
