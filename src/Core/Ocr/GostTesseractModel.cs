using System.Security.Cryptography;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>Pinned, separately trained specialist. It never replaces independent raster confirmation.</summary>
internal static class GostTesseractModel
{
    internal const string FileName = "rus_gost.traineddata";
    internal const string Languages = "rus_gost+eng";
    // Selected on development before the separately sealed evaluation corpus.
    internal const bool RawDesignation = true;
    internal const string Sha256 = "c92b7c889f0bd4fb07ca90148c011be7a1865be2b8bba036fa1a86d875a5add0";

    internal static void Validate(string tessdata)
    {
        var path = Path.Combine(tessdata, FileName);
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма модели чертёжного шрифта Tesseract не совпадает с поставкой.");
    }
}
