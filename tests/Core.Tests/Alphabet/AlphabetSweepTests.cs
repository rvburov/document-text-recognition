using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Tests.Ocr;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;
using Xunit.Abstractions;

namespace DocumentTextRecognition.Tests.Alphabet;

/// <summary>
/// Прогон алфавита: все буквы русского алфавита, цифры и знаки обозначений в чертежных шрифтах
/// (ГОСТ тип А, OpenGOST тип А и Б с наклоном и без) и в обычном шрифте, чистые и «сканы».
/// Каждая строка читается теми же движками и тем же кодом, что в программе: обычная пара,
/// затем пара для чертежного шрифта. Тест считает, какие буквы каждый движок путает и с чем,
/// и что в итоге приняла бы программа. Отчет: <c>DTR_ALPHABET_OUTPUT</c>.
/// </summary>
[Collection("OCR")]
public sealed class AlphabetSweepTests(OcrFixture ocr, ITestOutputHelper output)
{
    internal enum Outcome { Exact, Refused, Wrong }

    [Fact]
    public void Every_letter_and_digit_is_covered_several_times()
    {
        var coverage = AlphabetSamples.Coverage();
        var thin = coverage.Where(p => p.Value < 2).Select(p => $"{p.Key}: {p.Value}").ToArray();
        Assert.True(thin.Length == 0, "мало вхождений: " + string.Join(", ", thin));
        foreach (var line in AlphabetSamples.Lines())
            Assert.True(Expected(line) is not null, $"эталон не проходит правила программы: {line.Text}");
    }

    [OcrFact]
    public void Alphabet_sweep_reports_confusions_and_accepts_no_new_wrong_line()
    {
        var lines = AlphabetSamples.Lines();
        var gost = ocr.Pair.GostPair() ?? throw new InvalidOperationException("нет пары для чертежного шрифта");
        var outputRoot = Environment.GetEnvironmentVariable("DTR_ALPHABET_OUTPUT");
        var stats = new SweepStats();
        var wrong = new List<string>();
        var runs = new List<object>();

        foreach (var font in AlphabetFont.All.Where(f => f.Available))
        foreach (var condition in AlphabetCondition.All)
        {
            var crops = lines.Select((line, i) =>
            {
                using var bgr = AlphabetSamples.Render(line.Text, font, line.CapHeightMm, condition, seed: i + 1);
                return FieldCrop.Prepare(bgr);
            }).ToArray();
            try
            {
                if (!string.IsNullOrWhiteSpace(outputRoot))
                    SaveSheet(Path.Combine(outputRoot, "samples", $"{font.Key}-{condition.Key}.png"), crops.Take(12).ToArray());
                var roles = lines.Select(l => l.Role == AlphabetRole.Name ? FieldRole.Name : FieldRole.Designation).ToArray();
                var standard = DocumentReader.ReadSeparateFields(crops, roles, ocr.Pair, CancellationToken.None);
                var drafting = DocumentReader.ReadSeparateFields(crops, roles, gost, CancellationToken.None);
                var outcomes = new List<object>();
                for (var i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var expected = Expected(line)!;
                    var readings = new (string Pair, IOcrEngine Engine, OcrReading Reading)[]
                    {
                        ("standard", ocr.Pair.First, standard.First[i]), ("standard", ocr.Pair.Second, standard.Second[i]),
                        ("gost", gost.First, drafting.First[i]), ("gost", gost.Second, drafting.Second[i]),
                    };
                    foreach (var (pair, engine, reading) in readings)
                        stats.Add($"{pair}:{engine.Name}", font.Key, condition.Key, line, expected, Comparable(line, reading.Text));
                    var (value, problem, by) = Accept(line, standard.First[i], standard.Second[i], ocr.Pair)
                        is { Value: not null } first ? (first.Value, first.Problem, "standard")
                        : Accept(line, drafting.First[i], drafting.Second[i], gost) is var second
                            ? (second.Value, second.Problem, second.Value is null ? null : "gost") : default;
                    // Третье мнение, как в программе: значение обычной пары со спорными буквами
                    // читает модель чертежного шрифта; та же группа букв на том же месте — отказ.
                    if (ocr.Pair.UseThirdOpinion && by == "standard" && value!.Any(DraftingConfusions.IsConfusable))
                    {
                        var opinion = Parse(line, drafting.First[i], gost.First);
                        if (opinion.IsOk && !string.IsNullOrEmpty(opinion.Value) &&
                            DraftingConfusions.Conflict(value, opinion.Value) is { } c)
                        {
                            stats.ThirdOpinion(font.Key, condition.Key, value == expected);
                            (value, problem, by) = (null, $"третье мнение: «{c.Other}» вместо «{c.Accepted}»", null);
                        }
                    }
                    var outcome = value is null ? Outcome.Refused : value == expected ? Outcome.Exact : Outcome.Wrong;
                    stats.Outcome(font.Key, condition.Key, line.Role, outcome);
                    if (outcome == Outcome.Wrong)
                        wrong.Add($"{font.Key}/{condition.Key}: «{line.Text}» принято как «{value}» ({by})");
                    outcomes.Add(new
                    {
                        line.Text, Role = line.Role.ToString(), Expected = expected, Accepted = value, AcceptedBy = by,
                        Outcome = outcome.ToString(), Problem = problem,
                        Readings = readings.Select(r => new { r.Pair, r.Engine.Name, r.Reading.Text, Confidence = r.Reading.MinConfidence }),
                    });
                }
                runs.Add(new { Font = font.Key, Condition = condition.Key, Lines = outcomes });
            }
            finally { foreach (var crop in crops) crop.Dispose(); }
        }

        foreach (var l in stats.Summary()) output.WriteLine(l);
        foreach (var w in wrong) output.WriteLine("НЕВЕРНО: " + w);
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Directory.CreateDirectory(outputRoot);
            File.WriteAllText(Path.Combine(outputRoot, "alphabet-report.json"), JsonSerializer.Serialize(new
            {
                Engines = stats.EngineTable(),
                Letters = stats.LetterTable(),
                Confusions = stats.ConfusionTable(),
                SharedConfusions = stats.SharedConfusions(),
                Outcomes = stats.OutcomeTable(),
                ThirdOpinion = stats.ThirdOpinionTable(),
                Wrong = wrong,
                Runs = runs,
            }, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            }));
        }
        // Базовая линия: известные неверные принятия на чертежных шрифтах (их еще предстоит
        // устранить) и число точных строк по шрифтам. Новое неверное принятие или меньше точных
        // строк — провал; исправленное — подсказка обновить файл.
        var baseline = AlphabetBaseline.Load();
        var exactLines = stats.Outcomes();
        var failures = new List<string>();
        failures.AddRange(wrong.Where(w => !baseline.KnownWrong.Contains(w)).Select(w => "новое неверное принятие: " + w));
        foreach (var (key, exact) in baseline.Exact)
            if (exactLines.GetValueOrDefault(key) < exact)
                failures.Add($"меньше точных строк в {key}: {exactLines.GetValueOrDefault(key)} вместо {exact}");
        foreach (var fixedLine in baseline.KnownWrong.Where(k => !wrong.Contains(k)))
            output.WriteLine("Исправлено (уберите из alphabet-baseline.json): " + fixedLine);
        foreach (var (key, exact) in exactLines.Where(p => p.Value > baseline.Exact.GetValueOrDefault(p.Key)))
            output.WriteLine($"Больше точных строк в {key}: {exact} (обновите alphabet-baseline.json)");
        if (!string.IsNullOrWhiteSpace(outputRoot))
            File.WriteAllText(Path.Combine(outputRoot, "alphabet-baseline.suggested.json"),
                AlphabetBaseline.Serialize(new AlphabetBaseline(wrong.Order(StringComparer.Ordinal).ToList(),
                    exactLines.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary())));
        foreach (var f in failures) output.WriteLine("Расхождение с базовой линией: " + f);
        // Ответы движков зависят от версии Tesseract и шрифтов системы: базовая линия записана на
        // Linux (Ubuntu 24.04, Tesseract 5.3.4) и обязательна там (DTR_ALPHABET_BASELINE=enforce).
        if (Environment.GetEnvironmentVariable("DTR_ALPHABET_BASELINE") == "enforce")
            Assert.True(failures.Count == 0, "Прогон алфавита:\n" + string.Join("\n", failures));
    }

    /// <summary>Эталон в форме программы: правила поля применяются к самому тексту строки.</summary>
    internal static string? Expected(AlphabetLine line) => line.Role == AlphabetRole.Name
        ? FieldParser.CheckName([line.Text]).Value
        : FieldParser.ParseDesignationText(line.Text).Value;

    /// <summary>Ответ движка в той же нормализации, что и эталон, но без проверки правил:
    /// для подсчета ошибок по буквам нужен сам прочитанный текст.</summary>
    private static string Comparable(AlphabetLine line, string raw)
    {
        var text = TextRules.JoinLines(raw.Split('\n'));
        return line.Role == AlphabetRole.Name ? text : Designations.Prepare(text).Replace(" ", "").ToUpperInvariant();
    }

    /// <summary>Приемка одной графы так же, как в программе: оба движка прочитали, правила
    /// поля пройдены, значения совпали (с допуском только для латинской B).</summary>
    private static Parsed Parse(AlphabetLine line, OcrReading r, IOcrEngine engine) => line.Role == AlphabetRole.Name
        ? FieldParser.ParseName(r, OcrPair.MinConfidence(engine))
        : FieldParser.ParseDesignation(r, OcrPair.MinConfidence(engine));

    private static (string? Value, string? Problem) Accept(AlphabetLine line, OcrReading a, OcrReading b, OcrPair pair)
    {
        var pa = Parse(line, a, pair.First);
        var pb = Parse(line, b, pair.Second);
        if (!pa.IsOk || !pb.IsOk) return (null, pa.Problem ?? pb.Problem);
        var (merged, problem) = TextRules.MergeTwins(pa.Value!, pb.Value!);
        if (merged is null) return (null, problem ?? $"прочитано по-разному: «{pa.Value}» и «{pb.Value}»");
        if (line.Role == AlphabetRole.Designation && merged.Any(c => char.IsLetter(c) && !TextRules.IsCyrillic(c)))
            return (null, "обозначение содержит неподтвержденные буквы вне кириллицы");
        return (merged, null);
    }

    private static void SaveSheet(string path, Mat[] crops)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var width = crops.Max(c => c.Width);
        using var sheet = new Mat(crops.Sum(c => c.Height), width, MatType.CV_8UC1, Scalar.All(255));
        var y = 0;
        foreach (var crop in crops)
        {
            using var gray = crop.Channels() == 1 ? crop.Clone() : crop.CvtColor(ColorConversionCodes.BGR2GRAY);
            gray.CopyTo(sheet[new Rect(0, y, gray.Width, gray.Height)]);
            y += gray.Height;
        }
        Cv2.ImWrite(path, sheet);
    }

    /// <summary>Сводка прогона: точность строк и знаков по движкам, путаницы по буквам.</summary>
    private sealed class SweepStats
    {
        private readonly Dictionary<string, (int Lines, int Exact, int Chars, int Errors)> _engines = [];
        private readonly Dictionary<(string Engine, char Truth), (int Total, int Correct)> _letters = [];
        private readonly Dictionary<(string Engine, char Truth, string Read), int> _confusions = [];
        private readonly Dictionary<(string Font, string Condition, string Line, int Position), HashSet<string>> _shared = [];
        private readonly Dictionary<string, int[]> _outcomes = [];
        private readonly Dictionary<string, (int StoppedWrong, int StoppedCorrect)> _third = [];

        /// <summary>Третье мнение остановило значение: было ли оно неверным (польза) или верным (лишний отказ).</summary>
        public void ThirdOpinion(string font, string condition, bool wasCorrect)
        {
            var key = $"{font}|{condition}";
            var t = _third.GetValueOrDefault(key);
            _third[key] = wasCorrect ? (t.StoppedWrong, t.StoppedCorrect + 1) : (t.StoppedWrong + 1, t.StoppedCorrect);
        }

        public object ThirdOpinionTable() => _third.OrderBy(p => p.Key)
            .Select(p => new { Key = p.Key, p.Value.StoppedWrong, p.Value.StoppedCorrect }).ToArray();

        public void Add(string engine, string font, string condition, AlphabetLine line, string expected, string read)
        {
            var key = $"{engine}|{font}|{condition}";
            var e = _engines.GetValueOrDefault(key);
            var ops = Align(expected, read);
            var errors = ops.Count(o => o.Truth == '\0' || o.Read != o.Truth.ToString());
            _engines[key] = (e.Lines + 1, e.Exact + (expected == read ? 1 : 0), e.Chars + expected.Length, e.Errors + errors);
            var position = 0;
            foreach (var (truth, readText) in ops)
            {
                if (truth == '\0') continue;
                position++;
                if (char.IsWhiteSpace(truth)) continue;
                var l = _letters.GetValueOrDefault((engine, truth));
                var ok = readText == truth.ToString();
                _letters[(engine, truth)] = (l.Total + 1, l.Correct + (ok ? 1 : 0));
                if (ok) continue;
                _confusions[(engine, truth, readText)] = _confusions.GetValueOrDefault((engine, truth, readText)) + 1;
                var place = (font, condition, line.Text, position);
                if (!_shared.TryGetValue(place, out var set)) _shared[place] = set = [];
                set.Add($"{engine}={truth}>{readText}");
            }
        }

        public void Outcome(string font, string condition, AlphabetRole role, AlphabetSweepTests.Outcome outcome)
        {
            var key = $"{font}|{condition}|{role}";
            if (!_outcomes.TryGetValue(key, out var c)) _outcomes[key] = c = new int[3];
            c[(int)outcome]++;
        }

        /// <summary>Посимвольное выравнивание по расстоянию Левенштейна. Truth '\0' — лишний
        /// знак в ответе; Read "" — пропущенный знак.</summary>
        private static List<(char Truth, string Read)> Align(string truth, string read)
        {
            var d = new int[truth.Length + 1, read.Length + 1];
            for (var i = 0; i <= truth.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= read.Length; j++) d[0, j] = j;
            for (var i = 1; i <= truth.Length; i++)
            for (var j = 1; j <= read.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (truth[i - 1] == read[j - 1] ? 0 : 1));
            var ops = new List<(char, string)>();
            int x = truth.Length, y = read.Length;
            while (x > 0 || y > 0)
            {
                if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + (truth[x - 1] == read[y - 1] ? 0 : 1))
                { ops.Add((truth[x - 1], read[y - 1].ToString())); x--; y--; }
                else if (x > 0 && d[x, y] == d[x - 1, y] + 1) { ops.Add((truth[x - 1], "")); x--; }
                else { ops.Add(('\0', read[y - 1].ToString())); y--; }
            }
            ops.Reverse();
            return ops;
        }

        public IEnumerable<string> Summary()
        {
            foreach (var g in _engines.OrderBy(p => p.Key))
                yield return $"{g.Key,-62} строк точно {g.Value.Exact,3}/{g.Value.Lines,-3} ошибок в знаках {100.0 * g.Value.Errors / Math.Max(1, g.Value.Chars),5:0.0} %";
            foreach (var o in _outcomes.OrderBy(p => p.Key))
                yield return $"итог {o.Key,-46} точно {o.Value[0],3}  отказ {o.Value[1],3}  неверно {o.Value[2],3}";
            foreach (var t in _third.OrderBy(p => p.Key))
                yield return $"третье мнение {t.Key,-36} остановило неверных {t.Value.StoppedWrong,3}, верных {t.Value.StoppedCorrect,3}";
            foreach (var c in _confusions.OrderByDescending(p => p.Value).Take(40))
                yield return $"путаница {c.Key.Engine,-26} {c.Key.Truth} → «{c.Key.Read}» {c.Value}";
            foreach (var s in SharedConfusions().Take(30))
                yield return $"общая путаница пары: {s}";
        }

        public object EngineTable() => _engines.OrderBy(p => p.Key).Select(p => new
        {
            Key = p.Key, p.Value.Lines, p.Value.Exact, CharErrorRate = Math.Round((double)p.Value.Errors / Math.Max(1, p.Value.Chars), 4),
        }).ToArray();

        public object LetterTable() => _letters.GroupBy(p => p.Key.Engine).OrderBy(g => g.Key).Select(g => new
        {
            Engine = g.Key,
            Letters = g.OrderBy(p => p.Key.Truth).Select(p => new
            {
                Letter = p.Key.Truth.ToString(), p.Value.Total, p.Value.Correct,
                Accuracy = Math.Round((double)p.Value.Correct / p.Value.Total, 3),
            }).ToArray(),
        }).ToArray();

        public object ConfusionTable() => _confusions.OrderByDescending(p => p.Value)
            .Select(p => new { p.Key.Engine, Truth = p.Key.Truth.ToString(), p.Key.Read, Count = p.Value }).ToArray();

        /// <summary>Одинаковая ошибка обоих движков одной пары в одном и том же месте строки:
        /// такую ошибку сверка движков не остановит.</summary>
        public IReadOnlyList<string> SharedConfusions()
        {
            var shared = new Dictionary<string, int>();
            foreach (var set in _shared.Values)
                foreach (var pair in new[] { "standard", "gost" })
                {
                    var errors = set.Where(s => s.StartsWith(pair + ":", StringComparison.Ordinal))
                        .Select(s => s[(s.IndexOf('=') + 1)..]).ToList();
                    foreach (var e in errors.GroupBy(x => x).Where(g => g.Count() >= 2))
                        shared[$"{pair}: {e.Key}"] = shared.GetValueOrDefault($"{pair}: {e.Key}") + 1;
                }
            return shared.OrderByDescending(p => p.Value).Select(p => $"{p.Key} ×{p.Value}").ToArray();
        }

        /// <summary>Точные строки по ключу «шрифт|условия|графа».</summary>
        public Dictionary<string, int> Outcomes() => _outcomes.ToDictionary(p => p.Key, p => p.Value[0]);

        public object OutcomeTable() => _outcomes.OrderBy(p => p.Key).Select(p => new
        {
            Key = p.Key, Exact = p.Value[0], Refused = p.Value[1], Wrong = p.Value[2],
        }).ToArray();
    }
}

/// <summary>Базовая линия прогона алфавита: tests/Core.Tests/Alphabet/alphabet-baseline.json.</summary>
internal sealed record AlphabetBaseline(List<string> KnownWrong, Dictionary<string, int> Exact)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AlphabetBaseline Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tests", "Core.Tests", "Alphabet", "alphabet-baseline.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AlphabetBaseline>(File.ReadAllText(path), Json)
                    ?? throw new InvalidDataException(path);
        }
        throw new FileNotFoundException("alphabet-baseline.json не найден");
    }

    public static string Serialize(AlphabetBaseline baseline) => JsonSerializer.Serialize(baseline, Json) + "\n";
}
