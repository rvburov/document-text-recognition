using System.Collections;
using System.Reflection;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Core.Recognition;
using DocumentTextRecognition.Core.Templates;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

/// <summary>Реальные PDF/шаблоны, управляемые OCR: кэшируется выбор модели, а не прочитанные поля.</summary>
[Collection("OCR")]
public sealed class GostRoutingTests
{
    [OcrFact]
    public void Model_choice_is_bounded_by_template_reference_and_every_new_pdf_is_read_again()
    {
        using var work = new RoutingWorkbench();
        var standardFirst = new Engine("standard first", _ => Empty());
        var standardSecond = new Engine("standard second", _ => Empty());
        var gostFirst = new Engine("gost first", call => Reading(Values(call)));
        var gostSecond = new Engine("gost second", call => Reading(Values(call)));
        var creations = 0;
        using var pair = Pair(standardFirst, standardSecond, () =>
        {
            creations++;
            return Pair(gostFirst, gostSecond);
        });

        for (var number = 1; number <= 6; number++)
        {
            var result = work.Read(work.Document(number), pair);
            Assert.Equal(Values(number), result.Values);
            Assert.Equal(1, result.Trace!.GostAttempts);
            Assert.Equal(1, result.Trace.GostAccepted);
            Assert.Equal(number, gostFirst.ReadCalls);
            Assert.Equal(number, gostSecond.ReadCalls);
        }
        Assert.Equal(1, standardFirst.ReadCalls);
        Assert.Equal(1, standardSecond.ReadCalls);
        Assert.Equal(1, creations);
        Assert.Equal(1, PreferenceCount(pair)); // Six filenames do not produce six cache entries.

        using var reloaded = TemplateLibrary.Load(work.TemplateFile).Template!;
        Assert.NotNull(reloaded);
        Assert.Equal(work.Template.FilePath, reloaded.FilePath);
        Assert.False(pair.PrefersGost(reloaded)); // Equal filename is not equal template lifetime.
        var next = work.Read(work.Document(7), pair, template: reloaded);
        Assert.Equal(Values(7), next.Values);
        Assert.Equal(2, standardFirst.ReadCalls);
        Assert.Equal(7, gostFirst.ReadCalls);
        Assert.Equal(2, PreferenceCount(pair));
        Assert.Equal(1, creations);
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refusal_by_both_pairs_keeps_both_reasons_even_when_gost_was_preferred(bool preferred)
    {
        using var work = new RoutingWorkbench();
        var first = new Engine("standard first", _ => Empty());
        var second = new Engine("standard second", _ => Empty());
        var gostFirst = new Engine("gost first", call => Reading(Values(1), preferred && call == 1 ? 0.95 : 0.2));
        var gostSecond = new Engine("gost second", call => Reading(Values(1), preferred && call == 1 ? 0.95 : 0.2));
        using var pair = Pair(first, second, () => Pair(gostFirst, gostSecond));
        var file = work.Document(1);
        if (preferred)
        {
            Assert.Equal(Values(1), work.Read(file, pair).Values);
            Assert.True(pair.PrefersGost(work.Template));
        }

        var refused = work.Read(file, pair);

        Assert.Null(refused.Values);
        Assert.Contains("обычная модель", refused.Problem);
        Assert.Contains("чертёжный шрифт", refused.Problem);
        Assert.Contains("пустая", refused.Problem);
        Assert.Contains("уверенность", refused.Problem);
        Assert.Equal(1, refused.Trace!.GostAttempts);
        Assert.Equal(0, refused.Trace.GostAccepted);
    }

    [OcrFact]
    public void Preferred_gost_refusal_can_return_to_standard_and_remember_only_that_choice()
    {
        using var work = new RoutingWorkbench();
        var first = new Engine("standard first", call => call == 1 ? Empty() : Reading(Values(call)));
        var second = new Engine("standard second", call => call == 1 ? Empty() : Reading(Values(call)));
        var gostFirst = new Engine("gost first", call => call == 1 ? Reading(Values(1)) : Empty());
        var gostSecond = new Engine("gost second", call => call == 1 ? Reading(Values(1)) : Empty());
        using var pair = Pair(first, second, () => Pair(gostFirst, gostSecond));
        Assert.Equal(Values(1), work.Read(work.Document(1), pair).Values);
        Assert.True(pair.PrefersGost(work.Template));

        var standardWon = work.Read(work.Document(2), pair);
        Assert.Equal(Values(2), standardWon.Values);
        Assert.Equal(1, standardWon.Trace!.GostAttempts);
        Assert.Equal(0, standardWon.Trace.GostAccepted);
        Assert.False(pair.PrefersGost(work.Template));

        var next = work.Read(work.Document(3), pair);
        Assert.Equal(Values(3), next.Values);
        Assert.Equal(0, next.Trace!.GostAttempts);
        Assert.Equal(2, gostFirst.ReadCalls);
        Assert.Equal(3, first.ReadCalls);
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Engine_exceptions_are_propagated_without_trying_another_model(bool preferred)
    {
        using var work = new RoutingWorkbench();
        var error = new InvalidOperationException("native inference failed");
        var first = new Engine("standard first", _ => preferred ? Empty() : throw error);
        var second = new Engine("standard second", _ => Empty());
        var gostFirst = new Engine("gost first", call => call == 1 ? Reading(Values(1)) : throw error);
        var gostSecond = new Engine("gost second", _ => Reading(Values(1)));
        var creations = 0;
        using var pair = Pair(first, second, () =>
        {
            creations++;
            return Pair(gostFirst, gostSecond);
        });
        var file = work.Document(1);
        if (preferred) Assert.Equal(Values(1), work.Read(file, pair).Values);
        var standardReads = first.ReadCalls;

        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => work.Read(file, pair)));
        Assert.Equal(preferred ? standardReads : standardReads + 1, first.ReadCalls);
        Assert.Equal(preferred ? 1 : 0, creations);
        Assert.Equal(preferred ? 1 : 0, gostSecond.ReadCalls);
    }

    [OcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancellation_after_the_last_engine_does_not_initialize_or_try_another_pair(bool preferred)
    {
        using var work = new RoutingWorkbench();
        using var cancellation = new CancellationTokenSource();
        var first = new Engine("standard first", _ => Empty());
        var second = new Engine("standard second", _ =>
        {
            if (!preferred) cancellation.Cancel();
            return Empty();
        });
        var gostFirst = new Engine("gost first", call => call == 1 ? Reading(Values(1)) : Empty());
        var gostSecond = new Engine("gost second", call =>
        {
            if (call > 1) cancellation.Cancel();
            return call == 1 ? Reading(Values(1)) : Empty();
        });
        var creations = 0;
        using var pair = Pair(first, second, () =>
        {
            creations++;
            return Pair(gostFirst, gostSecond);
        });
        var file = work.Document(1);
        if (preferred) Assert.Equal(Values(1), work.Read(file, pair).Values);
        var standardReads = first.ReadCalls;

        Assert.ThrowsAny<OperationCanceledException>(() => work.Read(file, pair, cancellation.Token));
        Assert.Equal(preferred ? 1 : 0, creations);
        Assert.Equal(preferred ? standardReads : standardReads + 1, first.ReadCalls);
    }

    [OcrFact]
    public void Disabled_fallback_never_constructs_the_gost_pair()
    {
        using var work = new RoutingWorkbench();
        using var pair = Pair(new Engine("first", _ => Empty()), new Engine("second", _ => Empty()),
            () => throw new InvalidOperationException("disabled factory must not run"));
        pair.UseGostFallback = false;

        var result = work.Read(work.Document(1), pair);

        Assert.Null(result.Values);
        Assert.Equal(0, result.Trace!.GostAttempts);
        Assert.False(pair.PrefersGost(work.Template));
    }

    [OcrFact]
    public void Model_initialization_exception_is_not_retried_or_reported_as_a_data_refusal()
    {
        using var work = new RoutingWorkbench();
        var first = new Engine("first", _ => Empty());
        var second = new Engine("second", _ => Empty());
        var error = new DllNotFoundException("native model initialization failed");
        var creations = 0;
        using var pair = Pair(first, second, () => { creations++; throw error; });

        Assert.Same(error, Assert.Throws<DllNotFoundException>(() => work.Read(work.Document(1), pair)));
        Assert.Equal(1, creations);
        Assert.Equal(1, first.ReadCalls);
        Assert.Equal(1, second.ReadCalls);
    }

    [Fact]
    public void Initialized_fallback_and_base_engines_are_disposed_exactly_once()
    {
        var first = new Engine("first", _ => Empty());
        var second = new Engine("second", _ => Empty());
        var gostFirst = new Engine("gost first", _ => Empty());
        var gostSecond = new Engine("gost second", _ => Empty());
        var creations = 0;
        var pair = Pair(first, second, () => { creations++; return Pair(gostFirst, gostSecond); });
        Assert.Same(pair.GostPair(), pair.GostPair());

        pair.Dispose();
        pair.Dispose();

        Assert.Equal(1, creations);
        foreach (var engine in new[] { first, second, gostFirst, gostSecond }) Assert.Equal(1, engine.DisposeCalls);
    }

    [Fact]
    public void Disposal_does_not_initialize_an_unused_fallback_or_allow_initialization_later()
    {
        var first = new Engine("first", _ => Empty());
        var second = new Engine("second", _ => Empty());
        var creations = 0;
        var pair = Pair(first, second, () =>
        {
            creations++;
            return Pair(new Engine("gost first", _ => Empty()), new Engine("gost second", _ => Empty()));
        });

        pair.Dispose();
        pair.Dispose();

        Assert.Throws<ObjectDisposedException>(() => pair.GostPair());
        Assert.Equal(0, creations);
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(1, second.DisposeCalls);
    }

    [Fact]
    public void Disposal_failure_still_releases_every_initialized_engine()
    {
        var first = new Engine("first", _ => Empty()) { OnDispose = () => throw new InvalidOperationException("dispose failed") };
        var second = new Engine("second", _ => Empty());
        var gostFirst = new Engine("gost first", _ => Empty());
        var gostSecond = new Engine("gost second", _ => Empty());
        var pair = Pair(first, second, () => Pair(gostFirst, gostSecond));
        Assert.NotNull(pair.GostPair());

        var error = Assert.ThrowsAny<Exception>(() => pair.Dispose());
        Assert.Contains("dispose failed", error.ToString());
        pair.Dispose();

        foreach (var engine in new[] { first, second, gostFirst, gostSecond }) Assert.Equal(1, engine.DisposeCalls);
    }

    private static OcrPair Pair(Engine first, Engine second, Func<OcrPair>? factory = null) =>
        new(first, second, factory) { UsePdfText = false, PreferKnownLines = false };

    private static int PreferenceCount(OcrPair pair) => ((IDictionary)typeof(OcrPair)
        .GetField("_gostPreferred", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pair)!).Count;

    private static FieldValues Values(int number) =>
        new($"ЮИЛГ.461411.{number:000}", "Корпус " + number, DocTypes.WhenMissing);

    private static OcrReading Empty() => new("controlled", []);

    private static OcrReading Reading(FieldValues values, double confidence = 0.95) =>
        new("controlled", [new OcrLine(values.Designation, confidence, 0, 0, 300, 40),
            new OcrLine(values.Name, confidence, 0, 45, 300, 85)]);

    private sealed class Engine(string name, Func<int, OcrReading> read) : IOcrEngine
    {
        public string Name => name;
        public int ReadCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Action? OnDispose { get; init; }

        public OcrReading Read(Mat gray)
        {
            Assert.False(gray.Empty());
            return read(++ReadCalls) with { Engine = Name };
        }

        public void Dispose()
        {
            DisposeCalls++;
            OnDispose?.Invoke();
        }
    }

    private sealed class RoutingWorkbench : IDisposable
    {
        private readonly Workbench _work = new();
        public string TemplateFile { get; }
        public DocTemplate Template { get; }

        public RoutingWorkbench()
        {
            TemplateFile = _work.AddTemplate("controlled routing", FormKind.DrawingA3, Marking.Combined);
            Template = TemplateLibrary.Load(TemplateFile).Template!;
            Assert.NotNull(Template);
        }

        public string Document(int number) => _work.AddDocument("opaque-" + number + ".pdf", FormKind.DrawingA3,
            Values(number).Designation, Values(number).Name, new ScanOptions { Dpi = 200, JpegQuality = 95 });

        public DocumentResult Read(string path, OcrPair pair, CancellationToken cancellation = default,
            DocTemplate? template = null)
        {
            Cv2.SetTheRNG(1729UL);
            return DocumentReader.Read(path, [template ?? Template], pair, cancellation);
        }

        public void Dispose()
        {
            Template.Dispose();
            _work.Dispose();
        }
    }
}
