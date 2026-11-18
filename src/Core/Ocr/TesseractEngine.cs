using System.Runtime.InteropServices;
using OpenCvSharp;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>
/// Tesseract 5 (LSTM), языки rus+eng. Область читается как один блок текста (PSM 6), по строкам:
/// у каждой строки свой текст, уверенность и рамка — по ним общая красно-оранжевая область
/// делится на обозначение и наименование.
/// </summary>
internal sealed class TesseractEngine : IOcrEngine
{
    private IntPtr _handle;
    private const string DesignationCharacters =
        "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя0123456789.-()/ ";

    public string Name => "Tesseract";
    internal string TessdataDirectory { get; }
    internal string Languages { get; }
    private readonly bool _rawDesignation;
    private readonly bool _restrictDesignationAlphabet;
    internal bool IsStandardConfiguration => Languages == OcrComponents.TesseractLanguages &&
        !_rawDesignation && _restrictDesignationAlphabet;

    public TesseractEngine(string tessdata, string languages = OcrComponents.TesseractLanguages,
        bool rawDesignation = false, bool restrictDesignationAlphabet = true)
    {
        // GetFullPath may expand a Windows 8.3 alias back to a Cyrillic directory.
        // Keep the exported path identical to the one accepted by native Tesseract.
        TessdataDirectory = OcrComponents.AsciiDirectory(Path.GetFullPath(tessdata));
        Languages = languages;
        _rawDesignation = rawDesignation;
        _restrictDesignationAlphabet = restrictDesignationAlphabet;
        OcrComponents.RegisterNativeResolver();
        _handle = TesseractApi.TessBaseAPICreate();
        if (_handle == IntPtr.Zero) throw new InvalidOperationException("не создается экземпляр Tesseract");
        if (TesseractApi.TessBaseAPIInit3(_handle, TessdataDirectory, languages) != 0)
        {
            TesseractApi.TessBaseAPIDelete(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException($"не загружаются языковые данные {languages} из {TessdataDirectory}");
        }
        // Служебные сообщения Tesseract (о разрешении и т. п.) не нужны в консоли программы.
        TesseractApi.TessBaseAPISetVariable(_handle, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
    }

    /// <summary>Версия библиотеки, например «5.3.4».</summary>
    public static string Version()
    {
        OcrComponents.RegisterNativeResolver();
        return Marshal.PtrToStringUTF8(TesseractApi.TessVersion()) ?? "";
    }

    public OcrReading Read(Mat gray) => Read(gray, OcrTextProfile.General);

    internal OcrReading Read(Mat gray, OcrTextProfile profile)
    {
        ObjectDisposedException.ThrowIf(_handle == IntPtr.Zero, this);
        if (profile is not (OcrTextProfile.General or OcrTextProfile.CyrillicDesignation))
            throw new ArgumentOutOfRangeException(nameof(profile));
        if (gray.Type() != MatType.CV_8UC1) throw new ArgumentException("нужна картинка 8 бит в оттенках серого");
        var origin = new Point();
        using var raw = _rawDesignation && profile == OcrTextProfile.CyrillicDesignation
            ? TryPrepareRawDesignation(gray, out origin) : null;
        var selected = raw ?? gray;
        using var copy = selected.IsContinuous() ? null : selected.Clone();
        var image = copy ?? selected;

        var lines = new List<OcrLine>();
        TesseractApi.TessBaseAPISetPageSegMode(_handle, raw is not null ? TesseractApi.PsmRawLine : TesseractApi.PsmSingleBlock);
        TesseractApi.TessBaseAPISetImage(_handle, image.Data, image.Width, image.Height, 1, (int)image.Step());
        TesseractApi.TessBaseAPISetSourceResolution(_handle, 300);
        try
        {
            // Настройка принадлежит этому полю. Следующее наименование снова читает
            // оба алфавита; ограничение не остается в переиспользуемом экземпляре.
            if (TesseractApi.TessBaseAPISetVariable(_handle, "tessedit_char_whitelist",
                profile == OcrTextProfile.CyrillicDesignation && _restrictDesignationAlphabet ? DesignationCharacters : "") == 0)
                throw new InvalidOperationException("Tesseract не принял алфавит поля");
            if (TesseractApi.TessBaseAPIRecognize(_handle, IntPtr.Zero) != 0)
                return new OcrReading(Name, lines);

            var it = TesseractApi.TessBaseAPIGetIterator(_handle);
            if (it == IntPtr.Zero) return new OcrReading(Name, lines);
            try
            {
                var page = TesseractApi.TessResultIteratorGetPageIterator(it);
                const int level = TesseractApi.LevelTextLine;
                do
                {
                    var ptr = TesseractApi.TessResultIteratorGetUTF8Text(it, level);
                    if (ptr == IntPtr.Zero) continue;
                    string text;
                    try { text = (Marshal.PtrToStringUTF8(ptr) ?? "").Trim(); }
                    finally { TesseractApi.TessDeleteText(ptr); }
                    if (text.Length == 0) continue;

                    var conf = TesseractApi.TessResultIteratorConfidence(it, level) / 100.0;
                    TesseractApi.TessPageIteratorBoundingBox(page, level, out var l, out var t, out var r, out var b);
                    lines.Add(new OcrLine(text, Math.Clamp(conf, 0, 1),
                        l + origin.X, t + origin.Y, r + origin.X, b + origin.Y));
                }
                while (TesseractApi.TessPageIteratorNext(page, level) != 0);
            }
            finally
            {
                TesseractApi.TessResultIteratorDelete(it);
            }
        }
        finally
        {
            TesseractApi.TessBaseAPIClear(_handle);
            TesseractApi.TessBaseAPISetVariable(_handle, "tessedit_char_whitelist", "");
        }
        return new OcrReading(Name, lines.OrderBy(l => l.Top).ToList());
    }

    /// <summary>Only a geometrically complete single line may use raw-line normalization.
    /// A second line or faint outlying text keeps the ordinary full-block path.</summary>
    internal static Mat? TryPrepareRawDesignation(Mat gray) => TryPrepareRawDesignation(gray, out _);

    private static Mat? TryPrepareRawDesignation(Mat gray, out Point origin)
    {
        origin = default;
        if (!KnownSingleLineRegion.TryGetBounds(gray, out _)) return null;
        using var ink = gray.Threshold(199, 255, ThresholdTypes.BinaryInv);
        var bounds = Cv2.BoundingRect(ink);
        if (bounds.Width == 0 || bounds.Height == 0) return null;
        using var faint = gray.Threshold(244, 255, ThresholdTypes.BinaryInv);
        using var weakForeground = new Mat();
        Cv2.BitwiseNot(faint, weakForeground);
        if (!KnownSingleLineRegion.TryGetBounds(weakForeground, out _)) return null;
        var all = Cv2.BoundingRect(faint);
        // The margin retains original pixels, including faint accents and punctuation.
        // If any weak foreground lies outside it, use the full-block path instead.
        if (all.Left < bounds.Left - 8 || all.Top < bounds.Top - 8 ||
            all.Right > bounds.Right + 8 || all.Bottom > bounds.Bottom + 8) return null;
        origin = new(bounds.X - 8, bounds.Y - 8);
        var requested = new Rect(origin.X, origin.Y, bounds.Width + 16, bounds.Height + 16);
        var source = requested.Intersect(new Rect(0, 0, gray.Width, gray.Height));
        var result = new Mat(requested.Height, requested.Width, MatType.CV_8UC1, Scalar.All(255));
        try
        {
            using var pixels = new Mat(gray, source);
            using var destination = new Mat(result, new Rect(source.X - requested.X, source.Y - requested.Y,
                source.Width, source.Height));
            pixels.CopyTo(destination);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        TesseractApi.TessBaseAPIDelete(_handle);
        _handle = IntPtr.Zero;
    }
}
