using DocumentTextRecognition.Core.Rename;

namespace DocumentTextRecognition.Tests.Rename;

public sealed class WorkerPolicyTests
{
    [Theory]
    [InlineData(4, 16, 8)] // измеренный быстрый вариант на 4 CPU / 16 ГБ
    [InlineData(8, 8, 4)] // память ограничивает число отдельных экземпляров OCR
    [InlineData(4, 4, 2)]
    [InlineData(4, 2, 1)]
    [InlineData(2, 16, 2)] // избыток памяти не отменяет ограничение малого CPU
    [InlineData(1, 16, 1)]
    [InlineData(64, 128, 8)] // крупная машина не создает десятки копий моделей
    public void Auto_workers_fit_representative_computers(int processors, int memoryGiB, int expected)
    {
        Assert.Equal(expected, RunSettings.RecommendWorkers(processors, memoryGiB * 1024L * 1024 * 1024));
    }

    [Theory]
    [InlineData(12, 4, 8)]
    [InlineData(6, 2, 4)]
    [InlineData(3, 1, 2)]
    public void Memory_tier_is_not_enabled_below_its_reserve(int thresholdGiB, int below, int atThreshold)
    {
        var threshold = thresholdGiB * 1024L * 1024 * 1024;
        Assert.Equal(below, RunSettings.RecommendWorkers(8, threshold - 1));
        Assert.Equal(atThreshold, RunSettings.RecommendWorkers(8, threshold));
    }

    [Theory]
    [InlineData(32, 0, 4)]
    [InlineData(2, -1, 2)]
    [InlineData(0, 0, 1)]
    public void Unknown_capacity_uses_conservative_fallback(int processors, long memoryBytes, int expected)
    {
        Assert.Equal(expected, RunSettings.RecommendWorkers(processors, memoryBytes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void User_selected_workers_override_auto_policy(int workers)
    {
        var settings = new RunSettings("templates", "source", false, Workers: workers);
        Assert.Equal(workers, settings.EffectiveWorkers);
    }
}
