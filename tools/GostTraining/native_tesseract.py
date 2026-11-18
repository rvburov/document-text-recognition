"""Small C API reader for exact text-line confidence; standalone, without OpenCV/Paddle loading."""
import ctypes as c
import ctypes.util


class Reader:
    def __init__(self, tessdata, languages, psm=7, capture_symbols=False):
        self.capture_symbols = capture_symbols
        self.symbols = []
        self.tess = c.CDLL(ctypes.util.find_library("tesseract"))
        self.lept = c.CDLL(ctypes.util.find_library("lept") or ctypes.util.find_library("leptonica"))
        def api(name, result, arguments, library=None):
            fn = getattr(library or self.tess, name)
            fn.restype, fn.argtypes = result, arguments
            return fn
        ptr = c.c_void_p
        self.create = api("TessBaseAPICreate", ptr, [])
        self.delete = api("TessBaseAPIDelete", None, [ptr])
        self.init = api("TessBaseAPIInit3", c.c_int, [ptr, c.c_char_p, c.c_char_p])
        self.psm = api("TessBaseAPISetPageSegMode", None, [ptr, c.c_int])
        self.set_image = api("TessBaseAPISetImage2", None, [ptr, ptr])
        self.dpi = api("TessBaseAPISetSourceResolution", None, [ptr, c.c_int])
        self.recognize = api("TessBaseAPIRecognize", c.c_int, [ptr, ptr])
        self.text = api("TessBaseAPIGetUTF8Text", ptr, [ptr])
        self.delete_text = api("TessDeleteText", None, [ptr])
        self.clear = api("TessBaseAPIClear", None, [ptr])
        self.iterator = api("TessBaseAPIGetIterator", ptr, [ptr])
        self.next = api("TessResultIteratorNext", c.c_int, [ptr, c.c_int])
        self.result_text = api("TessResultIteratorGetUTF8Text", ptr, [ptr, c.c_int])
        self.confidence = api("TessResultIteratorConfidence", c.c_float, [ptr, c.c_int])
        self.delete_iterator = api("TessResultIteratorDelete", None, [ptr])
        self.pix_read = api("pixRead", ptr, [c.c_char_p], self.lept)
        self.pix_destroy = api("pixDestroy", None, [c.POINTER(ptr)], self.lept)
        self.handle = self.create()
        if not self.handle:
            raise RuntimeError("Tesseract allocation failed")
        if self.init(self.handle, str(tessdata).encode(), languages.encode()):
            self.close()
            raise RuntimeError("Tesseract model initialization failed")
        self.psm(self.handle, psm)

    def read(self, image, dpi):
        pix = c.c_void_p(self.pix_read(str(image).encode()))
        if not pix.value:
            raise RuntimeError(f"Leptonica cannot read {image}")
        try:
            self.set_image(self.handle, pix)
            self.dpi(self.handle, int(dpi))
            if self.recognize(self.handle, None):
                raise RuntimeError("Tesseract recognition failed")
            text_ptr = self.text(self.handle)
            try:
                text = c.string_at(text_ptr).decode("utf-8") if text_ptr else ""
            finally:
                if text_ptr: self.delete_text(text_ptr)
            values = []
            iterator = self.iterator(self.handle)
            if iterator:
                try:
                    while True:
                        values.append(self.confidence(iterator, 2) / 100.0)  # RIL_TEXTLINE
                        if not self.next(iterator, 2): break
                finally:
                    self.delete_iterator(iterator)
            self.symbols = []
            if self.capture_symbols:
                iterator = self.iterator(self.handle)
                if iterator:
                    try:
                        while True:
                            symbol_ptr = self.result_text(iterator, 4)  # RIL_SYMBOL
                            try:
                                symbol = c.string_at(symbol_ptr).decode("utf-8") if symbol_ptr else ""
                            finally:
                                if symbol_ptr: self.delete_text(symbol_ptr)
                            self.symbols.append({"text": symbol, "confidence": self.confidence(iterator, 4) / 100.0})
                            if not self.next(iterator, 4): break
                    finally:
                        self.delete_iterator(iterator)
            return text, values
        finally:
            self.clear(self.handle)
            self.pix_destroy(c.byref(pix))

    def close(self):
        if self.handle:
            self.delete(self.handle)
            self.handle = None
