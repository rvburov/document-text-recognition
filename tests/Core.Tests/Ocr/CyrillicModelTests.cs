using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

[Collection("OCR")]
public sealed class CyrillicModelTests
{
    [Fact]
    public void Offline_distribution_contains_the_matching_source_vocabulary_and_converted_model()
    {
        OcrComponents.ValidateCyrillicModelFiles(OcrComponents.PaddleDir,
            Path.Combine(OcrComponents.ProgramDir, "ocr", "onnx"));
        Assert.True(File.Exists(Path.Combine(OcrComponents.ProgramDir, "ocr", "onnx", "cyrillic-model-manifest.json")));
        Assert.True(File.Exists(Path.Combine(OcrComponents.ProgramDir, "licenses", "PaddleOCR-Apache-2.0.txt")));
    }

    [Fact]
    public void Cyrillic_engine_refuses_an_eslav_dictionary_instead_of_silently_decoding_wrong_symbols()
    {
        using var copies = new ModelCopies();
        File.Copy(Path.Combine(OcrComponents.PaddleDir, OcrComponents.PaddleRecognizer, "inference.yml"),
            Path.Combine(copies.Recognizer, "inference.yml"), overwrite: true);

        var error = Assert.Throws<InvalidDataException>(() => new PaddleEngine(copies.Models,
            useOnnx: false, recognizerName: OcrComponents.PaddleCyrillicRecognizer));

        Assert.Contains("inference.yml", error.Message);
    }

    [Fact]
    public void Cyrillic_engine_refuses_an_eslav_graph_even_when_the_filename_matches()
    {
        using var copies = new ModelCopies();
        File.Copy(Path.Combine(OcrComponents.ProgramDir, "ocr", "onnx", "rec.onnx"),
            Path.Combine(copies.Onnx, OcrComponents.PaddleCyrillicOnnxFile));

        var error = Assert.Throws<InvalidDataException>(() => new PaddleEngine(copies.Models,
            onnxModelsDir: copies.Onnx, recognizerName: OcrComponents.PaddleCyrillicRecognizer));

        Assert.Contains(OcrComponents.PaddleCyrillicOnnxFile, error.Message);
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explicit_cyrillic_engine_reads_recorded_GOST_designation_and_mixed_name(bool useOnnx)
    {
        using var engine = new PaddleEngine(OcrComponents.PaddleDir, useOnnx: useOnnx,
            recognizerName: OcrComponents.PaddleCyrillicRecognizer);
        Assert.Equal("PaddleOCR", engine.Name);
        Assert.Equal(OcrComponents.PaddleCyrillicRecognizer, engine.RecognizerName);
        using var designation = Cv2.ImRead(Sample("designation.png"), ImreadModes.Grayscale);
        using var name = Cv2.ImRead(Sample("name-mixed.png"), ImreadModes.Grayscale);
        var readings = engine.ReadMany([designation, name], profiles:
            [OcrTextProfile.CyrillicDesignation, OcrTextProfile.General]);

        Assert.Equal("УИЕС.468732.271", FieldParser.ParseDesignation(readings[0], .5).Value);
        Assert.Equal("Плата Epcos USB", FieldParser.ParseName(readings[1], .5).Value);
    }

    private static string Sample(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "docs", "test-samples", "gost-type-a", "direct-fields", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Tracked GOST crop is missing", name);
    }

    private sealed class ModelCopies : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "dtr-cyr-model-" + Guid.NewGuid().ToString("N"));
        internal string Models => Path.Combine(_root, "paddle");
        internal string Recognizer => Path.Combine(Models, OcrComponents.PaddleCyrillicRecognizer);
        internal string Onnx => Path.Combine(_root, "onnx");
        internal ModelCopies()
        {
            Directory.CreateDirectory(Recognizer);
            Directory.CreateDirectory(Onnx);
            foreach (var name in new[] { "inference.json", "inference.pdiparams", "inference.yml" })
                File.Copy(Path.Combine(OcrComponents.PaddleDir, OcrComponents.PaddleCyrillicRecognizer, name), Path.Combine(Recognizer, name));
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
