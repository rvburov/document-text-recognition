using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace DocumentTextRecognition.Common;

/// <summary>
/// Windows: ярлыки в меню «Пуск» и на рабочем столе, запись в «Установленные приложения»
/// (только для текущего пользователя, HKCU, права администратора не нужны), открытие файла
/// и папки в Проводнике.
/// </summary>
public static class WindowsShell
{
    public static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Title + ".lnk");

    public static string DesktopShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Title + ".lnk");

    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>Ярлык .lnk через WScript.Shell (есть в любой Windows).</summary>
    [SupportedOSPlatform("windows")]
    public static void CreateShortcut(string path, string target, string workingDir)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.WorkingDirectory = workingDir;
        link.IconLocation = target + ",0";
        link.Description = AppInfo.Title;
        link.Save();
    }

    [SupportedOSPlatform("windows")]
    public static void RegisterUninstall(string installDir, string version, long sizeKb)
    {
        var exe = Path.Combine(installDir, AppInfo.ExeName);
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRoot + AppInfo.UninstallKey);
        key.SetValue("DisplayName", AppInfo.Title);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", AppInfo.Publisher);
        key.SetValue("DisplayIcon", exe + ",0");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    [SupportedOSPlatform("windows")]
    public static void UnregisterUninstall() =>
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRoot + AppInfo.UninstallKey, throwOnMissingSubKey: false);

    /// <summary>Открыть файл программой по умолчанию (PDF: программа просмотра PDF).</summary>
    public static void Open(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Открыть Проводник с выделенным файлом.</summary>
    public static void ShowInFolder(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        else
            Open(Path.GetDirectoryName(path) ?? path);
    }

    /// <summary>Открыть папку в Проводнике (создается, если ее нет).</summary>
    public static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Open(dir);
    }
}
