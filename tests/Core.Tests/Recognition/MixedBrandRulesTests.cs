using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Tests.Recognition;

/// <summary>Смешанные названия и буквенные фрагменты не становятся догаданными брендами.</summary>
public sealed class MixedBrandRulesTests
{
    [Theory]
    [InlineData("PACE")]
    [InlineData("CORE")]
    [InlineData("МодульPACE")]
    [InlineData("МодульCORE")]
    [InlineData("МодульUSB")]
    [InlineData("МодульPАСЕ")]
    [InlineData("МодульРАСE")]
    [InlineData("PACEМодуль")]
    [InlineData("Модуль2PACE")]
    [InlineData("Модуль-PACE")]
    [InlineData("МодульO0З3B")]
    public void Mixed_names_and_identifiers_keep_their_written_alphabets(string name)
    {
        Assert.Equal(name, TextRules.ResolveMixedWords(name));
        Assert.Equal(name, Parse(name).Value);
    }

    [Theory]
    [InlineData("МодульPACE", "МодульРАСЕ")]
    [InlineData("МодульPАСЕ", "МодульРАСЕ")]
    [InlineData("МодульРАСE", "МодульРАСЕ")]
    [InlineData("PACEМодуль", "РАСЕМодуль")]
    [InlineData("МодульO0", "МодульОО")]
    [InlineData("МодульЗ3", "МодульЗЗ")]
    [InlineData("МодульB", "МодульБ")]
    public void Different_ambiguous_names_do_not_collapse_to_a_confirmed_answer(string first, string second)
    {
        var result = EngineAgreement.Combine(Fields(Parse(first)), Fields(Parse(second)));

        Assert.Null(result.Values);
        Assert.Contains("по-разному", result.Problem);
    }

    [Theory]
    [InlineData("МодульPACE")]
    [InlineData("МодульPАСЕ")]
    [InlineData("PACEМодуль")]
    public void Exactly_confirmed_raw_mixed_name_remains_usable(string name)
    {
        var result = EngineAgreement.Combine(Fields(Parse(name)), Fields(Parse(name)));

        Assert.Equal(name, result.Values?.Name);
    }

    [Fact]
    public void Preserving_a_mixed_name_does_not_bypass_confidence()
    {
        var result = EngineAgreement.Combine(Fields(Parse("МодульPACE", 0.2)), Fields(Parse("МодульPACE")));

        Assert.Null(result.Values);
        Assert.Contains("уверенность", result.Problem);
    }

    private static Parsed Parse(string name, double confidence = 0.95) => FieldParser.ParseName(
        new OcrReading("Тест", [new OcrLine(name, confidence, 0, 0, 500, 40)]), 0.5);

    private static EngineFields Fields(Parsed name) =>
        new("Тест", Parsed.Ok("АБВГ.461411.123"), name, Parsed.Ok(""));
}
