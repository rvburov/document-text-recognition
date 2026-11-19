using System.Security.Cryptography;
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
/// основной надписи (заданы человеком по самому документу, не по ответам OCR), области полей
/// (как их сохраняет редактор шаблона) и базовая линия результатов.
///
/// <para>Корпус в репозитории — <c>tests/real-documents</c>. Документы с открытой лицензией
/// лежат в <c>documents/</c>; остальные хранятся у авторов на GitHub и скачиваются по ссылке на
/// конкретный коммит в <c>external/</c> (папка не попадает в git), содержимое проверяется по
/// SHA-256. Свой корпус с тем же форматом подключается переменной <c>DTR_REAL_DOCS_DIR</c>.</para>
/// </summary>
internal sealed record RealCorpus(string Root, string Title, IReadOnlyList<RealDocument> Documents)
{
    public const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Прочитать manifest.json; ошибка формата — исключение с понятным текстом.</summary>
    public static RealCorpus Load(string root)
    {
        var path = Path.Combine(root, ManifestName);
        var manifest = JsonSerializer.Deserialize<RealManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path}: пустой манифест");
        if (manifest.Version != 1) throw new InvalidDataException($"{path}: неподдерживаемая версия {manifest.Version}");
        var documents = manifest.Documents ?? [];
        var duplicate = documents.GroupBy(d => d.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"{path}: повторяется id {duplicate.Key}");
        foreach (var d in documents)
        {
            var template = d.Template is null ? d : documents.FirstOrDefault(t => t.Id == d.Template)
                ?? throw new InvalidDataException($"{path}: {d.Id}: нет документа-шаблона «{d.Template}»");
            if (template.Regions is null)
                throw new InvalidDataException($"{path}: {d.Id}: у шаблона {template.Id} нет областей");
            var problem = TemplateMarkupStore.Validate(template.Regions, template.TypeAbsent);
            if (problem is not null) throw new InvalidDataException($"{path}: {template.Id}: {problem}");
            if (d.MustRefuse is null && (string.IsNullOrWhiteSpace(d.Expected.Designation)
                || string.IsNullOrWhiteSpace(d.Expected.Name) || string.IsNullOrWhiteSpace(d.Expected.Type)))
                throw new InvalidDataException($"{path}: {d.Id}: эталон должен содержать обозначение, наименование и тип");
            if (d.Sha256 is { } sha && (sha.Length != 64 || !sha.All(Uri.IsHexDigit)))
                throw new InvalidDataException($"{path}: {d.Id}: неверный sha256");
        }
        return new RealCorpus(root, manifest.Title ?? Path.GetFileName(root), documents);
    }

    /// <summary>Корпус в репозитории и, если задан, свой корпус пользователя.</summary>
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

    public string PathOf(RealDocument document) => Path.Combine(Root, document.File);

    /// <summary>
    /// Файл документа на месте и совпадает с эталонным хешем. Отсутствующий внешний документ
    /// скачивается, если задано <c>DTR_REAL_DOCS_FETCH=1</c>. null — документ готов; иначе причина.
    /// Неверный хеш у файла на диске — ошибка корпуса, а не пропуск.
    /// </summary>
    public string? Ensure(RealDocument document)
    {
        var path = PathOf(document);
        if (!File.Exists(path))
        {
            if (document.Source is null) throw new FileNotFoundException($"{document.Id}: нет файла", path);
            if (Environment.GetEnvironmentVariable("DTR_REAL_DOCS_FETCH") != "1")
                return "внешний документ не скачан (DTR_REAL_DOCS_FETCH=1 скачает его)";
            try { Download(document.Source, path, document.Sha256); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
            {
                return $"не удалось скачать: {ex.Message}";
            }
        }
        if (document.Sha256 is { } expected)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{document.Id}: файл отличается от эталона (sha256 {actual})");
        }
        return null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static void Download(string url, string path, string? sha256)
    {
        var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (sha256 is not null && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("содержимое по ссылке изменилось (sha256 не совпадает)");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".part";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed record RealManifest
{
    public int Version { get; init; }
    public string? Title { get; init; }
    public RealDocument[]? Documents { get; init; }
}

/// <summary>Эталон в форме, которую дает программа: код документа слитно, «ё» как «е», тип
/// из справочника; без типа в шаблоне — «Спецификация».</summary>
internal sealed record RealExpected(string Designation, string Name, string Type);

/// <summary>Исход варианта в базовой линии.</summary>
internal enum RealBaseline { Exact, Refused }

internal sealed record RealDocument
{
    public required string Id { get; init; }
    /// <summary>Путь от корня корпуса: documents/… (в git) или external/… (скачивается).</summary>
    public required string File { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>Ссылка для скачивания на конкретный коммит.</summary>
    public string? Source { get; init; }
    public string? Repository { get; init; }
    public string? License { get; init; }
    public string? Producer { get; init; }
    /// <summary>Страница с основной надписью (от 0): по ней строится шаблон и «скан».</summary>
    public int Page { get; init; }
    public bool TypeAbsent { get; init; }
    /// <summary>Области полей, как их сохраняет редактор шаблона.</summary>
    public TemplateRegion[]? Regions { get; init; }
    /// <summary>Шаблон из другого документа (id); по умолчанию — сам документ.</summary>
    public string? Template { get; init; }
    public required RealExpected Expected { get; init; }
    /// <summary>Программа не может дать верное имя (нет обозначения по правилам): любое принятое значение — ошибка.</summary>
    public string? MustRefuse { get; init; }
    public string? Note { get; init; }
    /// <summary>Базовая линия по вариантам. Нет записи — допускается отказ; неверное имя запрещено всегда.</summary>
    public Dictionary<string, RealBaseline> Baseline { get; init; } = [];
}

/// <summary>
/// Варианты документа. «vector» — исходный PDF (текстовый слой подтверждается OCR, иначе OCR).
/// Растровые варианты — страница с основной надписью без текстового слоя, как после сканера:
/// чистое распознавание изображения. Шум задается своим зерном: прогоны повторяемы.
/// </summary>
internal sealed record RealVariant(string Key, int Dpi, double SkewDegrees, double Blur, double Noise, int JpegQuality)
{
    public static readonly RealVariant Vector = new("vector", 0, 0, 0, 0, 0);

    private const int MaxLongSide = 7016;

    public static readonly IReadOnlyList<RealVariant> All =
    [
        Vector,
        new("scan300", 300, 0.0, 0.0, 0.0, 90),
        new("scan200-skew", 200, 0.6, 0.5, 0.02, 75),
    ];

    /// <summary>Растровая копия одной страницы документа: серый лист, как из сканера.</summary>
    public void Rasterize(string sourcePdf, int page, string targetPdf, int seed)
    {
        using var pdf = PdfDocument.Open(sourcePdf);
        var size = pdf.GetPageSize(page);
        // Листы A1 и больше сканируются широкоформатным сканером с меньшим разрешением:
        // длинная сторона не больше, чем у A2 при 300 dpi, иначе картинка не помещается в память.
        var inches = Math.Max(size.WidthMm, size.HeightMm) / 25.4;
        var dpi = Math.Min(Dpi, (int)(MaxLongSide / inches));
        using var bgr = pdf.RenderPage(page, (int)Math.Round(inches * dpi));
        using var scanned = Degrade(bgr, seed);
        SyntheticForms.SavePdf(targetPdf, [scanned], dpi, JpegQuality);
    }

    private Mat Degrade(Mat bgr, int seed)
    {
        var page = new Mat();
        using (var gray = new Mat())
        {
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
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
