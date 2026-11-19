using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Alphabet;

/// <summary>
/// Инструмент, а не проверка: рисует строки корпуса дообучения модели чертежного шрифта тем же
/// путем, каким программа готовит вырезку для OCR (Skia, наклон как в САПР, «скан», затем
/// FieldCrop.Prepare). Так обучающие картинки совпадают с тем, что видит Tesseract в программе.
/// Запускается только при DTR_TRAINING_LINES (TSV: раздел, вид, текст) и DTR_TRAINING_OUTPUT.
/// </summary>
public sealed class TrainingCorpusExport
{
    [OcrFact]
    public void Export_training_lines_through_the_application_crop_pipeline()
    {
        var input = Environment.GetEnvironmentVariable("DTR_TRAINING_LINES");
        var output = Environment.GetEnvironmentVariable("DTR_TRAINING_OUTPUT");
        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output)) return;
        var fonts = AlphabetFont.All.Where(f => !f.System).ToArray();
        var random = new Random(20261121);
        var index = 0;
        Parallel.ForEach(File.ReadAllLines(input).Select((line, i) => (line, i)), new ParallelOptions { MaxDegreeOfParallelism = 3 }, item =>
        {
            var parts = item.line.Split('\t');
            if (parts.Length != 3) return;
            var (split, kind, text) = (parts[0], parts[1], parts[2]);
            int seed;
            lock (random) seed = random.Next();
            var rng = new Random(seed);
            var font = fonts[item.i % fonts.Length];
            var condition = AlphabetCondition.All[rng.Next(AlphabetCondition.All.Count)];
            var cap = kind is "designation" ? 3.5 + rng.NextDouble() * 3.5 : 2.5 + rng.NextDouble() * 2.5;
            using var bgr = AlphabetSamples.Render(text, font, cap, condition, seed);
            using var prepared = FieldCrop.Prepare(bgr);
            var folder = Path.Combine(output, split);
            Directory.CreateDirectory(folder);
            var stem = $"{split}-{item.i:05d}-{font.Key}-{condition.Key}";
            Cv2.ImWrite(Path.Combine(folder, stem + ".png"), prepared);
            File.WriteAllText(Path.Combine(folder, stem + ".gt.txt"), text + "\n");
            Interlocked.Increment(ref index);
        });
        Assert.True(index > 0);
    }
}
