using System.Diagnostics;
using DocumentTextRecognition.Common;

namespace DocumentTextRecognition.Services;

/// <summary>
/// Обновление из папки обновлений: установщик копируется во временную папку (с ходом), затем
/// запускается в режиме обновления; программа закрывается, установщик ставит новую версию и
/// открывает программу снова.
/// </summary>
public static class Updater
{
    public static async Task<string> CopySetup(string folder, IProgress<(long Done, long Total)> progress, CancellationToken ct)
    {
        var source = UpdateSource.SetupPath(folder);
        var dir = Path.Combine(Path.GetTempPath(), "DocumentTextRecognition-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, AppInfo.SetupName);
        await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var to = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        long done = 0, total = from.Length;
        int read;
        while ((read = await from.ReadAsync(buffer, ct)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            progress.Report((done, total));
        }
        return target;
    }

    /// <summary>Запустить установщик обновления; программа после этого закрывается.</summary>
    public static void Launch(string setup)
    {
        AppLog.Info("Запуск обновления");
        Process.Start(new ProcessStartInfo(setup, $"--update --wait {Environment.ProcessId}") { UseShellExecute = false });
    }
}
