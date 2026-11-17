using DocumentTextRecognition.Core.Imaging;

namespace DocumentTextRecognition.Core.Recognition;

/// <summary>Подробности только для явной проверки скана. Обычный прогон их не собирает.</summary>
internal sealed record RecognitionDiagnostic(string Stage, int PageNumber, string Message, FieldDiagnostic? Field = null);

internal sealed record EngineDiagnostic(string Engine, string RawText, string ParsedText, string? Problem);

/// <summary>Подготовленный для OCR фрагмент и независимые ответы обоих движков.</summary>
internal sealed record FieldDiagnostic(FieldRole Role, NormRect Bounds, byte[] CropPng,
    EngineDiagnostic First, EngineDiagnostic Second);
