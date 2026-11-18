using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Tests.Recognition;

/// <summary>Только проверенный видимый PDF задаёт алфавит; raster peer подтверждает буквы и числа.</summary>
public sealed class PdfAlphabetAgreementTests
{
    [Theory]
    [InlineData("МодульPACE", "МодульРАСЕ")]
    [InlineData("МодульРАСЕ", "МодульPACE")]
    [InlineData("МодульB", "МодульВ")]
    [InlineData("МодульВ", "МодульB")]
    [InlineData("МодульB", "МодульB")]
    [InlineData("CORE", "СОRЕ")]
    [InlineData("AHKMOTXY", "АНКМОТХУ")]
    [InlineData("Цех acopexy", "Цех асореху")]
    public void Pdf_side_keeps_its_explicit_alphabet_when_every_visual_alias_is_confirmed(string pdfName, string rasterName)
    {
        var result = EngineAgreement.CombinePdfText(Fields(Parsed.Ok(pdfName)), Fields(Parsed.Ok(rasterName)));

        Assert.Equal(pdfName, result.Values?.Name);
        Assert.Null(result.Problem);
    }

    [Fact]
    public void Parsed_mixed_brand_is_preserved_without_changing_the_ordinary_ocr_agreement()
    {
        var pdf = Fields(FieldParser.CheckName(["МодульPACE"]));
        var raster = Fields(ReadName("МодульРАСЕ", 0.95));

        Assert.Equal("МодульPACE", EngineAgreement.CombinePdfText(pdf, raster).Values?.Name);
        Assert.Null(EngineAgreement.Combine(pdf, raster).Values);
        Assert.False(EngineAgreement.AreConfirmed(pdf.Name, raster.Name));
    }

    [Theory]
    [InlineData("МодульB", "МодульБ")]
    [InlineData("МодульО", "Модуль0")]
    [InlineData("МодульЗ", "Модуль3")]
    [InlineData("МодульУ", "МодульЧ")]
    [InlineData("МодульЙ", "МодульИ")]
    [InlineData("МодульI", "Модуль1")]
    [InlineData("МодульM", "МодульИ")]
    [InlineData("МодульС", "МодульG")]
    [InlineData("МодульA", "Модульа")]
    [InlineData("МодульPACE", "МодульPACE2")]
    [InlineData("Плата 1489 МГц", "Плата 1488 МГц")]
    public void Different_shapes_case_digits_or_text_are_not_visual_aliases(string pdfName, string rasterName)
    {
        var result = EngineAgreement.CombinePdfText(Fields(Parsed.Ok(pdfName)), Fields(Parsed.Ok(rasterName)));

        Assert.Null(result.Values);
        Assert.Contains("по-разному", result.Problem);
    }

    [Theory]
    [InlineData(0.49)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Pdf_alphabet_does_not_override_the_raster_confidence_gate(double confidence)
    {
        var result = EngineAgreement.CombinePdfText(Fields(FieldParser.CheckName(["МодульPACE"])),
            Fields(ReadName("МодульРАСЕ", confidence)));

        Assert.Null(result.Values);
        Assert.Contains("уверенность", result.Problem);
    }

    [Fact]
    public void Raster_at_the_existing_threshold_can_confirm_a_pdf_visual_alias()
    {
        var result = EngineAgreement.CombinePdfText(Fields(FieldParser.CheckName(["МодульPACE"])),
            Fields(ReadName("МодульРАСЕ", 0.5)));

        Assert.Equal("МодульPACE", result.Values?.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Failed_field_validation_on_either_side_is_not_repaired_by_visual_aliases(bool pdfFailed)
    {
        var failed = Fields(Parsed.Fail("проверка поля не пройдена"));
        var valid = Fields(Parsed.Ok("МодульPACE"));
        var result = pdfFailed ? EngineAgreement.CombinePdfText(failed, valid) :
            EngineAgreement.CombinePdfText(valid, failed);

        Assert.Null(result.Values);
        Assert.Contains("проверка поля", result.Problem);
    }

    [Fact]
    public void Missing_raster_text_does_not_confirm_pdf_unicode()
    {
        var empty = FieldParser.ParseName(new OcrReading("raster", []), 0.5);
        var result = EngineAgreement.CombinePdfText(Fields(Parsed.Ok("МодульPACE")), Fields(empty));

        Assert.Null(result.Values);
        Assert.Contains("только одним способом", result.Problem);
    }

    [Fact]
    public void Document_type_uses_the_pdf_alphabet_but_missing_types_still_get_the_default()
    {
        var pdf = Fields(Parsed.Ok("Корпус")) with { Type = Parsed.Ok("Тип PACE") };
        var raster = Fields(Parsed.Ok("Корпус")) with { Type = Parsed.Ok("Тип РАСЕ") };

        Assert.Equal("Тип PACE", EngineAgreement.CombinePdfText(pdf, raster).Values?.Type);
        Assert.Null(EngineAgreement.Combine(pdf, raster).Values);
        Assert.Equal(DocTypes.WhenMissing,
            EngineAgreement.CombinePdfText(pdf with { Type = Parsed.Ok("") },
                raster with { Type = Parsed.Ok("") }).Values?.Type);
        Assert.Null(EngineAgreement.CombinePdfText(pdf, raster with { Type = Parsed.Ok("") }).Values);
    }

    [Fact]
    public void Designation_is_still_canonical_cyrillic_instead_of_following_pdf_latin()
    {
        var pdf = Fields(Parsed.Ok("МодульPACE")) with
        {
            Designation = FieldParser.ParseDesignationText("AX.700.004"),
        };
        var raster = Fields(Parsed.Ok("МодульРАСЕ")) with
        {
            Designation = FieldParser.ParseDesignationText("АХ.700.004"),
        };

        Assert.Equal("АХ.700.004", EngineAgreement.CombinePdfText(pdf, raster).Values?.Designation);
        Assert.Null(EngineAgreement.CombinePdfText(pdf,
            raster with { Designation = Parsed.Ok("АХ.700.009") }).Values);
        var unresolved = pdf with { Designation = Parsed.Ok("B.700.004") };
        Assert.Null(EngineAgreement.CombinePdfText(unresolved, unresolved).Values);
        Assert.Equal("В.700.004", EngineAgreement.CombinePdfText(unresolved,
            raster with { Designation = Parsed.Ok("В.700.004") }).Values?.Designation);
    }

    private static Parsed ReadName(string text, double confidence) => FieldParser.ParseName(
        new OcrReading("raster", [new OcrLine(text, confidence, 0, 0, 300, 40)]), 0.5);

    private static EngineFields Fields(Parsed name) =>
        new("source", Parsed.Ok("АБВГ.461411.123"), name, Parsed.Ok(""));
}
