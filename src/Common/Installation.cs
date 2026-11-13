using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace DocumentTextRecognition.Common;

/// <summary>
/// Установка, обновление и удаление.
///
/// <para>Установщик — один файл «…-Setup.exe»: небольшая программа установки, а в конце файла
/// архив ZIP с папкой программы (<see cref="Payload"/>). Установка распаковывает архив в папку
/// рядом с папкой установки, затем меняет папки местами: старая версия остается целой, пока
/// новая не распакована полностью. Если заменить не удалось (программа открыта еще раз),
/// работает прежняя версия.</para>
/// <para>Настройки и журнал лежат отдельно (%APPDATA%) и при обновлении не трогаются.</para>
/// </summary>
public static class Installation
{
    /// <summary>Метка конца архива в установщике.</summary>
    public const string Magic = "DTRPAYLD";

    /// <summary>Где в файле установщика архив: (начало, длина); null, если архива нет.</summary>
    public static (long Offset, long Length)? Payload(string setupPath)
    {
        using var file = File.OpenRead(setupPath);
        if (file.Length < 16) return null;
        file.Seek(-16, SeekOrigin.End);
        var tail = new byte[16];
        file.ReadExactly(tail);
        if (Encoding.ASCII.GetString(tail, 8, 8) != Magic) return null;
        var offset = BitConverter.ToInt64(tail, 0);
        if (offset <= 0 || offset >= file.Length - 16) return null;
        return (offset, file.Length - 16 - offset);
    }

    /// <summary>Дописать архив в конец файла установщика (сборка).</summary>
    public static void AppendPayload(string setupPath, string zipPath)
    {
        using var setup = new FileStream(setupPath, FileMode.Append, FileAccess.Write);
        var offset = setup.Position;
        using (var zip = File.OpenRead(zipPath)) zip.CopyTo(setup);
        setup.Write(BitConverter.GetBytes(offset));
        setup.Write(Encoding.ASCII.GetBytes(Magic));
    }

    /// <summary>Сколько байт программа займет после распаковки.</summary>
    public static long UnpackedSize(string setupPath)
    {
        var p = Payload(setupPath) ?? throw new InvalidDataException("в установщике нет программы");
        using var archive = OpenArchive(setupPath, p);
        return archive.Entries.Sum(e => e.Length);
    }

    private static ZipArchive OpenArchive(string setupPath, (long Offset, long Length) p) =>
        new(new Slice(File.OpenRead(setupPath), p.Offset, p.Length), ZipArchiveMode.Read, leaveOpen: false);

    /// <summary>
    /// Поставить программу из установщика в <paramref name="installDir"/>. progress: доля 0..1 и
    /// текущий файл. Ошибка замены папки (файлы заняты) — исключение с понятным текстом, прежняя
    /// версия при этом остается рабочей.
    /// </summary>
    public static void Install(string setupPath, string installDir, Action<double, string>? progress, CancellationToken ct)
    {
        var p = Payload(setupPath) ?? throw new InvalidDataException("в установщике нет программы");
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installDir))!;
        Directory.CreateDirectory(parent);
        var fresh = installDir + ".new";
        var old = installDir + ".old";
        DeleteDir(fresh);
        DeleteDir(old);

        using (var archive = OpenArchive(setupPath, p))
        {
            var total = Math.Max(1, archive.Entries.Sum(e => e.Length));
            long done = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.GetFullPath(Path.Combine(fresh, entry.FullName));
                if (!target.StartsWith(Path.GetFullPath(fresh) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"недопустимый путь в архиве: {entry.FullName}");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                done += entry.Length;
                progress?.Invoke(done / (double)total, Path.GetDirectoryName(entry.FullName.Replace('/', '\\')) ?? "");
            }
        }

        if (Directory.Exists(installDir))
        {
            try
            {
                Directory.Move(installDir, old);
            }
            catch (IOException ex)
            {
                DeleteDir(fresh);
                throw new IOException("Не удалось заменить файлы: программа открыта. Закройте все ее окна и повторите.", ex);
            }
        }
        Directory.Move(fresh, installDir);
        DeleteDir(old);
        AppLog.Info($"Установлена версия в {installDir}");
    }

    /// <summary>Ярлыки и запись в «Установленные приложения» после установки.</summary>
    public static void Register(string installDir, string version, bool startMenu, bool desktop)
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(installDir, AppInfo.ExeName);
        if (startMenu) WindowsShell.CreateShortcut(WindowsShell.StartMenuShortcut, exe, installDir);
        if (desktop) WindowsShell.CreateShortcut(WindowsShell.DesktopShortcut, exe, installDir);
        var sizeKb = Directory.EnumerateFiles(installDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
        WindowsShell.RegisterUninstall(installDir, version, sizeKb);
    }

    /// <summary>
    /// Удалить программу: ярлыки, запись в «Установленных приложениях», по желанию настройки и
    /// журнал. Папку программы удаляет отдельная команда после закрытия программы (запущенный
    /// файл сам себя удалить не может). Переименованные файлы не затрагиваются.
    /// </summary>
    public static void Uninstall(string installDir, bool removeSettings)
    {
        if (OperatingSystem.IsWindows())
        {
            WindowsShell.UnregisterUninstall();
            foreach (var link in new[] { WindowsShell.StartMenuShortcut, WindowsShell.DesktopShortcut })
                try { File.Delete(link); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (removeSettings) DeleteDir(AppInfo.DataDir);
        if (OperatingSystem.IsWindows() && Directory.Exists(installDir))
        {
            // Пауза около двух секунд, чтобы программа успела закрыться, затем папка удаляется.
            var command = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{installDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }
    }

    /// <summary>Дождаться выхода программы перед обновлением (не дольше минуты).</summary>
    public static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(TimeSpan.FromMinutes(1));
        }
        catch (ArgumentException)
        {
            // Уже закрылась.
        }
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Часть файла как поток только для чтения (архив внутри установщика).</summary>
    private sealed class Slice(Stream inner, long offset, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int index, int count)
        {
            var left = length - _position;
            if (left <= 0) return 0;
            inner.Position = offset + _position;
            var read = inner.Read(buffer, index, (int)Math.Min(count, left));
            _position += read;
            return read;
        }
        public override long Seek(long delta, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => delta,
            SeekOrigin.Current => _position + delta,
            _ => length + delta,
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int index, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
