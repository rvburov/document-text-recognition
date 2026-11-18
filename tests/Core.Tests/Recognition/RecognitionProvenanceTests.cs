using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Rename;

namespace DocumentTextRecognition.Tests.Recognition;

public sealed class RecognitionProvenanceTests
{
    [Fact]
    public void Pdf_text_raster_confirmation_does_not_mislabel_the_primary_path_as_mixed()
    {
        var trace = new RecognitionProvenance(PdfTextAttempts: 1, PdfTextAccepted: 1,
            PaddleReads: 3, MatchedPages: 1, MatchingCalls: 1, GostAttempts: 1, GostAccepted: 1);
        Assert.Equal("pdf-text-confirmed", trace.SourcePath);
        Assert.Equal("mixed", (trace with { MatchedPages = 2, TesseractReads = 3 }).SourcePath);
        Assert.Equal("ocr", new RecognitionProvenance(TesseractReads: 3, PaddleReads: 3).SourcePath);
        Assert.Equal("none", new RecognitionProvenance(MatchingCalls: 1).SourcePath);
    }

    [Fact]
    public void Trace_metadata_preserves_existing_document_and_journal_value_identity()
    {
        var trace = new RecognitionProvenance(TesseractReads: 2, PaddleReads: 2,
            MatchedPages: 1, MatchingCalls: 1);
        var document = new DocumentResult(new FieldValues("АБВГ.466229.001", "Плата", "Спецификация"), null, "Шаблон", "1");
        var tracedDocument = document with { Trace = trace };
        Assert.Equal(document, tracedDocument);
        Assert.Equal(document.GetHashCode(), tracedDocument.GetHashCode());
        Assert.Same(trace, (tracedDocument with { Pages = "1" }).Trace);

        var entry = new JournalEntry("scan.pdf", FileStatus.Renamed, "new.pdf", null, "Шаблон", "/tmp/new.pdf");
        var tracedEntry = entry with { Trace = trace };
        Assert.Equal(entry, tracedEntry);
        Assert.Equal(entry.GetHashCode(), tracedEntry.GetHashCode());
        Assert.Same(trace, (tracedEntry with { NewName = "new.pdf" }).Trace);
    }

    [Fact]
    public async Task Actual_worker_protocol_roundtrips_trace_without_crop_buffers()
    {
        var trace = new RecognitionProvenance(PdfTextAttempts: 1, PdfTextAccepted: 1,
            PaddleReads: 2, MatchedPages: 1, MatchingCalls: 1, GostAttempts: 1, GostAccepted: 1);
        var document = new DocumentResult(new FieldValues("АБВГ.466229.001", "Плата", "Спецификация"), null)
            { Trace = trace };
        using var stream = new MemoryStream();
        await OcrWorkerProtocol.WriteAsync(stream, new OcrWorkerMessage("result", 7, Result: document), CancellationToken.None);
        Assert.InRange(stream.Length, 1, 4096);
        stream.Position = 0;
        var restored = await OcrWorkerProtocol.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(7L, restored!.RequestId);
        Assert.Equal(document, restored.Result);
        Assert.Equal(trace, restored.Result!.Trace);
        Assert.Equal("pdf-text-confirmed", restored.Result.Trace!.SourcePath);
    }

    [Fact]
    public void Rename_pipeline_preserves_trace_for_success_and_refusal()
    {
        var root = Path.Combine(Path.GetTempPath(), "dtr-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new[] { Path.Combine(root, "read.pdf"), Path.Combine(root, "refused.pdf") };
            foreach (var path in paths) File.WriteAllText(path, "Metadata flow fixture; OCR is intentionally substituted in this unit test.");
            var trace = new RecognitionProvenance(TesseractReads: 2, PaddleReads: 2, MatchedPages: 1, MatchingCalls: 1);
            var sink = new Rows();
            var summary = OcrRenameRun.ProcessFiles(new RunSettings(root, root, false, Workers: 1), paths,
                () => new TracedRecognizer(trace), sink, CancellationToken.None);
            Assert.Null(summary.Fatal);
            Assert.Equal(2, summary.Processed);
            Assert.Equal(1, summary.Renamed);
            Assert.Equal(1, summary.Skipped);
            Assert.Equal(2, sink.Entries.Count);
            Assert.All(sink.Entries, row => Assert.Same(trace, row.Trace));
            Assert.Contains(sink.Entries, row => row.Status == FileStatus.Renamed);
            Assert.Contains(sink.Entries, row => row.Status == FileStatus.Skipped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TracedRecognizer(RecognitionProvenance trace) : IDocumentRecognizer
    {
        public DocumentResult Read(string path, CancellationToken ct) =>
            (Path.GetFileName(path) == "read.pdf"
                ? new DocumentResult(new FieldValues("АБВГ.466229.001", "Плата", "Спецификация"), null)
                : DocumentResult.Fail("Declared fixture refusal")) with { Trace = trace };
        public void Dispose() { }
    }

    private sealed class Rows : IRunSink
    {
        internal readonly List<JournalEntry> Entries = [];
        public void Message(string text) { }
        public void Found(int count) { }
        public void File(JournalEntry entry) => Entries.Add(entry);
    }
}
