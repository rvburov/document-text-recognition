using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentTextRecognition.Core.Recognition;

namespace DocumentTextRecognition.Core.Rename;

/// <summary>Точная конфигурация штатного распознавателя; передается по закрытому pipe,
/// а не через аргументы командной строки. Обработка файлов в дочернем процессе read-only.</summary>
internal sealed record OcrWorkerConfiguration(string TemplatesFolder, string Tessdata,
    bool UseOneDnn, bool ReusePredictors, bool UseOnnx, string? OnnxModelsDir,
    bool KnownLines, bool BatchFields, bool UseAvx2);

/// <summary>Исполняемый файл и начальные аргументы, до служебного --ocr-worker.</summary>
internal sealed record OcrWorkerLaunchCommand(string FileName, string[] Arguments);

/// <summary>Ошибка процесса/протокола, а не недоступный исходный PDF. Не наследует IOException:
/// основной прогон должен показать «Ошибка», не «файл недоступен».</summary>
internal sealed class OcrWorkerException : InvalidOperationException
{
    public OcrWorkerException(string message, Exception? inner = null) : base(message, inner) { }
}

internal sealed record OcrWorkerMessage(string Kind, long RequestId = 0,
    OcrWorkerConfiguration? Configuration = null, string? Path = null,
    DocumentResult? Result = null, string? Error = null, double? CpuSeconds = null,
    string? CoreSha256 = null, bool? Avx2Available = null);

/// <summary>Длина UTF-8 JSON + JSON + LF. Ограниченный кадр не позволяет поврежденному
/// ответу бесконечно накапливаться в памяти; stdout/stderr никогда не являются протоколом.</summary>
internal static class OcrWorkerProtocol
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
    };

    internal static async ValueTask WriteAsync(Stream stream, OcrWorkerMessage message, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (json.Length > MaxFrameBytes) throw new InvalidDataException("Слишком большой кадр OCR worker.");
        var frame = new byte[json.Length + 5];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        frame[^1] = (byte)'\n';
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    internal static async ValueTask<OcrWorkerMessage?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header, ct).ConfigureAwait(false);
        if (first == 0) return null;
        await stream.ReadExactlyAsync(header.AsMemory(first), ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameBytes) throw new InvalidDataException("Повреждена длина кадра OCR worker.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        var ending = new byte[1];
        await stream.ReadExactlyAsync(ending, ct).ConfigureAwait(false);
        if (ending[0] != (byte)'\n') throw new InvalidDataException("Поврежден конец кадра OCR worker.");
        return JsonSerializer.Deserialize<OcrWorkerMessage>(payload, Json)
            ?? throw new InvalidDataException("Пустой JSON OCR worker.");
    }
}
