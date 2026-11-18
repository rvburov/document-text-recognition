// SPDX-License-Identifier: Apache-2.0
// Copyright (c) sdcb. General CTC decoding is derived from PaddleSharp 3.3.1:
// https://github.com/sdcb/PaddleSharp/tree/139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6/src/Sdcb.PaddleOCR
// Modified: optional per-field Cyrillic designation alphabet; original probabilities are
// retained and confidence cannot increase by removing disallowed labels.
// Full license: licenses/PaddleSharp-Apache-2.0.txt.
using System.Text;
using DocumentTextRecognition.Core.Recognition;
using OpenCvSharp;
using Sdcb.PaddleOCR;

namespace DocumentTextRecognition.Core.Ocr;

internal static class PaddleCtcDecoder
{
    internal static unsafe PaddleOcrRecognizerResult Decode(ReadOnlySpan<float> data, int characterCount,
        int labelCount, Func<int, string> getLabel, OcrTextProfile profile = OcrTextProfile.General)
    {
        if (characterCount < 0 || labelCount <= 0 || data.Length != checked(characterCount * labelCount))
            throw new ArgumentException("CTC tensor shape does not match its data.", nameof(data));
        if (profile is not (OcrTextProfile.General or OcrTextProfile.CyrillicDesignation))
            throw new ArgumentOutOfRangeException(nameof(profile));
        string?[]? allowed = null;
        if (profile == OcrTextProfile.CyrillicDesignation)
        {
            allowed = new string?[labelCount];
            allowed[0] = ""; // CTC blank always remains available.
            for (var i = 1; i < labelCount; i++)
            {
                var folded = TextRules.FoldToCyrillic(getLabel(i));
                if (IsAllowed(folded)) allowed[i] = folded;
            }
        }

        StringBuilder text = new();
        var characters = new List<RecognizedChar>();
        int previous = 0, originalPrevious = 0, originalLength = 0;
        float score = 0, originalScore = 0, restrictedEvidence = 1;
        fixed (float* pointer = data)
        {
            for (var n = 0; n < characterCount; n++)
            {
                using var row = Mat.FromPixelData(1, labelCount, MatType.CV_32FC1,
                    (IntPtr)(pointer + n * labelCount));
                int[] maxIndex = new int[2];
                row.MinMaxIdx(out double _, out double maximum, new int[0], maxIndex);
                var index = maxIndex[1];
                var probability = (float)maximum;

                if (allowed is not null)
                {
                    if (index > 0 && (!(n > 0 && index == originalPrevious)))
                    {
                        originalScore += probability;
                        originalLength += getLabel(index).Length;
                    }
                    originalPrevious = index;
                    if (allowed[index] is null)
                    {
                        var offset = n * labelCount;
                        index = 0;
                        probability = data[offset];
                        for (var candidate = 1; candidate < labelCount; candidate++)
                        {
                            if (allowed[candidate] is not null && data[offset + candidate] > probability)
                            {
                                index = candidate;
                                probability = data[offset + candidate];
                            }
                        }
                        // A rejected top label is unresolved evidence. Its allowed runner-up
                        // keeps its own raw probability, even if CTC, whitespace trimming or
                        // punctuation parsing later hides it among confident characters.
                        restrictedEvidence = Math.Min(restrictedEvidence, probability);
                    }
                }

                // Collapse on the original label index. Folding two visual aliases does not
                // invent a CTC blank or merge two distinct model labels into one character.
                if (index > 0 && (!(n > 0 && index == previous)))
                {
                    score += probability;
                    var character = allowed is null ? getLabel(index) : allowed[index]!;
                    text.Append(character);
                    characters.Add(new RecognizedChar(character, probability, characters.Count));
                }
                previous = index;
            }
        }
        var confidence = score / text.Length;
        if (allowed is not null)
            confidence = Math.Min(Math.Min(confidence, originalScore / originalLength), restrictedEvidence);
        return new PaddleOcrRecognizerResult(text.ToString(), confidence, characters);
    }

    // Non-letter symbols remain untouched: this is an alphabet restriction, not a formatter.
    // Latin B is deliberately retained as the existing ambiguous B/В/Б marker until agreement.
    private static bool IsAllowed(string label) => label.All(c => !char.IsLetter(c) ||
        TextRules.IsCyrillic(c) || c == TextRules.AmbiguousB);

    /// <summary>The legacy vendor wrapper exposes decoded text, not its probability tensor.
    /// Fold only unambiguous twins; forbidden letters retain their text and force refusal.</summary>
    internal static PaddleOcrRecognizerResult ApplyLegacyProfile(PaddleOcrRecognizerResult reading, OcrTextProfile profile)
    {
        if (profile == OcrTextProfile.General) return reading;
        if (profile != OcrTextProfile.CyrillicDesignation) throw new ArgumentOutOfRangeException(nameof(profile));
        var text = TextRules.FoldToCyrillic(reading.Text);
        var characters = reading.Chars?.Select(c => new RecognizedChar(
            TextRules.FoldToCyrillic(c.Character), c.Score, c.Index)).ToArray();
        return new PaddleOcrRecognizerResult(text, IsAllowed(text) ? reading.Score : 0, characters!);
    }
}
