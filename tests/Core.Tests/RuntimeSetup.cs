using System.Runtime.CompilerServices;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Tests.Samples;

namespace DocumentTextRecognition.Tests;

internal static class RuntimeSetup
{
    // Инициализатор только тестовой сборки: никакой фоновой обработки изображений еще нет.
    // На неподдерживаемых платформах по-прежнему можно выполнять чистые текстовые тесты.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (OcrPlatform.Supported) OcrComponents.ConfigureRuntime();
    }
}
