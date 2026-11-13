using DocumentTextRecognition.Core.Ocr;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Значение поля, прочитанное одним движком, или причина, почему его нет.</summary>
internal readonly record struct Parsed(string? Value, string? Problem)
{
    public static Parsed Ok(string value) => new(value, null);
    public static Parsed Fail(string problem) => new(null, problem);
    public bool IsOk => Value is not null;
}

/// <summary>Что один движок прочитал в полях документа.</summary>
internal sealed record EngineFields(string Engine, Parsed Designation, Parsed Name, Parsed Type);

/// <summary>
/// Разбор текста областей в обозначение и наименование — отдельно по каждому движку.
/// Ничего не додумывается: нет обозначения по правилам — нет обозначения.
/// </summary>
internal static class FieldParser
{
    public const int MaxNameLength = 180;
    public const int MaxNameLines = 5;

    /// <summary>Синяя область: ровно одно обозначение и ничего, кроме него.</summary>
    public static Parsed ParseDesignation(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty) return Parsed.Fail("область обозначения пустая или текст не читается");
        if (reading.MinConfidence < minConfidence) return Parsed.Fail("низкая уверенность распознавания обозначения");

        var prepared = Designations.Prepare(reading.Text);
        var found = Designations.FindAll(prepared);
        if (found.Count == 0) return Parsed.Fail("обозначение не прошло проверку формата");
        if (found.Select(f => f.Value).Distinct().Count() > 1 || found.Count > 1)
            return Parsed.Fail("в области обозначения несколько обозначений");

        var match = found[0];
        var rest = prepared.Remove(match.Index, match.Length);
        if (TextRules.HasAlnum(rest)) return Parsed.Fail("в области обозначения кроме обозначения есть другой текст");
        return Parsed.Ok(match.Value);
    }

    /// <summary>Зеленая область: весь текст — наименование.</summary>
    public static Parsed ParseName(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty) return Parsed.Fail("наименование отсутствует (область пустая или текст не читается)");
        if (reading.MinConfidence < minConfidence) return Parsed.Fail("низкая уверенность распознавания наименования");
        return CheckName(reading.Lines.Select(l => l.Text).ToList());
    }

    /// <summary>Желтая область: тип документа. Пустая область значит «тип не написан»:
    /// пустая строка, дальше ставится <see cref="DocTypes.WhenMissing"/>.</summary>
    public static Parsed ParseType(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty) return Parsed.Ok("");
        if (reading.MinConfidence < minConfidence) return Parsed.Fail("низкая уверенность распознавания типа документа");
        return CheckType(reading.Lines.Select(l => l.Text).ToList());
    }

    /// <summary>Проверка типа документа: буквы есть, строк не больше трех, без обозначения
    /// внутри; тип из справочника пишется как в справочнике.</summary>
    public static Parsed CheckType(IReadOnlyList<string> lines)
    {
        // Строки без букв (черточки, точки, следы линий бланка) за текст не считаются: поле, где
        // кроме них ничего нет, пустое.
        var meaningful = lines.Select(TextRules.NormalizeName).Where(l => l.Count(char.IsLetter) >= 2).ToList();
        if (meaningful.Count == 0) return Parsed.Ok("");
        if (meaningful.Count > 3)
            return Parsed.Fail($"в области типа документа {meaningful.Count} строк, похоже на таблицу");
        var type = TextRules.ResolveMixedWords(TextRules.JoinLines(meaningful));
        if (type.Count(char.IsLetter) < 2) return Parsed.Fail("тип документа не содержит слов");
        if (type.Length > DocTypes.MaxLength) return Parsed.Fail($"тип документа длиннее {DocTypes.MaxLength} символов");
        if (Designations.ContainsDesignation(type)) return Parsed.Fail("в области типа документа найдено обозначение");
        return Parsed.Ok(DocTypes.Canonical(type) ?? type);
    }

    /// <summary>
    /// Красная область: ровно одно обозначение; наименование и тип документа — текст по одну
    /// сторону от него (строки выше или ниже, либо остаток той же строки). Текст по обе
    /// стороны — разделение неоднозначно. Тип отделяется от наименования по справочнику
    /// <see cref="DocTypes"/>; не найден — тип не написан.
    /// </summary>
    public static (Parsed Designation, Parsed Name, Parsed Type) ParseCombined(OcrReading reading, double minConfidence)
    {
        var (designation, name, rest) = ParseCombinedLines(reading, minConfidence);
        if (rest is null) return (designation, name, name.IsOk ? Parsed.Ok("") : name);
        return (designation, name, rest.Value);
    }

    private static (Parsed Designation, Parsed Name, Parsed? Type) ParseCombinedLines(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty)
        {
            var empty = Parsed.Fail("общая область пустая или текст не читается");
            return (empty, empty, empty);
        }
        if (reading.MinConfidence < minConfidence)
        {
            var low = Parsed.Fail("низкая уверенность распознавания");
            return (low, low, low);
        }

        var lines = reading.Lines.Select(l => TextRules.NormalizeName(l.Text)).Where(l => l.Length > 0).ToList();
        var hits = new List<(int Line, DesignationMatch Match, int Start, int End)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var squeezed = Designations.Squeeze(lines[i], out var map);
            foreach (var m in Designations.FindAll(squeezed))
                hits.Add((i, m, map[m.Index], map[m.Index + m.Length - 1] + 1));
        }

        if (hits.Count == 0)
        {
            var none = Parsed.Fail("в общей области не найдено обозначение по правилам формата");
            var noName = Parsed.Fail("наименование не отделено: нет обозначения");
            return (none, noName, noName);
        }
        if (hits.Count > 1)
        {
            var many = Parsed.Fail("в общей области несколько обозначений");
            var noName = Parsed.Fail("наименование не отделено: несколько обозначений");
            return (many, noName, noName);
        }

        var (lineIndex, match, start, end) = hits[0];
        var line = lines[lineIndex];
        var before = line[..start].Trim();
        var after = line[end..].Trim();

        var above = lines.Take(lineIndex).Append(before).Where(TextRules.HasAlnum).ToList();
        var below = new[] { after }.Concat(lines.Skip(lineIndex + 1)).Where(TextRules.HasAlnum).ToList();

        var designation = Parsed.Ok(match.Value);
        if (above.Count > 0 && below.Count > 0)
        {
            var split = Parsed.Fail("общую область нельзя однозначно разделить: текст и до, и после обозначения");
            return (designation, split, split);
        }
        var nameLines = above.Count > 0 ? above : below;
        if (nameLines.Count == 0)
        {
            var noName = Parsed.Fail("наименование отсутствует (в общей области только обозначение)");
            return (designation, noName, noName);
        }

        // Тип документа: последняя строка, если она начинается словом типа (и строк больше
        // одной), иначе известный тип в конце последней строки.
        var last = nameLines[^1];
        if (nameLines.Count > 1 && (DocTypes.Canonical(last) is not null || DocTypes.StartsLikeType(last)))
            return (designation, CheckName(nameLines.Take(nameLines.Count - 1).ToList()), CheckType([last]));
        if (DocTypes.SplitSuffix(TextRules.ResolveMixedWords(last)) is { } tail && TextRules.HasAlnum(tail.Before))
        {
            var withoutType = nameLines.Take(nameLines.Count - 1).Append(tail.Before).ToList();
            return (designation, CheckName(withoutType), Parsed.Ok(tail.Type));
        }
        return (designation, CheckName(nameLines), null);
    }

    /// <summary>Проверка наименования: буквы есть, строк немного, нет обозначения внутри,
    /// длина разумная.</summary>
    public static Parsed CheckName(IReadOnlyList<string> lines)
    {
        var meaningful = lines.Where(TextRules.HasAlnum).ToList();
        if (meaningful.Count == 0) return Parsed.Fail("наименование отсутствует");
        if (meaningful.Count > MaxNameLines)
            return Parsed.Fail($"в области наименования {meaningful.Count} строк, похоже на таблицу, а не на наименование");

        var name = TextRules.ResolveMixedWords(TextRules.JoinLines(meaningful));
        if (name.Count(char.IsLetter) < 2) return Parsed.Fail("наименование не содержит слов");
        if (name.Length > MaxNameLength) return Parsed.Fail($"наименование длиннее {MaxNameLength} символов, похоже на случайный текст");
        if (Designations.ContainsDesignation(name)) return Parsed.Fail("в области наименования найдено обозначение");
        return Parsed.Ok(name);
    }
}
