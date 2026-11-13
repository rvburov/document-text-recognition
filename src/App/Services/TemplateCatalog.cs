using System.Text.RegularExpressions;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using OpenCvSharp;

namespace DocumentTextRecognition.Services;

/// <summary>Шаблон в разделе «Шаблоны»: файл, распознан ли, размер страницы, найденные рамки.</summary>
public sealed class TemplateItem : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Точка у строки: зеленая (распознан), красная (не распознан), серая (проверяется).</summary>
    public Avalonia.Media.IBrush Dot => Recognized switch
    {
        true => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#17803D")),
        false => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#B42318")),
        _ => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#C9D0DA")),
    };

    /// <summary>Сообщить окну, что проверка закончилась.</summary>
    public void Changed() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null));

    public required string FilePath { get; init; }
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>null, пока шаблон проверяется.</summary>
    public bool? Recognized { get; set; }
    public string Sub { get; set; } = "Проверяется…";
    public string PageSize { get; set; } = "";
    public string Frames { get; set; } = "";
    public int PageIndex { get; set; }
}

/// <summary>
/// Шаблоны из папки шаблонов для раздела «Шаблоны»: проверка тем же разбором, что при запуске
/// (<see cref="TemplateLibrary"/>), и картинка страницы для просмотра.
/// </summary>
public static class TemplateCatalog
{
    public static List<TemplateItem> List(string folder) =>
        Directory.Exists(folder)
            ? TemplateLibrary.ListFiles(folder).Select(f => new TemplateItem { FilePath = f }).ToList()
            : [];

    private static readonly Regex PagePrefix = new(@"^стр\. \d+: ", RegexOptions.Compiled);

    /// <summary>Проверить шаблон: распознан или нет, размер страницы, цвета рамок.</summary>
    public static void Inspect(TemplateItem item)
    {
        var load = TemplateLibrary.Load(item.FilePath);
        using (load.Template)
        {
            item.Recognized = load.Template is not null;
            item.Sub = load.Template is not null ? "Распознан" : "Не распознан: " + PagePrefix.Replace(load.Problem ?? "", "");
            try
            {
                using var pdf = PdfDocument.Open(item.FilePath);
                item.PageIndex = load.Template?.PageIndex ?? 0;
                item.PageSize = DescribeSize(pdf.GetPageSize(item.PageIndex));
                IReadOnlyList<ColorFrame> frames = load.Template?.Frames ?? DetectFrames(pdf, item.PageIndex);
                item.Frames = DescribeFrames(frames.Select(f => f.Role));
            }
            catch (Exception ex) when (ex is PdfOpenException or OpenCVException or IOException)
            {
                item.PageSize = "";
                item.Frames = "не найдены";
            }
        }
    }

    private static IReadOnlyList<ColorFrame> DetectFrames(PdfDocument pdf, int page)
    {
        using var render = pdf.RenderPage(page, TemplateLibrary.FrameLongSide);
        return ColorFrames.Detect(render).Frames;
    }

    internal static string DescribeFrames(IEnumerable<FieldRole> roles)
    {
        var order = new[] { FieldRole.Designation, FieldRole.Name, FieldRole.Type, FieldRole.Combined };
        var names = order.Where(roles.Contains).Select(ColorFrames.Describe).ToList();
        return names.Count == 0 ? "не найдены" : string.Join(", ", names);
    }

    /// <summary>«A3, альбомная» для стандартных листов, иначе размеры в миллиметрах.</summary>
    internal static string DescribeSize(PageSize size)
    {
        double w = size.WidthMm, h = size.HeightMm;
        var (shortSide, longSide) = (Math.Min(w, h), Math.Max(w, h));
        var formats = new (string Name, double S, double L)[]
        {
            ("A4", 210, 297), ("A3", 297, 420), ("A2", 420, 594), ("A1", 594, 841), ("A0", 841, 1189),
            ("A4×3", 297, 630), ("A4×4", 297, 841), ("A3×3", 420, 891),
        };
        foreach (var f in formats)
            if (Math.Abs(shortSide - f.S) <= 6 && Math.Abs(longSide - f.L) <= 6)
                return $"{f.Name}, {(w > h ? "альбомная" : "книжная")}";
        return $"{w:F0} × {h:F0} мм";
    }

    /// <summary>Страница шаблона картинкой PNG для просмотра (длинная сторона в точках).</summary>
    public static byte[]? RenderPng(string file, int page, int longSide = 1800)
    {
        try
        {
            using var pdf = PdfDocument.Open(file);
            using var render = pdf.RenderPage(Math.Clamp(page, 0, pdf.PageCount - 1), longSide);
            Cv2.ImEncode(".png", render, out var png);
            return png;
        }
        catch (Exception ex) when (ex is PdfOpenException or OpenCVException or IOException)
        {
            return null;
        }
    }
}
