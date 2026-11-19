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
    [InlineData("АБВГ.112345.001 ТО", "АБВГ.112345.001ТО")]
    [InlineData("АБВГ.112345.001ЗИ", "АБВГ.112345.001ЗИ")]
    public void Digit_read_as_letter_twin_at_the_end_of_a_document_code_is_restored(string raw, string expected)
    {
        Assert.Equal(expected, FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
    }

    [Theory]
    [InlineData("АБВГ.112345.001 ПЗЭ")]
    [InlineData("АБВГ.112345.001 ЗЭ")]
    [InlineData("АБВГ.112345.ОО1 СБ")]
    public void Only_the_last_code_character_may_become_a_digit(string raw)
    {
        Assert.NotEqual("АБВГ.112345.001ПЭ3", FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
        Assert.NotEqual("АБВГ.112345.001Э3", FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
        Assert.NotEqual("АБВГ.112345.001СБ", FieldParser.ParseDesignation(Reading(raw), 0.5).Value);
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
