using Microsoft.ML.OnnxRuntime;

namespace DocumentTextRecognition.Core.Ocr;

/// <summary>Настройки только явно выбранного экспериментального ONNX backend.</summary>
internal static class OnnxRuntimeSetup
{
    private static readonly Lazy<bool> TelemetryDisabled = new(() =>
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static SessionOptions CreateSessionOptions()
    {
        _ = TelemetryDisabled.Value;
        return new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
    }
}
