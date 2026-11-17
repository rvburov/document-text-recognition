using OpenCvSharp;
using System.Runtime.InteropServices;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Matching;
using DocumentTextRecognition.Core.Pdf;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests.Matching;

/// <summary>
/// Группы ТЗ 5–7 (без OCR): повороты 90/180/270, наклон, масштаб, сдвиг; выбор среди
/// нескольких шаблонов одного формата; нет подходящего шаблона; одинаковые шаблоны —
/// неоднозначность; входные документы без цветных рамок; шаблоны в векторе (как из
/// редактора PDF) и с ошибками разметки.
/// </summary>
public sealed class MatchingTests : IDisposable
{
    private readonly Workbench _wb = new();
    private readonly List<DocTemplate> _templates = [];

    public void Dispose()
    {
        foreach (var t in _templates) t.Dispose();
        _wb.Dispose();
    }

    private List<DocTemplate> Load()
    {
        var loads = TemplateLibrary.LoadFolder(_wb.Templates);
        foreach (var l in loads) Assert.True(l.Template is not null, $"{l.FileName}: {l.Problem}");
        _templates.AddRange(loads.Select(l => l.Template!));
        return _templates;
    }

    private static MatchOutcome MatchFirstPage(string pdfPath, IReadOnlyList<DocTemplate> templates)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        var size = pdf.GetPageSize(0);
        using var render = pdf.RenderPage(0, PageFeatures.LongSide);
        var gray = PageImage.ToGray(render);
        using var features = PageFeatures.Build(gray, gray.Width / size.Width);
        return TemplateMatcher.Match(features, size, templates);
    }

    public static TheoryData<int, double, double, double, double> Distortions => new()
    {
        { 0, 0, 1.0, 0, 0 },
        { 1, 0, 1.0, 0, 0 },
        { 2, 0, 1.0, 0, 0 },
        { 3, 0, 1.0, 0, 0 },
        { 0, 2.5, 1.0, 0, 0 },
        { 0, -4, 1.0, 0, 0 },
        { 1, 1.5, 0.92, 5, -4 },
        { 2, -1, 1.06, -6, 3 },
        { 3, 3, 0.9, 0, 8 },
    };

    [OcrTheory]
    [MemberData(nameof(Distortions))]
    public void Rotated_skewed_scaled_and_shifted_scans_match_the_right_template(int turns, double skew, double scale, double dx, double dy)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        var options = new ScanOptions { QuarterTurns = turns, SkewDegrees = skew, Scale = scale, ShiftXmm = dx, ShiftYmm = dy, Noise = 0.02 };

        var drawing = MatchFirstPage(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус", options, seed: 3), templates);
        var spec = MatchFirstPage(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль", options, seed: 4), templates);

        Assert.Equal("Чертеж", drawing.Match?.Template.Name);
        Assert.Equal("Спецификация", spec.Match?.Template.Name);
        var expected = -90.0 * turns + skew;   // поворот скана «назад» к шаблону
        Assert.True(AngleDiff(drawing.Match!.Angle, expected) < 1.0, $"угол {drawing.Match.Angle:F1}, ожидался {expected:F1}");
    }

    private static double AngleDiff(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    [OcrFact]
    public void Same_page_size_does_not_decide_the_type()
    {
        // Чертеж и спецификация — оба A4 книжные: выбор по форме бланка, а не по размеру.
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        Assert.All(templates, t => Assert.Equal(210, t.PageSize.WidthMm, 1.0));

        var outcome = MatchFirstPage(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль"), templates);
        Assert.Equal("Спецификация", outcome.Match?.Template.Name);
        using var features = Features(_wb.Source + "/s.pdf", out var size);
        Assert.True(outcome.Match!.Score - TemplateMatcher.TryMatch(features, size,
            templates.Single(t => t.Name == "Чертеж"))!.Score > TemplateMatcher.Margin);
    }

    private static DocTemplate WithSize(DocTemplate template, double scale, string name) => new()
    {
        Name = name, FilePath = template.FilePath, PageIndex = template.PageIndex,
        PageSize = new PageSize(template.PageSize.Width * scale, template.PageSize.Height * scale),
        Frames = template.Frames, Features = template.Features,
    };

    [OcrFact]
    public void Other_physical_size_cannot_make_a_valid_same_size_template_ambiguous()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var template = Assert.Single(Load());
        var otherSize = WithSize(template, 2, "Тот же бланк другого размера");
        using var features = Features(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус"), out var size);

        var outcome = TemplateMatcher.Match(features, size, [otherSize, template]);

        Assert.Same(template, outcome.Match?.Template);
        Assert.False(outcome.Ambiguous);
    }

    [OcrFact]
    public void Failed_same_size_candidate_does_not_block_other_physical_sizes()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        var drawing = templates.Single(t => t.Name == "Чертеж");
        var spec = WithSize(templates.Single(t => t.Name == "Спецификация"), 2, "Спецификация другого размера");
        using var features = Features(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль"), out var size);

        var outcome = TemplateMatcher.Match(features, size, [drawing, spec]);

        Assert.Same(spec, outcome.Match?.Template);
        Assert.False(outcome.Match?.SizeAgrees);
    }

    [OcrFact]
    public void Wrong_physical_size_in_pdf_still_matches_by_form()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        using var features = Features(_wb.AddDocument("s.pdf", FormKind.Specification, "АБВГ.461411.200", "Модуль"), out var size);

        var outcome = TemplateMatcher.Match(features, new PageSize(size.Width * 3, size.Height * 3), templates);

        Assert.Equal("Спецификация", outcome.Match?.Template.Name);
        Assert.False(outcome.Match?.SizeAgrees);
    }

    private static PageFeatures Features(string pdfPath, out PageSize size)
    {
        using var pdf = PdfDocument.Open(pdfPath);
        size = pdf.GetPageSize(0);
        using var render = pdf.RenderPage(0, PageFeatures.LongSide);
        var gray = PageImage.ToGray(render);
        return PageFeatures.Build(gray, gray.Width / size.Width);
    }

    [OcrFact]
    public void Two_identical_templates_are_ambiguous()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Чертеж детали", FormKind.Drawing);
        var templates = Load();

        var outcome = MatchFirstPage(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус"), templates);

        Assert.Null(outcome.Match);
        Assert.True(outcome.Ambiguous);
        Assert.Contains("несколько шаблонов", outcome.Problem);
    }

    [OcrFact]
    public void Unrelated_page_has_no_template()
    {
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();

        // Лист без бланка: только текст.
        using var page = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        for (var y = 200; y < 2200; y += 80)
            Cv2.PutText(page, "Lorem ipsum dolor sit amet 12345", new Point(150, y), HersheyFonts.HersheySimplex, 1.6, Scalar.All(0), 3);
        var path = _wb.AddPages("text.pdf", [page], 200);

        var outcome = MatchFirstPage(path, templates);

        Assert.Null(outcome.Match);
        Assert.False(outcome.Ambiguous);
        Assert.Contains("не найден", outcome.Problem);
    }

    [OcrFact]
    public void Different_format_is_not_matched()
    {
        _wb.AddTemplate("Чертеж A3", FormKind.DrawingA3, Marking.Combined);
        var templates = Load();
        var outcome = MatchFirstPage(_wb.AddDocument("a4.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус"), templates);
        Assert.Null(outcome.Match);
    }

    [OcrFact]
    public void Blank_page_is_reported_not_guessed()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var templates = Load();
        using var page = new Mat(2339, 1654, MatType.CV_8UC3, Scalar.All(255));
        var outcome = MatchFirstPage(_wb.AddPages("blank.pdf", [page], 200), templates);
        Assert.Null(outcome.Match);
    }

    [OcrFact]
    public void Vector_frames_from_pdf_editor_are_detected()
    {
        // Рамки — векторные прямоугольники поверх скана, как рисует редактор PDF.
        _wb.AddTemplate("Чертеж", FormKind.Drawing, Marking.SeparateVector);
        var template = Assert.Single(Load());
        Assert.Equal(3, template.Frames.Count);
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        var blue = template.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        Assert.Equal((fields.Designation.X + 1) / fields.PageWidthMm, blue.X, 0.01);
        Assert.Equal((fields.Designation.Y + 1) / fields.PageHeightMm, blue.Y, 0.01);
    }

    [OcrFact]
    public void Template_file_name_is_the_document_type()
    {
        _wb.AddTemplate("  Спецификация ", FormKind.Specification);
        Assert.Equal("Спецификация", Assert.Single(Load()).Name);
    }

    [OcrFact]
    public void Broken_templates_are_reported_and_do_not_block_good_ones()
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Без зеленой", FormKind.Specification, customize: null, green: new Scalar(255, 255, 255));
        File.WriteAllText(Path.Combine(_wb.Templates, "Поврежденный.pdf"), "это не PDF");

        var loads = TemplateLibrary.LoadFolder(_wb.Templates);

        Assert.NotNull(loads.Single(l => l.FileName == "Чертеж.pdf").Template);
        Assert.Contains("нет зеленой", loads.Single(l => l.FileName == "Без зеленой.pdf").Problem);
        Assert.NotNull(loads.Single(l => l.FileName == "Поврежденный.pdf").Problem);
        foreach (var l in loads) l.Template?.Dispose();
    }

    [OcrFact]
    public void Template_without_frames_is_reported()
    {
        var fields = SyntheticForms.Fields(FormKind.Drawing);
        using var page = SyntheticForms.Render(FormKind.Drawing, "ОБРЗ.000000.000", "Образец", 200);
        SyntheticForms.SavePdf(Path.Combine(_wb.Templates, "Пустой.pdf"), [page], 200);
        var load = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates));
        Assert.Null(load.Template);
        Assert.Contains("не найдено цветных рамок", load.Problem);
    }

    [OcrFact]
    public void Edited_template_is_read_again_on_next_load()
    {
        var path = _wb.AddTemplate("Чертеж", FormKind.Drawing);
        var first = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates)).Template!;
        var before = first.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        first.Dispose();

        // Тот же файл, рамка обозначения сужена и сдвинута на 20 мм вправо.
        File.Delete(path);
        _wb.AddTemplate("Чертеж", FormKind.Drawing, blue: new Scalar(255, 255, 255), customize: (page, f) =>
            SyntheticForms.DrawFrame(page, f.Designation.Inflate(-1) with { X = f.Designation.X + 21, Width = f.Designation.Width - 42 },
                FrameColors.Blue, Workbench.TemplateDpi));

        var second = Assert.Single(TemplateLibrary.LoadFolder(_wb.Templates)).Template!;
        var after = second.Frames.Single(f => f.Role == FieldRole.Designation).Inner;
        second.Dispose();
        Assert.True(after.X - before.X > 0.08);
    }

    [OcrTheory]
    [InlineData(0, 0, 80, 60)]
    [InlineData(15, 12, 40, 30)]
    [InlineData(0, 7, 33, 40)]
    [InlineData(55, 35, 25, 25)]
    public void Cached_line_dilation_preserves_scores_at_region_boundaries(int x, int y, int width, int height)
    {
        using var page = new Mat(60, 80, MatType.CV_8UC1, Scalar.All(0));
        using var template = new Mat(60, 80, MatType.CV_8UC1, Scalar.All(0));
        for (var i = 3; i < 80; i += 11)
        {
            Cv2.Line(page, new Point(i, 0), new Point(i, 59), Scalar.All(255));
            Cv2.Line(template, new Point(i + 2, 0), new Point(i + 2, 59), Scalar.All(255));
        }
        for (var i = 5; i < 60; i += 13)
        {
            Cv2.Line(page, new Point(0, i), new Point(79, i), Scalar.All(255));
            Cv2.Line(template, new Point(0, i + 1), new Point(79, i + 1), Scalar.All(255));
        }

        var area = new Rect(x, y, width, height);
        // Исходный подсчет расширял каждый ROI отдельно. В том числе проверяем линии
        // сразу снаружи ROI: при расширении OpenCV использует соседние точки родительской Mat.
        using var p = new Mat(page, area);
        using var t = new Mat(template, area);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        using var pg = new Mat();
        using var tg = new Mat();
        Cv2.Dilate(p, pg, kernel);
        Cv2.Dilate(t, tg, kernel);
        using var hitT = new Mat();
        using var hitP = new Mat();
        Cv2.BitwiseAnd(t, pg, hitT);
        Cv2.BitwiseAnd(p, tg, hitP);
        var recall = Cv2.CountNonZero(hitT) / (double)Cv2.CountNonZero(t);
        var precision = Cv2.CountNonZero(hitP) / (double)Cv2.CountNonZero(p);
        var reference = 2 * recall * precision / (recall + precision);

        using var pageGrown = PageFeatures.GrowLines(page);
        using var templateGrown = PageFeatures.GrowLines(template);
        Assert.Equal(reference, TemplateMatcher.LineAgreement(page, template, pageGrown, templateGrown, area));
    }

    [OcrTheory]
    [InlineData(1, 1, 32, false)]
    [InlineData(19, 2, 32, false)]
    [InlineData(47, 83, 32, false)]
    [InlineData(300, 500, 32, false)]
    [InlineData(47, 83, 32, true)]
    [InlineData(47, 83, 16, true)]
    public void Exact_hamming_neighbors_equal_bfmatcher_including_strided_rows(int queryCount, int trainCount, int width, bool strided)
    {
        using var queryStorage = new Mat(queryCount, width + (strided ? 8 : 0), MatType.CV_8UC1);
        using var trainStorage = new Mat(trainCount, width + (strided ? 8 : 0), MatType.CV_8UC1);
        var random = new Random(7123);
        foreach (var storage in new[] { queryStorage, trainStorage })
        {
            var bytes = new byte[storage.Rows * storage.Cols];
            random.NextBytes(bytes);
            Marshal.Copy(bytes, 0, storage.Data, bytes.Length);
        }
        using var query = new Mat(queryStorage, new Rect(strided ? 3 : 0, 0, width, queryCount));
        using var train = new Mat(trainStorage, new Rect(strided ? 3 : 0, 0, width, trainCount));
        if (trainCount > 2)
        {
            // Совпавшие дескрипторы должны оставаться двумя разными соседями в порядке строк.
            using var first = new Mat(train, new Rect(0, 0, width, 1));
            using var next = new Mat(train, new Rect(0, 1, width, 1));
            using var exact = new Mat(query, new Rect(0, 0, width, 1));
            first.CopyTo(next);
            first.CopyTo(exact);
        }
        using var bf = new BFMatcher(NormTypes.Hamming);
        var expected = bf.KnnMatch(query, train, 2);

        var actual = TemplateMatcher.NearestTwo(query, train);

        Assert.Equal(expected.Length, actual.Length);
        for (var row = 0; row < actual.Length; row++)
        {
            Assert.Equal(expected[row][0].TrainIdx, actual[row].FirstIndex);
            Assert.Equal(expected[row][0].Distance, actual[row].FirstDistance);
            Assert.Equal(expected[row].Length > 1 ? expected[row][1].TrainIdx : -1, actual[row].SecondIndex);
            Assert.Equal(expected[row].Length > 1 ? expected[row][1].Distance : int.MaxValue, actual[row].SecondDistance);
        }
    }

    [OcrFact]
    public void Hamming_ties_keep_first_two_training_rows_and_empty_input_has_no_neighbors()
    {
        using var empty = new Mat();
        using var query = new Mat(5, 32, MatType.CV_8UC1, Scalar.All(0));
        using var train = new Mat(7, 32, MatType.CV_8UC1, Scalar.All(0));

        Assert.Empty(TemplateMatcher.NearestTwo(empty, train));
        Assert.All(TemplateMatcher.NearestTwo(query, empty), p => Assert.Equal(
            new TemplateMatcher.NearestDescriptor(-1, int.MaxValue, -1, int.MaxValue), p));
        Assert.All(TemplateMatcher.NearestTwo(query, train), p => Assert.Equal(
            new TemplateMatcher.NearestDescriptor(0, 0, 1, 0), p));
    }

    [OcrTheory]
    [InlineData(0.35, 150)]
    [InlineData(0.55, 90)]
    public void Low_contrast_skewed_page_still_selects_the_right_template(double contrast, double brightness)
    {
        _wb.AddTemplate("Чертеж", FormKind.Drawing);
        _wb.AddTemplate("Спецификация", FormKind.Specification);
        var templates = Load();
        using var original = Features(_wb.AddDocument("d.pdf", FormKind.Drawing, "АБВГ.461411.123", "Корпус",
            new ScanOptions { QuarterTurns = 1, SkewDegrees = 2.5, Noise = 0.01 }, seed: 9), out var size);
        var gray = new Mat();
        original.Gray.ConvertTo(gray, MatType.CV_8UC1, contrast, brightness);
        using var features = PageFeatures.Build(gray, original.Scale);

        var outcome = TemplateMatcher.Match(features, size, templates);

        Assert.Equal("Чертеж", outcome.Match?.Template.Name);
        Assert.False(outcome.Ambiguous);
    }
}
