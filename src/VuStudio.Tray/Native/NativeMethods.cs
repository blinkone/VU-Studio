using System.Runtime.InteropServices;
using VuStudio.Session.Signals;

namespace VuStudio.Tray.Native;

/// <summary>Win32 calls used by the session collectors (docs/integrations/windows-sources.md §8).</summary>
internal static partial class NativeMethods
{
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_THEMECHANGED = 0x031A;
    public const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
    public const int WM_ENDSESSION = 0x0016;
    public const int ASFW_ANY = -1;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int WTSSessionInfoEx = 25;
    private const int WTS_CURRENT_SESSION = -1;
    private const int WTS_SESSIONSTATE_LOCK = 0;
    private const int WTS_SESSIONSTATE_UNLOCK = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>Time since the last keyboard/mouse input of this session, or <c>null</c>.</summary>
    public static TimeSpan? GetIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? IdleTime.FromTicks(unchecked((uint)Environment.TickCount), info.dwTime) : null;
    }

    public static nint ForegroundWindow() => GetForegroundWindow();

    public static uint WindowProcessId(nint hwnd)
    {
        if (hwnd == 0)
        {
            return 0;
        }

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>Full image path via <c>PROCESS_QUERY_LIMITED_INFORMATION</c>; <c>null</c> for protected processes.</summary>
    public static string? ProcessImagePath(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static ScreenRect? WindowRect(nint hwnd) =>
        hwnd != 0 && GetWindowRect(hwnd, out var r) ? new ScreenRect(r.Left, r.Top, r.Right, r.Bottom) : null;

    public static ScreenRect? MonitorRect(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return null;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfoW(monitor, ref info)
            ? new ScreenRect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom)
            : null;
    }

    /// <summary>The desktop, the shell's wallpaper windows and the taskbar never count as full-screen apps.</summary>
    public static bool IsShellWindow(nint hwnd)
    {
        if (hwnd == 0 || hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
        {
            return true;
        }

        var buffer = new char[64];
        var length = GetClassNameW(hwnd, buffer, buffer.Length);
        var cls = length > 0 ? new string(buffer, 0, length) : "";
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary><c>QUERY_USER_NOTIFICATION_STATE</c>, or <c>null</c> when the call fails.</summary>
    public static int? NotificationState() => SHQueryUserNotificationState(out var state) == 0 ? state : null;

    /// <summary><c>DwmGetColorizationColor</c> as <c>0xAARRGGBB</c>.</summary>
    public static uint? DwmColorization() => DwmGetColorizationColor(out var color, out _) == 0 ? color : null;

    /// <summary>
    /// Lock state of this session from <c>WTSSessionInfoEx</c> (<c>SessionFlags</c>), <c>null</c> if unknown.
    /// Layout: <c>WTSINFOEXW { DWORD Level; union (8-aligned) { WTSINFOEX_LEVEL1_W { ULONG SessionId; INT SessionState; LONG SessionFlags; ... } } }</c>.
    /// </summary>
    public static bool? IsSessionLocked()
    {
        if (!WTSQuerySessionInformationW(0, WTS_CURRENT_SESSION, WTSSessionInfoEx, out var buffer, out var bytes) || buffer == 0)
        {
            return null;
        }

        try
        {
            if (bytes < 20 || Marshal.ReadInt32(buffer, 0) != 1)
            {
                return null;
            }

            return Marshal.ReadInt32(buffer, 16) switch
            {
                WTS_SESSIONSTATE_LOCK => true,
                WTS_SESSIONSTATE_UNLOCK => false,
                _ => null,
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(nint hWnd, [Out] char[] lpClassName, int nMaxCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref uint lpdwSize);

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int pquns);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetColorizationColor(out uint pcrColorization, [MarshalAs(UnmanagedType.Bool)] out bool pfOpaqueBlend);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint hServer, int sessionId, int wtsInfoClass, out nint ppBuffer, out int pBytesReturned);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint pMemory);
}
