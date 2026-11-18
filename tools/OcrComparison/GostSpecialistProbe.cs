using System.Diagnostics;
using System.Security.Cryptography;
using DocumentTextRecognition.Core.Imaging;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using OpenCvSharp;

/// <summary>One bounded diagnostic pass through the real product field-reading path.
/// Field evidence is not a whole-document acceptance or performance benchmark.</summary>
internal static class GostSpecialistProbe
{
    internal static object Run(string root, Case[] cases, bool knownLines)
    {
        var tessdata = OcrComponents.FindTessdata() ?? throw new DirectoryNotFoundException("Tesseract models are missing");
        GostTesseractModel.Validate(tessdata);
        var modelFile = Path.Combine(tessdata, GostTesseractModel.FileName);
        var modelHash = Hash(modelFile);
        var first = new TesseractEngine(tessdata, GostTesseractModel.Languages,
            rawDesignation: true, restrictDesignationAlphabet: false);
        OcrPair pair;
        try
        {
            pair = new OcrPair(first, new PaddleEngine(OcrComponents.PaddleDir,
                recognizerName: OcrComponents.PaddleCyrillicRecognizer))
                { UseGostFallback = false, UsePdfText = false, PreferKnownLines = knownLines };
        }
        catch { first.Dispose(); throw; }
        using (pair)
        {
            var results = new List<object>();
            var correct = 0;
            var refused = 0;
            var wrongAgreement = 0;
            foreach (var item in cases)
            {
                var path = Path.Combine(root, item.File);
                var hash = Hash(path);
                if (!hash.Equals(item.CropSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Input checksum mismatch: " + item.File);
                using var crop = Cv2.ImRead(path, ImreadModes.Grayscale);
                if (crop.Empty()) throw new InvalidDataException(item.File);
                var role = Enum.Parse<FieldRole>(item.Role);
                using var rawDesignation = TesseractEngine.TryPrepareRawDesignation(crop);
                var rawEligible = rawDesignation is not null;
                var geometricSingleLine = KnownSingleLineRegion.TryGetBounds(crop, out _);
                var trace = new RecognitionTraceBuilder();
                var phases = new Dictionary<string, double>();
                var started = Stopwatch.GetTimestamp();
                var readings = DocumentReader.ReadSeparateFields([crop], [role], pair, CancellationToken.None,
                    (stage, elapsed) => phases[stage] = phases.GetValueOrDefault(stage) + elapsed.TotalMilliseconds, trace);
                var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var a = readings.First.Single();
                var b = readings.Second.Single();
                var parsedA = Parse(role, a, OcrPair.MinConfidence(pair.First));
                var parsedB = Parse(role, b, OcrPair.MinConfidence(pair.Second));
                var confirmed = EngineAgreement.AreConfirmed(parsedA, parsedB);
                var agreedValue = confirmed ? TextRules.MergeTwins(parsedA.Value!, parsedB.Value!).Value : null;
                var exact = confirmed && agreedValue == item.Expected;
                if (exact) correct++;
                else if (confirmed) wrongAgreement++;
                else refused++;
                results.Add(new { item.Id, item.File, item.Role, item.Source, item.Expected, CropSha256 = hash,
                    RawDesignationEligible = rawEligible,
                    RawDesignationApplied = role == FieldRole.Designation && rawEligible,
                    RawPreparedSize = rawDesignation is null ? null : new { rawDesignation.Width, rawDesignation.Height },
                    GeometricSingleLine = geometricSingleLine,
                    Tesseract = new { Raw = a.Text, Reading = a, Parsed = parsedA, Threshold = OcrPair.MinConfidence(pair.First) },
                    Paddle = new { Raw = b.Text, Reading = b, Parsed = parsedB, Threshold = OcrPair.MinConfidence(pair.Second) },
                    FieldAgreement = confirmed, AgreedValue = agreedValue, AgreementExact = exact,
                    Trace = trace.Snapshot(), PaddleDetectorFallback = phases.ContainsKey("paddle.fallback"),
                    DiagnosticElapsedMilliseconds = elapsed, PhasesMilliseconds = phases,
                    InputUnchanged = Hash(path) == hash });
                Console.WriteLine($"{item.Id}: rawEligible={rawEligible}, Tess={a.Text.Replace('\n', '|')} [{a.MinConfidence:F4}], " +
                    $"Paddle={b.Text.Replace('\n', '|')} [{b.MinConfidence:F4}], exact={exact}, fallback={phases.ContainsKey("paddle.fallback")}");
            }
            return new { SchemaVersion = 1, Engine = "gost-specialist",
                Scope = "Saved field crops through actual DocumentReader.ReadSeparateFields; one diagnostic pass, not a full-document accuracy or throughput measurement",
                DiagnosticTimingNote = "Wall times may overlap development/training work; do not use them as performance benchmark or model-selection evidence",
                Models = new { TesseractFile = modelFile, TesseractSha256 = modelHash,
                    ExpectedTesseractSha256 = GostTesseractModel.Sha256, GostTesseractModel.Languages,
                    RawDesignation = true, RestrictDesignationAlphabet = false,
                    PaddleRecognizer = ((PaddleEngine)pair.Second).RecognizerName, UsesOnnx = ((PaddleEngine)pair.Second).UsesOnnx },
                PreferKnownLines = pair.PreferKnownLines, Fields = cases.Length,
                ExactFieldAgreements = correct, RefusedFieldAgreements = refused, IncorrectFieldAgreements = wrongAgreement,
                Results = results };
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static Parsed Parse(FieldRole role, OcrReading reading, double threshold) => role switch
    {
        FieldRole.Designation => FieldParser.ParseDesignation(reading, threshold),
        FieldRole.Name => FieldParser.ParseName(reading, threshold),
        FieldRole.Type => FieldParser.ParseType(reading, threshold),
        _ => throw new ArgumentException("Only separate fields are supported", nameof(role)),
    };
}
