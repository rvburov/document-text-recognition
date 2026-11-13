using System.Runtime.InteropServices;
using DocumentTextRecognition.Common;

namespace DocumentTextRecognition.Services;

/// <summary>
/// Уведомление Windows по окончании работы: всплывающее сообщение у часов (значок программы в
/// области уведомлений на несколько секунд). Только Windows; ошибки не мешают работе.
/// </summary>
public static class Notifier
{
    public static void Show(IntPtr hwnd, string title, string text)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = hwnd,
                uID = 1,
                uFlags = NifIcon | NifTip | NifInfo,
                hIcon = ExtractIconW(IntPtr.Zero, Environment.ProcessPath ?? "", 0),
                szTip = AppInfo.Title,
                szInfo = text,
                szInfoTitle = title,
                dwInfoFlags = NiifInfo,
            };
            Shell_NotifyIconW(NimAdd, ref data);
            // Значок убирается через 15 секунд: сообщение к тому времени показано.
            var copy = data;
            Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Shell_NotifyIconW(NimDelete, ref copy));
        }
        catch (Exception ex)
        {
            AppLog.Error("Уведомление Windows не показано", ex);
        }
    }

    private const int NimAdd = 0, NimDelete = 2;
    private const int NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int NiifInfo = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(int message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIconW(IntPtr instance, string file, int index);
}
