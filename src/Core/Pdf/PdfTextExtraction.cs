namespace DocumentTextRecognition.Core.Pdf;

internal sealed partial class PdfDocument
{
    private PdfTextPage? _textSnapshot;
    private const int MaxTextCharacters = 100_000;
    private const double TextCoordinateScale = 1000;

    /// <summary>Unicode и геометрия PDFium, без доверия к истинности текстового слоя.
    /// Снимок ограничен текущей страницей; весь доступ к PDFium остается под Gate.</summary>
    public PdfTextPage ReadTextPage(int index, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (Gate)
        {
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_doc == IntPtr.Zero, this);
            if ((uint)index >= (uint)PageCount) throw new ArgumentOutOfRangeException(nameof(index));
            var page = GetPage(index);
            if (_textSnapshot is { } cached) return cached;
            var size = GetPageSize(index);
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) ||
                size.Width * TextCoordinateScale > int.MaxValue || size.Height * TextCoordinateScale > int.MaxValue)
                return new(index, size, [], "некорректный размер страницы для текстовой геометрии");
            var width = Math.Max(1, (int)Math.Round(size.Width * TextCoordinateScale));
            var height = Math.Max(1, (int)Math.Round(size.Height * TextCoordinateScale));
            var text = PdfiumApi.FPDFText_LoadPage(page);
            if (text == IntPtr.Zero) return new(index, size, [], "не удалось открыть текстовый слой PDF");
            try
            {
                var count = PdfiumApi.FPDFText_CountChars(text);
                if (count < 0 || count > MaxTextCharacters)
                    return new(index, size, [], "недопустимое число символов текстового слоя PDF");
                var characters = new PdfTextCharacter[count];
                for (var i = 0; i < count; i++)
                {
                    if ((i & 127) == 0) ct.ThrowIfCancellationRequested();
                    var unicode = PdfiumApi.FPDFText_GetUnicode(text, i);
                    var generated = PdfiumApi.FPDFText_IsGenerated(text, i);
                    var mapError = PdfiumApi.FPDFText_HasUnicodeMapError(text, i);
                    var bounds = default(PdfTextRect);
                    if (PdfiumApi.FPDFText_GetCharBox(text, i, out var left, out var right, out var bottom, out var top) != 0 &&
                        new[] { left, right, bottom, top }.All(double.IsFinite))
                    {
                        // PageToDevice uses exactly the same PDFium viewport transform as
                        // rendering: /Rotate, nonzero MediaBox/CropBox and Y inversion included.
                        var points = new PdfTextPoint[4];
                        var native = new[] { new PdfTextPoint(left, top), new(right, top), new(right, bottom), new(left, bottom) };
                        var okay = true;
                        for (var p = 0; p < native.Length; p++)
                        {
                            if (PdfiumApi.FPDF_PageToDevice(page, 0, 0, width, height, 0, native[p].X, native[p].Y,
                                    out var x, out var y) == 0) { okay = false; break; }
                            points[p] = new(x * size.Width / width, y * size.Height / height);
                        }
                        if (okay) bounds = PdfTextRect.Enclose(points);
                    }
                    var fillOkay = PdfiumApi.FPDFText_GetFillColor(text, i, out var fr, out var fg, out var fb, out var fa) != 0;
                    var strokeOkay = PdfiumApi.FPDFText_GetStrokeColor(text, i, out var sr, out var sg, out var sb, out var sa) != 0;
                    characters[i] = new(i, unicode, bounds, generated == 1, mapError != 0 || generated < 0,
                        PdfiumApi.FPDFText_GetTextRenderMode(text, i), fillOkay ? fa : null, strokeOkay ? sa : null,
                        PdfiumApi.FPDFText_GetFontSize(text, i), (fr << 16) | (fg << 8) | fb, (sr << 16) | (sg << 8) | sb);
                }
                ct.ThrowIfCancellationRequested();
                return _textSnapshot = new(index, size, Array.AsReadOnly(characters));
            }
            finally { PdfiumApi.FPDFText_ClosePage(text); }
        }
    }
}
