namespace DocumentTextRecognition.Core.Recognition;

/// <summary>
/// Lightweight evidence from the actual document read, without crop buffers or timings.
/// PDF-text counters count matched-page attempts/acceptances; engine counters count field
/// images submitted, including fallback (a batch of three fields contributes three reads).
/// MatchingCalls counts page-level matcher invocations, not descriptor comparisons.
/// </summary>
internal sealed record RecognitionProvenance(int PdfTextAttempts = 0, int PdfTextAccepted = 0,
    int TesseractReads = 0, int PaddleReads = 0, int EasyReads = 0,
    int MatchedPages = 0, int MatchingCalls = 0, int GostAttempts = 0, int GostAccepted = 0)
{
    // PDF-text pages also require a raster peer; those Paddle reads do not turn a
    // fully text-confirmed document into a mixed primary path. Acceptance is page-based.
    public string SourcePath => PdfTextAccepted > 0
        ? PdfTextAccepted == MatchedPages ? "pdf-text-confirmed" : "mixed"
        : HasOcr ? "ocr" : "none";

    private bool HasOcr => TesseractReads > 0 || PaddleReads > 0 || EasyReads > 0;
}

/// <summary>Owned by one DocumentReader.Read invocation; never shared between workers.</summary>
internal sealed class RecognitionTraceBuilder
{
    internal int PdfTextAttempts, PdfTextAccepted, TesseractReads, PaddleReads, EasyReads;
    internal int MatchedPages, MatchingCalls;
    internal int GostAttempts, GostAccepted;

    internal RecognitionProvenance Snapshot() => new(PdfTextAttempts, PdfTextAccepted,
        TesseractReads, PaddleReads, EasyReads, MatchedPages, MatchingCalls, GostAttempts, GostAccepted);
}
