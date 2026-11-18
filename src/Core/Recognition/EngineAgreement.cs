namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Подтвержденные поля документа.</summary>
internal sealed record FieldValues(string Designation, string Name, string Type);

/// <summary>
/// Сверка двух движков. Поле принимается, только если и Tesseract, и PaddleOCR прочитали его,
/// оно прошло проверки (<see cref="FieldParser"/>) и после нормализации значения совпали символ
/// в символ. Единственное допущение — латинская «B» против кириллической «В» на том же месте:
/// второй движок подтверждает «В» (<see cref="TextRules.MergeTwins"/>). Любое другое
/// расхождение, в том числе в одной букве, регистре или алфавите, — пропуск файла с причиной:
/// выбирать «более уверенный» ответ нельзя, шкалы уверенности у движков разные и не сравнимы.
/// </summary>
internal static class EngineAgreement
{
    /// <summary>Сколько символов прочитанного текста показывать в причине пропуска.</summary>
    public const int QuoteLength = 60;

    /// <summary>Та же проверка отдельного поля, что используется окончательной сверкой.</summary>
    internal static bool AreConfirmed(Parsed first, Parsed second) =>
        Field("поле", "первый", first, "второй", second).Problem is null;

    public static (FieldValues? Values, string? Problem) Combine(EngineFields first, EngineFields second)
        => CombineCore(first, second, pdfAlphabet: false);

    /// <summary>Visible PDF Unicode supplies the alphabet; the raster reader must still
    /// confirm every shape. Only fixed Latin/Cyrillic twins are equivalent here.</summary>
    internal static (FieldValues? Values, string? Problem) CombinePdfText(EngineFields pdf, EngineFields raster)
        => CombineCore(pdf, raster, pdfAlphabet: true);

    // Low-confidence or invalid OCR may be retried with another raster algorithm.
    // A valid conflicting reading is evidence against the PDF map and must not be
    // discarded merely because a later recognizer agrees with that map.
    internal static bool HasPdfTextConflict(EngineFields pdf, EngineFields raster) =>
        Conflicts(pdf.Designation, raster.Designation, false) ||
        Conflicts(pdf.Name, raster.Name, true) || Conflicts(pdf.Type, raster.Type, true);

    private static bool Conflicts(Parsed pdf, Parsed raster, bool pdfAlphabet) =>
        raster.IsOk && Field("поле", "PDF", pdf, "OCR", raster, pdfAlphabet).Problem is not null;

    private static (FieldValues? Values, string? Problem) CombineCore(EngineFields first, EngineFields second, bool pdfAlphabet)
    {
        var designation = Field("обозначение", first.Engine, first.Designation, second.Engine, second.Designation);
        if (designation.Problem is not null) return (null, designation.Problem);
        // Одинаковая латинская B у обоих движков еще не подтверждает В или Б, даже если
        // весь буквенный префикс состоит из B. Наименования могут содержать латиницу.
        if (designation.Value!.Any(c => char.IsLetter(c) && !TextRules.IsCyrillic(c)))
            return (null, "обозначение содержит неподтвержденные буквы вне кириллицы");
        var name = Field("наименование", first.Engine, first.Name, second.Engine, second.Name, pdfAlphabet);
        if (name.Problem is not null) return (null, name.Problem);
        var type = Field("тип документа", first.Engine, first.Type, second.Engine, second.Type, pdfAlphabet);
        if (type.Problem is not null) return (null, type.Problem);
        // Тип не написан на скане (оба способа видят пустое поле): ставится тип по умолчанию.
        var typeValue = type.Value!.Length == 0 ? DocTypes.WhenMissing : type.Value!;
        return (new FieldValues(designation.Value!, name.Value!, typeValue), null);
    }

    private static (string? Value, string? Problem) Field(string what, string e1, Parsed a, string e2, Parsed b,
        bool pdfAlphabet = false)
    {
        if (!a.IsOk && !b.IsOk)
            return (null, a.Problem == b.Problem ? a.Problem : $"{a.Problem}; {b.Problem}");
        if (!a.IsOk) return (null, $"{what} прочитано только одним способом распознавания, второй: {a.Problem}");
        if (!b.IsOk) return (null, $"{what} прочитано только одним способом распознавания, второй: {b.Problem}");
        if (a.Value!.Length == 0 && b.Value!.Length == 0) return ("", null);
        if (a.Value!.Length == 0 || b.Value!.Length == 0)
            return (null, $"{what} прочитано только одним способом распознавания: «{Quote(a.Value!.Length == 0 ? b.Value! : a.Value!)}»");
        if (pdfAlphabet && TextRules.SameVisualAlphabet(a.Value!, b.Value!)) return (a.Value!, null);
        var (merged, problem) = TextRules.MergeTwins(a.Value!, b.Value!);
        if (problem is not null) return (null, $"{what}: {problem}");
        if (merged is null)
            return (null, $"{what} прочитано по-разному: «{Quote(a.Value!)}» и «{Quote(b.Value!)}»");
        return (merged, null);
    }

    private static string Quote(string s) => s.Length <= QuoteLength ? s : s[..QuoteLength] + "…";
}
