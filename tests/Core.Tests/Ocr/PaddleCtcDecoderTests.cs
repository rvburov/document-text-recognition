// SPDX-License-Identifier: Apache-2.0
// The independent General-profile oracle below preserves the PaddleSharp 3.3.1 CTC loop:
// https://github.com/sdcb/PaddleSharp/tree/139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6/src/Sdcb.PaddleOCR
// Copyright (c) sdcb. Full license: licenses/PaddleSharp-Apache-2.0.txt.
using System.Runtime.InteropServices;
using System.Text;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Controlled probability tensors distinguish alphabet selection from probability
/// renormalization and ensure restricted CTC cannot gain confidence by deleting evidence.</summary>
public sealed class PaddleCtcDecoderTests
{
    [OcrFact]
    public void General_keeps_latin_name_letters_without_folding_or_restriction()
    {
        string[] labels = ["", "D", "L", "F", "N", "A", "M", "E"];
        var data = Peaks(labels.Length, (1, .91f), (2, .82f), (3, .93f), (4, .84f), (5, .95f), (6, .86f), (7, .97f));

        var actual = Decode(data, labels, OcrTextProfile.General);

        Assert.Equal("DLFNAME", actual.Text);
        AssertResultBits(VendorGeneral(data, labels), actual);
    }

    [OcrFact]
    public void General_matches_vendor_bits_for_argmax_ties_multichar_labels_and_CTC_blanks()
    {
        string[] labels = ["", "A", "D", "Ж", "0", ".", " ", "AB"];
        var rows = new float[][]
        {
            [.10f, .45f, .45f, 0, 0, 0, 0, 0], // Equal maxima retain the vendor's first-index rule.
            [.10f, .90f, 0, 0, 0, 0, 0, 0],    // Repeated label collapses.
            [1, 0, 0, 0, 0, 0, 0, 0],
            [.18f, 0, 0, 0, 0, 0, 0, .82f],  // Denominator is decoded string length, as upstream.
            [.35f, 0, 0, 0, 0, 0, .65f, 0],
            [.25f, 0, .75f, 0, 0, 0, 0, 0],
            [1, 0, 0, 0, 0, 0, 0, 0],
        };
        var data = rows.SelectMany(r => r).ToArray();

        // Omitted profile must retain the original decoder, including raw score bits.
        var actual = PaddleCtcDecoder.Decode(data, rows.Length, labels.Length, index => labels[index]);

        Assert.Equal("AAB D", actual.Text);
        AssertResultBits(VendorGeneral(data, labels), actual);
    }

    [OcrFact]
    public void Designation_folds_only_approved_visual_twins_with_original_probability()
    {
        const string latin = "ACEHKMOPTXY";
        const string cyrillic = "АСЕНКМОРТХУ";
        for (var i = 0; i < latin.Length; i++)
        {
            string[] labels = ["", latin[i].ToString(), cyrillic[i].ToString()];
            float[] data = [.02f, .91f, .07f];

            var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

            Assert.Equal(cyrillic[i].ToString(), actual.Text);
            AssertFloatBits(.91f, actual.Score);
            var character = Assert.Single(actual.Chars);
            Assert.Equal(cyrillic[i].ToString(), character.Character);
            AssertFloatBits(.91f, character.Score);
            Assert.Equal(0, character.Index);
            Assert.Equal(latin[i].ToString(), Decode(data, labels, OcrTextProfile.General).Text);
        }
    }

    [OcrFact]
    public void Ambiguous_latin_B_is_preserved_until_independent_engine_agreement()
    {
        string[] labels = ["", "B", "В", "Б"];
        float[] data = [.05f, .80f, .10f, .05f];

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal("B", actual.Text);
        AssertFloatBits(.80f, actual.Score);
        Assert.Equal("B", Assert.Single(actual.Chars).Character);
    }

    [OcrTheory]
    [InlineData("D")]
    [InlineData("F")]
    [InlineData("L")]
    [InlineData("N")]
    public void Forbidden_latin_top_class_uses_raw_cyrillic_runner_up_probability(string forbidden)
    {
        string[] labels = ["", forbidden, "Д"];
        float[] data = [.01f, .89f, .10f];
        Assert.Equal(forbidden, Decode(data, labels, OcrTextProfile.General).Text);

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal("Д", actual.Text);
        AssertFloatBits(.10f, actual.Score); // Not .10/(.10+.01), and not the forbidden class's .89.
        AssertFloatBits(.10f, Assert.Single(actual.Chars).Score);
        Assert.True(actual.Score < .5f);
    }

    [OcrFact]
    public void Forbidden_class_replaced_by_blank_cannot_inflate_remaining_text_confidence()
    {
        string[] labels = ["", "D", "Я"];
        float[] data = [.01f, .01f, .98f, .15f, .80f, .05f];
        Assert.Equal("ЯD", Decode(data, labels, OcrTextProfile.General).Text);

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal("Я", actual.Text);
        AssertFloatBits(.15f, actual.Score); // The omitted letter contributes its raw blank evidence.
        AssertFloatBits(.98f, Assert.Single(actual.Chars).Score);
        Assert.True(actual.Score < .5f);
    }

    [OcrFact]
    public void Forbidden_class_collapsed_into_previous_label_keeps_its_raw_runner_up_evidence()
    {
        string[] labels = ["", "D", "А"];
        float[] data = [.05f, .05f, .90f, .05f, .60f, .35f];
        var general = Decode(data, labels, OcrTextProfile.General);
        Assert.Equal("АD", general.Text);
        AssertFloatBits(.75f, general.Score);

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal("А", actual.Text); // The runner-up А repeats the preceding selected label.
        AssertFloatBits(.35f, actual.Score); // Neither .90 nor unrestricted mean .75 retains the omitted evidence.
        AssertFloatBits(.90f, Assert.Single(actual.Chars).Score);
        Assert.True(actual.Score < .5f);
    }

    [OcrFact]
    public void Collapsing_a_forbidden_prefix_letter_cannot_confirm_a_different_valid_designation()
    {
        const string wrongDesignation = "АБВГ.461411.123";
        var labels = new[] { "", "D" }.Concat(wrongDesignation.Distinct().Select(c => c.ToString())).ToArray();
        var rows = new List<float[]>();
        for (var i = 0; i < wrongDesignation.Length; i++)
        {
            var index = Array.IndexOf(labels, wrongDesignation[i].ToString());
            rows.Add(Peaks(labels.Length, (index, .90f)));
            if (i == 0)
            {
                var forbidden = new float[labels.Length];
                forbidden[0] = .05f;
                forbidden[1] = .60f;
                forbidden[index] = .35f; // Selecting А collapses it into the preceding А.
                rows.Add(forbidden);
            }
            rows.Add(Peaks(labels.Length, (0, 1f))); // Preserve repeated digits in the number.
        }
        var data = rows.SelectMany(r => r).ToArray();
        Assert.Equal("АDБВГ.461411.123", Decode(data, labels, OcrTextProfile.General).Text);

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal(wrongDesignation, actual.Text); // Valid syntax alone must not hide the lost prefix letter.
        AssertFloatBits(.35f, actual.Score);
        AssertFloatBits(.35f, PaddleEngine.KnownLineConfidence(actual));
        var reading = new OcrReading("PaddleOCR", [new OcrLine(actual.Text, actual.Score, 0, 0, 500, 40)]);
        var parsed = FieldParser.ParseDesignation(reading, .5);
        Assert.False(parsed.IsOk);
        Assert.Contains("уверенность", parsed.Problem);
        Assert.True(FieldParser.ParseDesignation(reading with
            { Lines = [reading.Lines[0] with { Confidence = .90 }] }, .5).IsOk);

        var name = Parsed.Ok("Корпус");
        var type = Parsed.Ok("");
        var agreement = EngineAgreement.Combine(
            new EngineFields("Tesseract", Parsed.Ok(wrongDesignation), name, type),
            new EngineFields("PaddleOCR", parsed, name, type));
        Assert.Null(agreement.Values); // Even an independently matching string cannot bypass the low score.
    }

    [OcrFact]
    public void Additional_forbidden_region_decoded_as_blank_cannot_disappear_beside_valid_designation()
    {
        const string designation = "АБВГ.461411.123";
        string[] labels = ["", "D", "А"];
        float[] data = [.15f, .80f, .05f];
        var forbidden = Decode(data, labels, OcrTextProfile.CyrillicDesignation);
        Assert.Empty(forbidden.Text);
        Assert.False(float.IsFinite(forbidden.Score));
        PaddleOcrResultRegion[] regions =
        [
            new(new RotatedRect(new Point2f(250, 20), new Size2f(500, 40), 0), designation, .95f, []),
            new(new RotatedRect(new Point2f(50, 90), new Size2f(100, 40), 0),
                forbidden.Text, forbidden.Score, forbidden.Chars),
        ];

        var restricted = PaddleEngine.Collect(regions, OcrTextProfile.CyrillicDesignation);

        Assert.Equal(2, restricted.Lines.Count);
        Assert.Contains(restricted.Lines, line => line.Text.Length == 0 && line.Confidence == 0);
        var parsed = FieldParser.ParseDesignation(restricted, .5);
        Assert.False(parsed.IsOk);
        Assert.Contains("уверенность", parsed.Problem);
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok(designation), parsed));

        // The General profile deliberately retains the existing vendor collection contract.
        var general = PaddleEngine.Collect(regions);
        Assert.Equal(designation, Assert.Single(general.Lines).Text);
        Assert.True(FieldParser.ParseDesignation(general, .5).IsOk);
    }

    [OcrTheory]
    [InlineData("АБВ?.461411.123", "Г", "АБВГ.461411.123")]
    [InlineData("?АБВГ.461411.123", " ", " АБВГ.461411.123")]
    [InlineData("?АБВГ.461411.123", "-", "-АБВГ.461411.123")]
    public void Weak_allowed_runner_up_cannot_hide_in_a_long_valid_designation_or_ignored_punctuation(
        string pattern, string replacement, string expectedText)
    {
        var labels = new[] { "", "D" }.Concat(expectedText.Distinct().Select(c => c.ToString())).ToArray();
        var rows = new List<float[]>();
        foreach (var character in pattern)
        {
            if (character == '?')
            {
                var forbidden = new float[labels.Length];
                forbidden[0] = .05f;
                forbidden[1] = .60f;
                forbidden[Array.IndexOf(labels, replacement)] = .35f;
                rows.Add(forbidden);
            }
            else
                rows.Add(Peaks(labels.Length, (Array.IndexOf(labels, character.ToString()), .95f)));
            rows.Add(Peaks(labels.Length, (0, 1f)));
        }
        var data = rows.SelectMany(r => r).ToArray();
        Assert.Equal(pattern.Replace('?', 'D'), Decode(data, labels, OcrTextProfile.General).Text);

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal(expectedText, actual.Text);
        AssertFloatBits(.35f, actual.Score);
        var rect = new RotatedRect(new Point2f(250, 20), new Size2f(500, 40), 0);
        var reading = PaddleEngine.Collect(
            [new PaddleOcrResultRegion(rect, actual.Text, actual.Score, actual.Chars)],
            OcrTextProfile.CyrillicDesignation);
        Assert.Equal(.35f, Assert.Single(reading.Lines).Confidence);
        var parsed = FieldParser.ParseDesignation(reading, .5);
        Assert.False(parsed.IsOk);
        Assert.Contains("уверенность", parsed.Problem);
        Assert.False(EngineAgreement.AreConfirmed(Parsed.Ok("АБВГ.461411.123"), parsed));
        // The same text at high confidence has valid syntax; the guard must preserve the
        // weak evidence even when a space is trimmed or punctuation is ignored by parsing.
        Assert.True(FieldParser.ParseDesignation(reading with
            { Lines = [reading.Lines[0] with { Confidence = .95 }] }, .5).IsOk);
    }

    [OcrFact]
    public void Digits_and_punctuation_keep_text_and_original_probabilities()
    {
        const string text = "01.234-()/";
        var labels = new[] { "" }.Concat(text.Select(c => c.ToString())).ToArray();
        var data = Peaks(labels.Length, Enumerable.Range(1, text.Length).Select(i => (i, .80f + i * .01f)).ToArray());

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal(text, actual.Text);
        AssertResultBits(VendorGeneral(data, labels), actual);
    }

    [OcrFact]
    public void Repeated_labels_collapse_and_blank_separates_identical_characters_in_both_profiles()
    {
        string[] labels = ["", "А", "Б"];
        var data = Peaks(labels.Length, (1, .90f), (1, .80f), (0, 1f), (1, .70f), (2, .85f), (2, .60f), (0, 1f));
        var expected = VendorGeneral(data, labels);
        Assert.Equal("ААБ", expected.Text);
        Assert.Equal(new[] { 0, 1, 2 }, expected.Chars.Select(c => c.Index));

        AssertResultBits(expected, Decode(data, labels, OcrTextProfile.General));
        AssertResultBits(expected, Decode(data, labels, OcrTextProfile.CyrillicDesignation));
    }

    [OcrFact]
    public void Folding_visual_aliases_does_not_merge_distinct_CTC_label_indices()
    {
        string[] labels = ["", "A", "А"];
        var data = Peaks(labels.Length, (1, .90f), (2, .80f));

        var actual = Decode(data, labels, OcrTextProfile.CyrillicDesignation);

        Assert.Equal("АА", actual.Text);
        Assert.Equal(2, actual.Chars.Count);
        AssertFloatBits((.90f + .80f) / 2, actual.Score);
        AssertFloatBits(.90f, actual.Chars[0].Score);
        AssertFloatBits(.80f, actual.Chars[1].Score);
    }

    [OcrFact]
    public void Legacy_general_preserves_text_score_and_character_evidence()
    {
        var original = new PaddleOcrRecognizerResult("DLFN", .8125f,
            [new("D", .91f, 2), new("L", .82f, 5), new("F", .73f, 8), new("N", .79f, 11)]);

        var actual = PaddleCtcDecoder.ApplyLegacyProfile(original, OcrTextProfile.General);

        AssertResultBits(original, actual);
        Assert.Same(original.Chars, actual.Chars);
    }

    [OcrFact]
    public void Legacy_designation_folds_twins_and_keeps_ambiguous_B_probabilities_and_indices()
    {
        var original = new PaddleOcrRecognizerResult("ACB", .875f,
            [new("A", .91f, 4), new("C", .83f, 7), new("B", .87f, 11)]);
        var expected = new PaddleOcrRecognizerResult("АСB", .875f,
            [new("А", .91f, 4), new("С", .83f, 7), new("B", .87f, 11)]);

        var actual = PaddleCtcDecoder.ApplyLegacyProfile(original, OcrTextProfile.CyrillicDesignation);

        AssertResultBits(expected, actual);
        Assert.Equal("ACB", original.Text);
        Assert.Equal("A", original.Chars[0].Character);
    }

    [OcrFact]
    public void Legacy_forbidden_latin_is_retained_with_zero_result_score_and_original_character_evidence()
    {
        var original = new PaddleOcrRecognizerResult("D/F-LN", .95f,
            [new("D", .91f, 1), new("/", .96f, 3), new("F", .94f, 5),
             new("-", .99f, 7), new("L", .93f, 9), new("N", .97f, 11)]);
        var expected = new PaddleOcrRecognizerResult(original.Text, 0, original.Chars);

        var actual = PaddleCtcDecoder.ApplyLegacyProfile(original, OcrTextProfile.CyrillicDesignation);

        AssertResultBits(expected, actual);
        AssertFloatBits(.95f, original.Score);
    }

    private static PaddleOcrRecognizerResult Decode(float[] data, string[] labels, OcrTextProfile profile)
        => PaddleCtcDecoder.Decode(data, data.Length / labels.Length, labels.Length, index => labels[index], profile);

    private static float[] Peaks(int labelCount, params (int Label, float Probability)[] timesteps)
    {
        var data = new float[timesteps.Length * labelCount];
        for (var i = 0; i < timesteps.Length; i++)
        {
            var (label, probability) = timesteps[i];
            data[i * labelCount] = label == 0 ? probability : 1 - probability;
            if (label != 0) data[i * labelCount + label] = probability;
        }
        return data;
    }

    private static void AssertFloatBits(float expected, float actual)
        => Assert.Equal(BitConverter.SingleToInt32Bits(expected), BitConverter.SingleToInt32Bits(actual));

    private static void AssertResultBits(PaddleOcrRecognizerResult expected, PaddleOcrRecognizerResult actual)
    {
        Assert.Equal(expected.Text, actual.Text);
        AssertFloatBits(expected.Score, actual.Score);
        Assert.Equal(expected.Chars.Count, actual.Chars.Count);
        for (var i = 0; i < expected.Chars.Count; i++)
        {
            Assert.Equal(expected.Chars[i].Character, actual.Chars[i].Character);
            Assert.Equal(expected.Chars[i].Index, actual.Chars[i].Index);
            AssertFloatBits(expected.Chars[i].Score, actual.Chars[i].Score);
        }
    }

    // Pinned upstream loop, independent of PaddleCtcDecoder and profile-selection helpers.
    // Preserve its float accumulation, string-length denominator and OpenCV tie-breaking.
    private static PaddleOcrRecognizerResult VendorGeneral(float[] data, string[] labels)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var pointer = handle.AddrOfPinnedObject();
            var text = new StringBuilder();
            int previous = 0, characterIndex = 0;
            float score = 0;
            var characters = new List<RecognizedChar>();
            for (var n = 0; n < data.Length / labels.Length; n++)
            {
                using var row = Mat.FromPixelData(1, labels.Length, MatType.CV_32FC1,
                    pointer + n * labels.Length * sizeof(float));
                int[] maxIndex = new int[2];
                row.MinMaxIdx(out double _, out double maximum, new int[0], maxIndex);
                if (maxIndex[1] > 0 && (!(n > 0 && maxIndex[1] == previous)))
                {
                    score += (float)maximum;
                    var character = labels[maxIndex[1]];
                    text.Append(character);
                    characters.Add(new RecognizedChar(character, (float)maximum, characterIndex));
                    characterIndex++;
                }
                previous = maxIndex[1];
            }
            return new PaddleOcrRecognizerResult(text.ToString(), score / text.Length, characters);
        }
        finally { handle.Free(); }
    }
}
