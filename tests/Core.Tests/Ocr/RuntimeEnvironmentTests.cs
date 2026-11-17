using System.Runtime.InteropServices;
using DocumentTextRecognition.Core.Ocr;
using DocumentTextRecognition.Tests.Samples;
using OpenCvSharp;

namespace DocumentTextRecognition.Tests.Ocr;

[CollectionDefinition("Runtime environment", DisableParallelization = true)]
public sealed class RuntimeEnvironmentCollection;

/// <summary>Настройка должна доходить до getenv нативных библиотек и сохранять явные значения.</summary>
[Collection("Runtime environment")]
public sealed class RuntimeEnvironmentTests
{
    [OcrFact]
    public void Missing_variable_receives_default_in_managed_and_native_environment()
    {
        WithVariable(name =>
        {
            Assert.Null(Environment.GetEnvironmentVariable(name));
            Assert.Null(NativeValue(name));

            OcrComponents.SetDefaultEnvironmentVariable(name, "1");

            Assert.Equal("1", Environment.GetEnvironmentVariable(name));
            Assert.Equal("1", NativeValue(name));
            OcrComponents.SetDefaultEnvironmentVariable(name, "4");
            Assert.Equal("1", Environment.GetEnvironmentVariable(name));
            Assert.Equal("1", NativeValue(name));
        });
    }

    [OcrFact]
    public void Explicit_managed_value_is_preserved_and_reaches_native_library()
    {
        WithVariable(name =>
        {
            Environment.SetEnvironmentVariable(name, "3");

            OcrComponents.SetDefaultEnvironmentVariable(name, "1");

            Assert.Equal("3", Environment.GetEnvironmentVariable(name));
            Assert.Equal("3", NativeValue(name));
        });
    }

    [OcrFact]
    public void Explicit_native_value_is_preserved_and_reported_to_managed_code()
    {
        WithVariable(name =>
        {
            SetNative(name, "2");

            OcrComponents.SetDefaultEnvironmentVariable(name, "1");

            Assert.Equal("2", NativeValue(name));
            Assert.Equal("2", Environment.GetEnvironmentVariable(name));
        });
    }

    [OcrFact]
    public void Repeated_runtime_configuration_does_not_reset_an_existing_opencv_pool()
    {
        // Инициализатор тестовой сборки уже выполнил настройку до любых OpenCV-тестов.
        var original = Cv2.GetNumThreads();
        try
        {
            Cv2.SetNumThreads(2);
            var explicitThreads = Cv2.GetNumThreads();
            Parallel.For(0, 16, _ => OcrComponents.ConfigureRuntime());
            Assert.Equal(explicitThreads, Cv2.GetNumThreads());
        }
        finally { Cv2.SetNumThreads(original); }
    }

    private static void WithVariable(Action<string> test)
    {
        // Не меняем реальные OMP/BLAS настройки после загрузки библиотек.
        var name = "DTR_RUNTIME_TEST_" + Guid.NewGuid().ToString("N");
        try { test(name); }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
            if (OperatingSystem.IsWindows()) PutEnvWindows(name, "");
            else UnsetEnvUnix(name);
        }
    }

    private static string? NativeValue(string name)
    {
        var pointer = OperatingSystem.IsWindows() ? GetEnvWindows(name) : GetEnvUnix(name);
        return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);
    }

    private static void SetNative(string name, string value)
    {
        var result = OperatingSystem.IsWindows() ? PutEnvWindows(name, value) : SetEnvUnix(name, value, 1);
        Assert.Equal(0, result);
    }

    [DllImport("libc", EntryPoint = "getenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetEnvUnix(string name);

    [DllImport("libc", EntryPoint = "setenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetEnvUnix(string name, string value, int overwrite);

    [DllImport("libc", EntryPoint = "unsetenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern int UnsetEnvUnix(string name);

    [DllImport("ucrtbase.dll", EntryPoint = "getenv", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetEnvWindows(string name);

    [DllImport("ucrtbase.dll", EntryPoint = "_putenv_s", CallingConvention = CallingConvention.Cdecl)]
    private static extern int PutEnvWindows(string name, string value);
}
