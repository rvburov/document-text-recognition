namespace DocumentTextRecognition.Core.Recognition;

/// <summary>
/// Буквы, которые движки путают на чертежных шрифтах ГОСТ (рукописные «б», «в», «д», «т»,
/// «з» как цифра 3 и т. п.). Группы получены прогоном алфавита (тест AlphabetSweepTests):
/// все строчные и прописные буквы, цифры и знаки в шрифтах ГОСТ тип А и OpenGOST тип А и Б.
/// Оба движка пары нередко ошибаются здесь одинаково, поэтому их согласие не доказывает
/// верность. Если третья модель уверенно прочитала на том же месте другую букву той же
/// группы при совпадающих соседних знаках, значение считается неоднозначным.
/// </summary>
internal static class DraftingConfusions
{
    public static readonly IReadOnlyList<string> Groups =
    [
        "бвд6", "ъь", "зэ3", "йиуuл", "тm", "ао",
        "БВ68", "ЗЭ3", "ЙИ", "П1", "У9", "О0", "ЬЪ",
    ];

    private static readonly Dictionary<char, int> GroupOf = Groups
        .SelectMany((g, i) => g.Select(c => (c, i)))
        .GroupBy(p => p.c).ToDictionary(g => g.Key, g => g.First().i);

    public static bool IsConfusable(char c) => GroupOf.ContainsKey(c);

    public static bool SameGroup(char a, char b) =>
        a != b && GroupOf.TryGetValue(a, out var x) && GroupOf.TryGetValue(b, out var y) && x == y;

    /// <summary>
    /// Спор о букве: <paramref name="other"/> отличается от <paramref name="accepted"/> только
    /// заменами букв внутри групп путаницы (той же длины, остальные знаки совпадают). Возвращает
    /// первое такое место. null — ответы совпадают или различаются иначе: такой ответ третьей
    /// модели считается ее собственной ошибкой и в споре не участвует.
    /// </summary>
    public static (int Index, char Accepted, char Other)? Conflict(string accepted, string other)
    {
        if (accepted == other || accepted.Length != other.Length) return null;
        (int, char, char)? first = null;
        for (var i = 0; i < accepted.Length; i++)
        {
            if (accepted[i] == other[i]) continue;
            if (!SameGroup(accepted[i], other[i])) return null;
            first ??= (i, accepted[i], other[i]);
        }
        return first;
    }
}
