using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Tests.Recognition;

public sealed class FieldParserConfidenceTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Every_field_rejects_a_nonfinite_line_even_beside_a_confident_line(double confidence)
    {
        AssertConfidenceProblem(FieldParser.ParseDesignation(Reading("AX.700.", "004", confidence), 0.5));
        AssertConfidenceProblem(FieldParser.ParseName(Reading("Плата", "USB", confidence), 0.5));
        AssertConfidenceProblem(FieldParser.ParseType(Reading("Сборочный", "чертеж", confidence), 0.5));

        var (designation, name, type) = FieldParser.ParseCombined(
            Reading("АБВГ.461411.123", "Плата USB", confidence), 0.5);
        AssertConfidenceProblem(designation);
        AssertConfidenceProblem(name);
        AssertConfidenceProblem(type);
    }

    [Fact]
    public void Finite_line_at_the_threshold_still_passes_every_field()
    {
        Assert.True(FieldParser.ParseDesignation(Reading("АБВГ.461411.", "123", 0.5), 0.5).IsOk);
        Assert.True(FieldParser.ParseName(Reading("Плата", "USB", 0.5), 0.5).IsOk);
        Assert.True(FieldParser.ParseType(Reading("Сборочный", "чертеж", 0.5), 0.5).IsOk);
        var (designation, name, type) = FieldParser.ParseCombined(
            Reading("АБВГ.461411.123", "Плата USB", 0.5), 0.5);
        Assert.True(designation.IsOk);
        Assert.True(name.IsOk);
        Assert.True(type.IsOk);
    }

    private static OcrReading Reading(string first, string second, double secondConfidence) =>
        new("Тест", [new OcrLine(first, 0.95, 0, 0, 500, 40),
            new OcrLine(second, secondConfidence, 0, 40, 500, 80)]);

    private static void AssertConfidenceProblem(Parsed parsed)
    {
        Assert.False(parsed.IsOk);
        Assert.Contains("уверенность", parsed.Problem);
    }
}
