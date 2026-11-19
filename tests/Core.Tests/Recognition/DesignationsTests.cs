using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Tests.Recognition;

public sealed class DesignationsTests
{
    private static OcrReading Reading(string text) =>
        new("Тест", [new OcrLine(text, 0.95, 0, 0, 500, 40)]);

    [Theory]
    [InlineData("AX.700.004", "АХ.700.004")]
    [InlineData("Y2.700.004", "У2.700.004")]
    [InlineData("8A1.234.567", "8А1.234.567")]
    [InlineData("ТКC.700.004", "ТКС.700.004")]
    [InlineData("AБCГ.461411.123C3", "АБСГ.461411.123С3")]
    public void Designation_twins_are_cyrillic_for_every_format(string raw, string expected)
    {
        Assert.Equal(expected, FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
    }

    [Theory]
    [InlineData("АБВГ.112345.001 ПЭЗ", "АБВГ.112345.001ПЭ3")]
    [InlineData("АБВГ.112345.001ПЭЗ", "АБВГ.112345.001ПЭ3")]
    [InlineData("АБВГ.112345.001 ЭЗ", "АБВГ.112345.001Э3")]
    [InlineData("АБВГ.112345.001ЭО", "АБВГ.112345.001Э0")]
    [InlineData("АБВГ.112345.001ЗИ", "АБВГ.112345.001ЗИ")]
    [InlineData("МГТУ.469335.001 33", "МГТУ.469335.001Э3")]
    [InlineData("МГТУ.469335.001 П33", "МГТУ.469335.001ПЭ3")]
    [InlineData("ГИДР.123456.001-01.01 33", "ГИДР.123456.001-01.01Э3")]
    [InlineData("МГТУ.469335.001П33", "МГТУ.469335.001ПЭ3")]
    public void Document_code_letters_and_digits_confused_on_drafting_fonts_are_restored(string raw, string expected)
    {
        Assert.Equal(expected, FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
    }

    [Theory]
    [InlineData("АБВГ.112345.001 ПЗЭ")]
    [InlineData("АБВГ.112345.001 ЗЭ")]
    [InlineData("АБВГ.112345.001 93")]
    [InlineData("АБВГ.112345.001 1393")]
    [InlineData("МИЭМ.467999.001733")]
    [InlineData("МГТУ.469335.00133")]
    public void Unknown_code_or_code_read_as_digits_is_not_a_designation(string raw)
    {
        Assert.False(FieldParser.ParseDesignation(Reading(raw), 0.5).IsOk);
    }

    [Theory]
    [InlineData("ЦШЩЭ.909090.111ПЗ")]
    [InlineData("ЦШЩЭ.909090.111П3")]
    [InlineData("АБВГ.123456.001ВО")]
    [InlineData("АБВГ.123456.001 ТО")]
    public void Codes_that_look_identical_on_drafting_fonts_are_refused_from_ocr_but_kept_in_pdf_text(string raw)
    {
        var ocr = FieldParser.ParseDesignation(Reading(raw), 0.5);
        Assert.False(ocr.IsOk);
        Assert.Contains("не различить", ocr.Problem);
        Assert.True(FieldParser.ParseDesignationText(raw).IsOk);
    }

    [Theory]
    [InlineData("МПСУ.464414.22-01Э3", "МПСУ.464414.22-01Э3")]
    [InlineData("ИУ4.11.03.03", "ИУ4.11.03.03")]
    [InlineData("2436.181092.5167", "2436.181092.5167")]
    public void Industry_designations_keep_their_short_groups(string raw, string expected)
    {
        Assert.Equal(expected, FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
    }

    [Theory]
    [InlineData("DX.700.004")]
    [InlineData("USB.700.004")]
    [InlineData("R2.700.004")]
    [InlineData("N.123.456")]
    [InlineData("G.123.456")]
    [InlineData("L.123.456")]
    [InlineData("F.123.456")]
    [InlineData("ADLG.461411.123")]
    public void Nonhomoglyph_latin_is_rejected_in_every_designation_format(string raw)
    {
        Assert.Empty(Designations.FindAll(Designations.Prepare(raw)));
        Assert.False(FieldParser.ParseDesignation(Reading(raw), 0.5).IsOk);
    }

    [Fact]
    public void Combined_field_canonicalizes_only_the_designation()
    {
        var (designation, name, type) = FieldParser.ParseCombined(Reading("AX.700.004 Плата USB"), 0.5);

        Assert.Equal("АХ.700.004", designation.Value);
        Assert.Equal("Плата USB", name.Value);
        Assert.Equal("", type.Value);
        Assert.False(FieldParser.ParseCombined(Reading("DX.700.004 Плата USB"), 0.5).Designation.IsOk);
    }

    [Theory]
    [InlineData("BBBB.461411.123")]
    [InlineData("B.700.004")]
    public void Unresolved_latin_b_cannot_be_a_final_designation(string value)
    {
        var parsed = FieldParser.ParseDesignation(Reading(value), 0.5);
        Assert.True(parsed.IsOk); // A second engine may still confirm a Cyrillic В.
        Assert.True(EngineAgreement.AreConfirmed(parsed, parsed)); // Generic field checks stay unchanged.

        var result = EngineAgreement.Combine(Fields(parsed), Fields(parsed));

        Assert.Null(result.Values);
        Assert.Contains("кириллиц", result.Problem);
    }

    [Theory]
    [InlineData("B.700.004", "В.700.004")]
    [InlineData("BBBB.461411.123", "ВВВВ.461411.123")]
    public void Other_engine_may_confirm_latin_b_as_cyrillic_ve(string raw, string expected)
    {
        var result = EngineAgreement.Combine(
            Fields(FieldParser.ParseDesignation(Reading(raw), 0.5)),
            Fields(FieldParser.ParseDesignation(Reading(expected), 0.5)));

        Assert.Equal(expected, result.Values?.Designation);
    }

    [Fact]
    public void Designation_policy_keeps_latin_product_names()
    {
        var designation = FieldParser.ParseDesignation(Reading("AX.700.004"), 0.5);
        var result = EngineAgreement.Combine(Fields(designation, "Board USB B"), Fields(designation, "Board USB B"));

        Assert.Equal("АХ.700.004", result.Values?.Designation);
        Assert.Equal("Board USB B", result.Values?.Name);
    }

    [Fact]
    public void Canonicalization_does_not_override_low_confidence_or_digit_disagreement()
    {
        var low = new OcrReading("Тест", [new OcrLine("AX.700.004", 0.2, 0, 0, 500, 40)]);
        Assert.Contains("уверенность", FieldParser.ParseDesignation(low, 0.5).Problem);

        var result = EngineAgreement.Combine(
            Fields(FieldParser.ParseDesignation(Reading("AX.700.004"), 0.5)),
            Fields(FieldParser.ParseDesignation(Reading("АХ.700.009"), 0.5)));
        Assert.Null(result.Values);
        Assert.Contains("по-разному", result.Problem);
        Assert.False(FieldParser.ParseDesignation(Reading("AX.7О0.004"), 0.5).IsOk);
    }

    private static EngineFields Fields(Parsed designation, string name = "Плата УПЧ 1489 МГц") =>
        new("Тест", designation, Parsed.Ok(name), Parsed.Ok(""));
}
