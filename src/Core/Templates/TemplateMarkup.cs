using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;

namespace DocumentTextRecognition.Core.Templates;

/// <summary>Вручную выделенная область: координаты от левого верхнего угла в долях страницы.</summary>
internal sealed record TemplateRegion(FieldRole Role, NormRect Bounds);

/// <summary>Переносимая разметка PDF. Оттенки и масштаб предпросмотра на координаты не влияют.</summary>
internal sealed record TemplateMarkup
{
    public int Version { get; init; } = 1;
    public int PageIndex { get; init; }
    public double PageWidth { get; init; }
    public double PageHeight { get; init; }
    public string PdfSha256 { get; init; } = "";
    public bool TypeAbsent { get; init; }
    public IReadOnlyList<TemplateRegion> Regions { get; init; } = [];
}

internal sealed record TemplateMarkupLoad(TemplateMarkup? Markup, string? Problem, bool Exists);

/// <summary>
/// Разметка хранится рядом с исходным PDF. Поврежденная или устаревшая разметка не подменяется
/// автоматическим поиском рамок: пользователь должен проверить области и сохранить их снова.
/// </summary>
internal static class TemplateMarkupStore
{
    private const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string PathFor(string pdfFile) => pdfFile + ".template.json";

    public static TemplateMarkupLoad Load(string pdfFile)
    {
        var path = PathFor(pdfFile);
        TemplateMarkup? markup = null;
        try
        {
            // File.Exists hides permission errors. Opening directly distinguishes a missing
            // sidecar from an unreadable one, which must never silently enable old auto markup.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaxBytes)
                    return new(null, "файл разметки слишком большой", true);
                markup = JsonSerializer.Deserialize<TemplateMarkup>(stream, Json);
            }
            if (markup is null) return new(null, "файл разметки пуст", true);
            var problem = ValidateMetadata(markup);
            if (problem is not null) return new(markup, problem, true);

            using var pdf = PdfDocument.Open(pdfFile);
            if (markup.PageIndex >= pdf.PageCount)
                return new(markup, "страница разметки отсутствует в PDF: откройте редактор шаблона", true);
            var size = pdf.GetPageSize(markup.PageIndex);
            if (Math.Abs(markup.PageWidth - size.Width) > 0.01 || Math.Abs(markup.PageHeight - size.Height) > 0.01)
                return new(markup, "размер страницы PDF изменился: проверьте области в редакторе шаблона", true);
            if (!string.Equals(markup.PdfSha256, GetFingerprint(pdfFile), StringComparison.OrdinalIgnoreCase))
                return new(markup, "PDF изменился после разметки: проверьте области и сохраните шаблон снова", true);
            return new(markup, null, true);
        }
        catch (FileNotFoundException) when (markup is null) { return new(null, null, false); }
        catch (DirectoryNotFoundException) when (markup is null) { return new(null, null, false); }
        catch (JsonException) { return new(null, "поврежден файл разметки: откройте редактор шаблона и сохраните области снова", true); }
        catch (PdfOpenException ex) { return new(markup, ex.Message, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(markup, $"не удалось прочитать разметку: {ex.Message}", true);
        }
    }

    /// <summary>Сохранить проверенные области; null — успех. При ошибке прежняя разметка остается.</summary>
    public static string? Save(string pdfFile, int pageIndex, IReadOnlyList<TemplateRegion> regions, bool typeAbsent = false,
        string? expectedPdfSha256 = null)
    {
        var problem = Validate(regions, typeAbsent);
        if (problem is not null) return problem;
        string? temporary = null;
        try
        {
            var fingerprint = GetFingerprint(pdfFile);
            if (expectedPdfSha256 is not null && !string.Equals(expectedPdfSha256, fingerprint, StringComparison.OrdinalIgnoreCase))
                return "PDF изменился после открытия редактора: закройте редактор и откройте документ снова";
            using var pdf = PdfDocument.Open(pdfFile);
            if (pageIndex < 0 || pageIndex >= pdf.PageCount) return "выберите существующую страницу PDF";
            var size = pdf.GetPageSize(pageIndex);
            if (fingerprint != GetFingerprint(pdfFile)) return "PDF изменился во время сохранения: откройте его снова";
            var markup = new TemplateMarkup
            {
                PageIndex = pageIndex,
                PageWidth = size.Width,
                PageHeight = size.Height,
                PdfSha256 = fingerprint,
                TypeAbsent = typeAbsent,
                Regions = regions.ToArray(),
            };
            var target = PathFor(pdfFile);
            temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(markup, Json));
            File.Move(temporary, target, overwrite: true);
            return null;
        }
        catch (PdfOpenException ex) { return ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"не удалось сохранить разметку рядом с PDF: {ex.Message}";
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string? Validate(IReadOnlyList<TemplateRegion>? regions, bool typeAbsent)
    {
        if (regions is null || regions.Count == 0) return "выделите области на странице";
        if (regions.Count > 4) return "слишком много областей: каждому полю нужна одна область";
        foreach (var region in regions)
        {
            if (region is null || !Enum.IsDefined(region.Role)) return "неизвестное назначение области";
            var r = region.Bounds;
            if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height)
                || r.X < 0 || r.X > 1 || r.Y < 0 || r.Y > 1 || r.Width <= 0 || r.Width > 1 || r.Height <= 0 || r.Height > 1
                || r.Right > 1.000000001 || r.Bottom > 1.000000001)
                return "область должна находиться внутри страницы и иметь положительный размер";
            if (r.Width * r.Height > 0.5) return "область занимает больше половины страницы";
        }
        if (regions.Select(r => r.Role).Distinct().Count() != regions.Count)
            return "каждому полю нужна только одна область";
        if (regions.Any(r => r.Role == FieldRole.Combined))
        {
            if (regions.Count != 1) return "используйте одну красную область или отдельные области полей";
            if (typeAbsent) return "для общей красной области тип определяется по тексту: отключите «Тип отсутствует»";
        }
        else
        {
            if (!regions.Any(r => r.Role == FieldRole.Designation)) return "выделите обозначение (синяя область)";
            if (!regions.Any(r => r.Role == FieldRole.Name)) return "выделите наименование (зеленая область)";
            var hasType = regions.Any(r => r.Role == FieldRole.Type);
            if (typeAbsent && hasType) return "уберите желтую область или отключите «Тип отсутствует»";
            if (!typeAbsent && !hasType) return "выделите тип документа или укажите «Тип отсутствует → Спецификация»";
        }
        // Касающиеся стороны разрешены: пересекаться не должны только внутренние области.
        for (var i = 0; i < regions.Count; i++)
            for (var j = i + 1; j < regions.Count; j++)
            {
                var a = regions[i].Bounds;
                var b = regions[j].Bounds;
                if (Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X) > 1e-9
                    && Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y) > 1e-9)
                    return "области перекрываются: разделите поля";
            }
        return null;
    }

    private static string? ValidateMetadata(TemplateMarkup markup)
    {
        if (markup.Version != 1) return "неподдерживаемая версия разметки: обновите программу";
        if (markup.PageIndex < 0 || !double.IsFinite(markup.PageWidth) || !double.IsFinite(markup.PageHeight)
            || markup.PageWidth <= 0 || markup.PageHeight <= 0)
            return "неверные параметры страницы в файле разметки";
        if (markup.PdfSha256 is null || markup.PdfSha256.Length != 64 || !markup.PdfSha256.All(Uri.IsHexDigit))
            return "в файле разметки нет корректного отпечатка PDF: сохраните шаблон снова";
        return Validate(markup.Regions, markup.TypeAbsent);
    }

    /// <summary>Отпечаток исходника; редактор передает его при сохранении открытого документа.</summary>
    public static string GetFingerprint(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
