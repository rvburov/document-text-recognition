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

    public static (FieldValues? Values, string? Problem) Combine(EngineFields first, EngineFields second)
    {
        var designation = Field("обозначение", first.Engine, first.Designation, second.Engine, second.Designation);
        if (designation.Problem is not null) return (null, designation.Problem);
        var name = Field("наименование", first.Engine, first.Name, second.Engine, second.Name);
        if (name.Problem is not null) return (null, name.Problem);
        var type = Field("тип документа", first.Engine, first.Type, second.Engine, second.Type);
        if (type.Problem is not null) return (null, type.Problem);
        // Тип не написан на скане (оба способа видят пустое поле): ставится тип по умолчанию.
        var typeValue = type.Value!.Length == 0 ? DocTypes.WhenMissing : type.Value!;
        return (new FieldValues(designation.Value!, name.Value!, typeValue), null);
    }

    private static (string? Value, string? Problem) Field(string what, string e1, Parsed a, string e2, Parsed b)
    {
        if (!a.IsOk && !b.IsOk)
            return (null, a.Problem == b.Problem ? a.Problem : $"{a.Problem}; {b.Problem}");
        if (!a.IsOk) return (null, $"{what} прочитано только одним способом распознавания, второй: {a.Problem}");
        if (!b.IsOk) return (null, $"{what} прочитано только одним способом распознавания, второй: {b.Problem}");
        if (a.Value!.Length == 0 && b.Value!.Length == 0) return ("", null);
        if (a.Value!.Length == 0 || b.Value!.Length == 0)
            return (null, $"{what} прочитано только одним способом распознавания: «{Quote(a.Value!.Length == 0 ? b.Value! : a.Value!)}»");
        var (merged, problem) = TextRules.MergeTwins(a.Value!, b.Value!);
        if (problem is not null) return (null, $"{what}: {problem}");
        if (merged is null)
            return (null, $"{what} прочитано по-разному: «{Quote(a.Value!)}» и «{Quote(b.Value!)}»");
        return (merged, null);
    }

    private static string Quote(string s) => s.Length <= QuoteLength ? s : s[..QuoteLength] + "…";
}
