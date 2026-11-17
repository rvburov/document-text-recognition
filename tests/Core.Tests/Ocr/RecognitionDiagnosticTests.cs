using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Диагностика использует те же реальные PDF/области, но ответы движков здесь управляемые:
/// тест должен гарантированно воспроизвести и объяснить разногласие, независимо от версии OCR.</summary>
public sealed class RecognitionDiagnosticTests
{
    [OcrFact]
    public void Disagreement_keeps_both_answers_and_readable_crops_without_changing_the_pdf()
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Чертеж", FormKind.Drawing);
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        var file = wb.AddDocument("document.pdf", FormKind.Drawing, "ЮИЛГ.461411.123", "Корпус");
        var bytes = File.ReadAllBytes(file);
        using var pair = new OcrPair(
            new Answers("Первый", "ЮИЛГ.461411.123", "Корпус", ""),
            new Answers("Второй", "ЮИЛГ.461411.123", "Крышка", ""));
        var log = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(file, [template], pair, CancellationToken.None, log.Add);

        Assert.Null(result.Values);
        Assert.Contains("по-разному", result.Problem);
        Assert.Contains(log, d => d.Stage == "Совмещение" && d.Message.Contains("Чертеж"));
        var fields = log.Where(d => d.Field is not null).Select(d => d.Field!).ToArray();
        Assert.Equal(3, fields.Length);
        var name = Assert.Single(fields, f => f.Role == FieldRole.Name);
        Assert.Equal("Корпус", name.First.RawText);
        Assert.Equal("Крышка", name.Second.RawText);
        Assert.Null(name.First.Problem);
        Assert.Contains(log, d => d.Stage == "Сверка движков" && d.Message.Contains("по-разному"));
        foreach (var field in fields)
        {
            using var image = Cv2.ImDecode(field.CropPng, ImreadModes.Grayscale);
            Assert.False(image.Empty());
            Assert.True(image.Width > 20 && image.Height > 20);
        }
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.True(File.Exists(file));
    }

    [OcrFact]
    public void Explicit_missing_type_never_reads_a_third_region_and_explains_the_default()
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Спецификация", FormKind.Specification);
        TemplateRegion[] regions;
        using (var before = TemplateLibrary.Load(templateFile).Template!)
            regions = before.Frames.Where(f => f.Role != FieldRole.Type).Select(f => new TemplateRegion(f.Role, f.Inner)).ToArray();
        Assert.Null(TemplateMarkupStore.Save(templateFile, 0, regions, typeAbsent: true));
        using var template = TemplateLibrary.Load(templateFile).Template!;
        Assert.NotNull(template);
        Assert.True(template.TypeAbsent);
        var file = wb.AddDocument("document.pdf", FormKind.Specification, "ВКЕМ.461411.200", "Модуль связи");
        var first = new Answers("Первый", "ВКЕМ.461411.200", "Модуль связи");
        var second = new Answers("Второй", "ВКЕМ.461411.200", "Модуль связи");
        using var pair = new OcrPair(first, second);
        var log = new List<RecognitionDiagnostic>();

        var result = DocumentReader.Read(file, [template], pair, CancellationToken.None, log.Add);

        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal(DocTypes.WhenMissing, result.Values.Type);
        Assert.Equal(2, first.Calls);
        Assert.Equal(2, second.Calls);
        Assert.Equal(2, log.Count(d => d.Field is not null));
        Assert.Contains(log, d => d.Stage == "Области" && d.Message.Contains("по настройке шаблона"));
    }

    private sealed class Answers(string name, params string[] answers) : IOcrEngine
    {
        private readonly Queue<string> _answers = new(answers);
        public string Name => name;
        public int Calls { get; private set; }
        public OcrReading Read(Mat gray)
        {
            Calls++;
            Assert.False(gray.Empty());
            var text = _answers.Dequeue();
            return new OcrReading(Name, text.Length == 0 ? [] : [new OcrLine(text, 0.99, 0, 0, gray.Width, gray.Height)]);
        }
        public void Dispose() { }
    }
}
