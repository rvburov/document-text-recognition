using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>
/// Третье мнение: оба обычных движка могут одинаково спутать рукописные буквы чертежного
/// шрифта («резьбовая» как «резьдовая»). Модель чертежного шрифта, видящая на том же месте
/// другую букву той же группы, останавливает файл; ее мусорный ответ ничего не решает.
/// Ответы движков управляемые, PDF и области настоящие.
/// </summary>
public sealed class ThirdOpinionTests
{
    [Fact]
    public void Conflict_is_only_an_in_group_letter_swap_with_everything_else_equal()
    {
        Assert.Equal((12, 'д', 'б'), DraftingConfusions.Conflict("Шпилька резьдовая", "Шпилька резьбовая"));
        Assert.Equal((2, 'ь', 'ъ'), DraftingConfusions.Conflict("Раьем", "Раъем"));
        Assert.Null(DraftingConfusions.Conflict("Шпилька резьдовая", "Шпилька резьдовая"));
        Assert.Null(DraftingConfusions.Conflict("Шпилька резьдовая", "Шпилька резьбовая 1"));
        Assert.Null(DraftingConfusions.Conflict("АГБП.563251.001ТУ", "АГЪБ1.5632.51.001ТУ"));
        Assert.Null(DraftingConfusions.Conflict("Корпус", "Кортус"));
    }

    [OcrFact]
    public void Drafting_model_disputing_a_confusable_letter_stops_the_shared_error_of_both_engines()
    {
        var (result, third) = Read("Шпилька резьдовая", "Шпилька резьбовая");
        Assert.True(result.Values is null, $"{result.Values} / {result.Problem} / third calls {third.Calls}");
        Assert.Contains("модель чертежного шрифта читает «б» вместо «д»", result.Problem);
        Assert.Equal(2, third.Calls);
    }

    [OcrFact]
    public void Garbage_from_the_drafting_model_does_not_decide_anything()
    {
        var (result, third) = Read("Шпилька резьбовая", "Шп1лька резь6овая !!");
        Assert.True(result.Values is not null, result.Problem);
        Assert.Equal("Шпилька резьбовая", result.Values.Name);
        Assert.Equal(2, third.Calls);
    }

    [OcrFact]
    public void Value_without_confusable_letters_is_not_read_a_third_time()
    {
        var (result, third) = Read("Шеф", "Шеф", designation: "КМНР.524752.245", thirdDesignation: null);
        Assert.True(result.Values is not null, result.Problem);
        // В «КМНР.524752.245» и «Шеф» нет знаков из групп путаницы: третье чтение не нужно.
        Assert.Equal(0, third.Calls);
    }

    private static (DocumentResult Result, Answers Third) Read(string pairName, string thirdName,
        string designation = "ЮИЛГ.461411.124", string? thirdDesignation = "ЮИЛГ.461411.124")
    {
        using var wb = new Workbench();
        var templateFile = wb.AddTemplate("Чертеж", FormKind.Drawing);
        TemplateRegion[] regions;
        using (var before = TemplateLibrary.Load(templateFile).Template!)
            regions = before.Frames.Where(f => f.Role != FieldRole.Type).Select(f => new TemplateRegion(f.Role, f.Inner)).ToArray();
        Assert.Null(TemplateMarkupStore.Save(templateFile, 0, regions, typeAbsent: true));
        using var template = TemplateLibrary.Load(templateFile).Template!;
        var file = wb.AddDocument("document.pdf", FormKind.Drawing, designation, "Шпилька");
        // Обозначение «ЮИЛГ.461411.124» тоже содержит спорные знаки (И, 1, 6): третья модель
        // читает его первым, затем наименование.
        var third = thirdDesignation is null ? new Answers("Чертежная", thirdName) : new Answers("Чертежная", thirdDesignation, thirdName);
        using var pair = new OcrPair(new Answers("Первый", designation, pairName), new Answers("Второй", designation, pairName),
            () => new OcrPair(third, new Answers("Четвертый")) { UseGostFallback = false })
        { UsePdfText = false, UseThirdOpinion = true };
        return (DocumentReader.Read(file, [template], pair, CancellationToken.None), third);
    }

    private sealed class Answers(string name, params string[] answers) : IOcrEngine
    {
        private readonly Queue<string> _answers = new(answers);
        public string Name => name;
        public int Calls { get; private set; }
        public OcrReading Read(Mat gray)
        {
            Calls++;
            var text = _answers.Count > 0 ? _answers.Dequeue() : "";
            return new OcrReading(Name, text.Length == 0 ? [] : [new OcrLine(text, 0.99, 0, 0, gray.Width, gray.Height)]);
        }
        public void Dispose() { }
    }
}
