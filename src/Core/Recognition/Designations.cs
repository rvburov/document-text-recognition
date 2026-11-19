using System.Text.RegularExpressions;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Найденное обозначение: каноническая запись, правило и место в исходном тексте.</summary>
internal sealed record DesignationMatch(string Value, string Rule, int Index, int Length);

/// <summary>
/// Правила десятичного обозначения. Формат не ограничен одним примером — поддержаны:
/// <list type="bullet">
/// <item>ЕСКД (ГОСТ 2.201): <c>АБВГ.461411.123</c>, с исполнением <c>-01</c>, групповым
/// <c>-01.02</c>, кодом документа <c>СБ</c>, <c>Э3</c>, <c>ПЭ3</c>… и <c>-ЛУ/-УЛ</c>;</item>
/// <item>ЕСПД (ГОСТ 19.103): <c>АБВГ.12345-01 34 01</c>, <c>АБВГ.12345-01</c>;</item>
/// <item>технологические: <c>АБВГ.12345.12345</c>;</item>
/// <item>отраслевые и заводские «через точки»: 1–8 букв и цифр (хотя бы одна буква), затем
/// две-четыре группы цифр через точку (<c>ВЛ2.700.004-01</c>, <c>8А1.234.567</c>), либо
/// только цифры вида <c>8.112.006</c> (средняя группа — не короче трех цифр, чтобы дата
/// «12.05.2020» не принималась за обозначение).</item>
/// </list>
/// Каноническая запись: прописные буквы, без пробелов у точек и дефисов, тире → дефис.
/// Во всех форматах буквы обозначения — кириллица: однозначные латинские двойники переводятся
/// в кириллицу, прочая латиница (D, F, L…) не принимается. Неоднозначная B остается до сверки
/// со вторым движком. Цифры и буквы между собой не заменяются.
/// </summary>
internal static class Designations
{
    private const string L = "A-Za-zА-Яа-я";

    /// <summary>Коды документов ЕСКД, которые OCR может отделить пробелом: «…123 СБ» → «…123СБ».</summary>
    public static readonly HashSet<string> DocumentCodes = new(StringComparer.Ordinal)
    {
        "СБ", "ВО", "ТЧ", "ГЧ", "МЭ", "МЧ", "УЧ", "ВС", "ВД", "ВП", "ВИ", "ДП", "ПТ", "ЭП", "ТП", "ПЗ",
        "ТУ", "ПМ", "ТБ", "РР", "ИМ", "ЭД", "РЭ", "ИМ", "ФО", "ПС", "ЭТ", "ЗИ", "ВЭ", "ТО", "ИС", "КТ", "Д",
        "Э0", "Э1", "Э2", "Э3", "Э4", "Э5", "Э6", "Э7", "ПЭ3", "ПЭ4", "ПЭ5", "Г3", "К3", "П3", "С3", "Л3",
    };

    private static readonly Regex NumberedCode = new(@"^(?:ПЭ|П[ГКЛСЭ]|[ЭГКПСЛВЕРХТШ])\d$|^Д\d{1,2}$", RegexOptions.Compiled);

    /// <summary>Код документа после номера (СБ, Э3, ПЭ3, Д1…). Неизвестный код — не часть
    /// обозначения: «…123З» (буква З вместо цифры 3) обозначением не считается.</summary>
    public static bool IsKnownCode(string code) => DocumentCodes.Contains(code) || NumberedCode.IsMatch(code);

    // Каждое правило — по тексту, где пробелы у точек и дефисов уже убраны.
    private static readonly (string Name, Regex Rx, bool GostOrg)[] Rules =
    [
        ("ЕСКД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.(?<num>\d{{6}})\.(?<ver>\d{{3,4}})(?:-\d{{2}}\.\d{{2}})?(?:-\d{{1,3}})?" +
            $@"(?<code>[{L}]{{1,3}}\d{{0,2}})?(?:-(?:ЛУ|УЛ|лу|ул))?(?![{L}0-9]|\.\d)", RegexOptions.Compiled), true),
        ("ЕСПД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.\d{{5}}-\d{{2}}(?: \d{{2}} \d{{2}})?(?:-\d)?(?:-(?:ЛУ|УЛ|лу|ул))?(?![{L}0-9]|\.\d)",
            RegexOptions.Compiled), true),
        ("ТД", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}]{{4}})\.\d{{5}}\.\d{{5}}(?:-\d{{1,3}})?(?![{L}0-9]|\.\d)", RegexOptions.Compiled), true),
        ("отраслевое", new Regex(
            $@"(?<![{L}0-9.])(?<org>[{L}0-9]{{1,8}})(?:\.\d{{2,8}}){{2,4}}(?:-\d{{1,3}})?(?<code>[{L}]{{1,3}}\d{{0,2}})?(?![{L}0-9]|\.\d)",
            RegexOptions.Compiled), false),
    ];

    /// <summary>Подготовка текста к поиску: нормализация строки наименования
    /// (<see cref="TextRules.NormalizeName"/>) и убранные пробелы у точек.</summary>
    public static string Prepare(string text) => Squeeze(TextRules.NormalizeName(text.Replace('\n', ' ')), out _);

    /// <summary>
    /// Убрать пробелы рядом с точками («АБВГ. 461411 .123» → «АБВГ.461411.123»).
    /// <paramref name="map"/>[i] — индекс символа результата в исходной строке; последний
    /// элемент — длина исходной строки.
    /// </summary>
    public static string Squeeze(string normalized, out int[] map)
    {
        var sb = new System.Text.StringBuilder(normalized.Length);
        var m = new List<int>(normalized.Length + 1);
        for (var i = 0; i < normalized.Length; i++)
        {
            var c = normalized[i];
            if (c == ' ')
            {
                var prev = sb.Length > 0 ? sb[^1] : '\0';
                var next = i + 1 < normalized.Length ? normalized[i + 1] : '\0';
                if (prev == '.' || next == '.') continue;
                // Пробел внутри группы цифр между двумя точками («.967 747.»): в группе
                // обозначения пробелов не бывает, Tesseract ставит их по ширине цифр.
                if (char.IsDigit(prev) && char.IsDigit(next) && InsideDotGroup(sb, normalized, i)) continue;
            }
            sb.Append(c);
            m.Add(i);
        }
        m.Add(normalized.Length);
        map = [.. m];
        return sb.ToString();
    }

    private static bool InsideDotGroup(System.Text.StringBuilder emitted, string text, int space)
    {
        var j = emitted.Length - 1;
        while (j >= 0 && char.IsDigit(emitted[j])) j--;
        if (j < 0 || emitted[j] != '.') return false;
        var k = space + 1;
        while (k < text.Length && (char.IsDigit(text[k]) || text[k] == ' ' && k + 1 < text.Length && char.IsDigit(text[k + 1]))) k++;
        return k < text.Length && text[k] == '.';
    }

    /// <summary>Все обозначения в тексте (одна строка, см. <see cref="Prepare"/>), по порядку.</summary>
    public static List<DesignationMatch> FindAll(string prepared)
    {
        var found = new List<DesignationMatch>();
        // Строка вида ГОСТ (АААА.000000.000), не прошедшая проверку кода организации, не
        // становится «отраслевым» обозначением: латиница в таком месте — ошибка чтения.
        var rejected = new List<(int Index, int Length)>();
        foreach (var (name, rx, gost) in Rules)
        {
            foreach (Match m in rx.Matches(prepared))
            {
                bool Overlaps(int index, int length) => m.Index < index + length && index < m.Index + m.Length;
                if (found.Any(f => Overlaps(f.Index, f.Length)) || rejected.Any(r => Overlaps(r.Index, r.Length))) continue;
                var value = Canonical(m, gost, prepared, out var length);
                if (value is null)
                {
                    if (gost) rejected.Add((m.Index, m.Length));
                    continue;
                }
                found.Add(new DesignationMatch(value, name, m.Index, length));
            }
        }
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        return found;
    }

    /// <summary>Каноническая запись совпадения; null — не годится (например, латинская буква
    /// без кириллического двойника). <paramref name="length"/> — сколько символов текста занято, с
    /// присоединенным кодом документа.</summary>
    private static string? Canonical(Match m, bool gost, string text, out int length)
    {
        length = m.Length;
        var value = TextRules.FoldToCyrillic(m.Value.ToUpperInvariant());
        var org = m.Groups["org"].Value.ToUpperInvariant();

        // Правило относится только к найденному обозначению, включая отраслевые форматы.
        // B не угадываем: второй движок должен подтвердить В; финальная сверка не оставит латиницу.
        if (value.Any(c => char.IsLetter(c) && !TextRules.IsCyrillic(c) && c != TextRules.AmbiguousB)) return null;

        if (!gost)
        {
            if (org.All(char.IsDigit))
            {
                // Только цифры: средняя группа не короче трех цифр (отсекает даты).
                var groups = value.Split('.', '-');
                if (groups.Length < 3 || groups[1].Length < 3) return null;
            }
            else if (!org.Any(char.IsLetter)) return null;

            // Начало как у ЕСКД (АААА.000000.), но длинный хвост цифр: так выглядит код документа,
            // прочитанный цифрами («…001ПЭ3» как «…001733»). Правило ЕСКД его уже не приняло.
            var parts = value.Split('.');
            if (org.Length == 4 && org.All(TextRules.IsCyrillic) && parts.Length >= 3 && parts[1].Length == 6
                && parts[1].All(char.IsDigit) && parts[2].TakeWhile(char.IsDigit).Count() > 4)
                return null;
        }

        // Код документа ЕСКД, отделенный пробелом: «…123 СБ».
        if (!m.Groups["code"].Success || m.Groups["code"].Length == 0)
        {
            var rest = text[(m.Index + m.Length)..];
            var code = Regex.Match(rest, $@"^ ([{L}0-9]{{1,3}})(?![{L}0-9])");
            if (code.Success)
            {
                var c = DigitInCode(TextRules.FoldToCyrillic(code.Groups[1].Value.ToUpperInvariant()), DocumentCodes.Contains);
                if (DocumentCodes.Contains(c))
                {
                    value += c;
                    length += code.Length;
                }
            }
        }
        else
        {
            // Код документа — только известный; латинские двойники в нем — кириллица (коды ЕСКД).
            var codeGroup = m.Groups["code"];
            var c = DigitInCode(TextRules.FoldToCyrillic(codeGroup.Value.ToUpperInvariant()), IsKnownCode);
            if (!IsKnownCode(c)) return null;
            value = value[..(codeGroup.Index - m.Index)] + c + value[(codeGroup.Index - m.Index + codeGroup.Length)..];
        }

        return value;
    }

    /// <summary>
    /// Код документа с буквами и цифрами, перепутанными на чертежном шрифте: «ПЭЗ» → «ПЭ3»,
    /// «ЭО» → «Э0», «33» → «Э3», «П33» → «ПЭ3». Цифра в кодах ЕСКД бывает только последней, а
    /// набор кодов закрыт, поэтому замена делается, лишь когда исходный код неизвестен, а
    /// исправленный известен. Второй движок по-прежнему должен прочитать то же самое.
    /// </summary>
    internal static string DigitInCode(string code, Func<string, bool> known)
    {
        if (code.Length < 2 || known(code)) return code;
        // Буквы кода стоят до последнего знака: «3» на их месте на чертежном шрифте — «Э».
        var chars = code.ToCharArray();
        for (var i = 0; i < chars.Length - 1; i++)
            if (chars[i] == '3') chars[i] = 'Э';
        chars[^1] = chars[^1] switch { 'З' => '3', 'О' => '0', var c => c };
        var candidate = new string(chars);
        return known(candidate) ? candidate : code;
    }

    // Пары существующих кодов, которые на чертежных шрифтах выглядят одинаково: буква З и
    // цифра 3, буква О и цифра 0 нарисованы почти одной линией (прогон алфавита).
    private static readonly (string Letter, string Digit, string What)[] AmbiguousCodes =
    [
        ("ПЗ", "П3", "пояснительная записка или схема пневматическая принципиальная"),
        ("ВО", "В0", "чертеж общего вида или схема вакуумная объединенная"),
        ("ТО", "Т0", "техническое описание или схема тепловая объединенная"),
    ];

    /// <summary>
    /// Код документа, который по изображению не отличить от другого существующего кода
    /// («ПЗ» и «П3»). Для чтения изображения это отказ с причиной; в тексте PDF код записан
    /// точно и такой проверки не требует. null — код однозначен.
    /// </summary>
    public static string? AmbiguousCode(string designation)
    {
        foreach (var (letter, digit, what) in AmbiguousCodes)
            if (Regex.IsMatch(designation, $@"[0-9]({letter}|{digit})$"))
                return $"код документа «{letter}» и «{digit}» на изображении не различить ({what}): переименуйте файл вручную";
        return null;
    }

    /// <summary>Похож ли текст на обозначение (для проверки, что наименование не им является).</summary>
    public static bool ContainsDesignation(string text) => FindAll(Prepare(text)).Count > 0;
}
