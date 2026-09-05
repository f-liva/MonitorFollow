using System.Runtime.InteropServices;

namespace MonitorFollow.Native;

/// <summary>Window and monitor helpers: count active monitors, snapshot/restore window placements, global hotkey.</summary>
public static class WindowsApi
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public POINT MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_SHOWMINIMIZED = 2;
    private const uint MONITORINFOF_PRIMARY = 1;

    public sealed record WindowSnapshot(IntPtr Handle, WINDOWPLACEMENT Placement);

    /// <summary>Number of monitors currently part of the desktop topology.</summary>
    public static int MonitorCount()
    {
        int n = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) => { n++; return true; }, IntPtr.Zero);
        return n;
    }

    private static List<RECT> NonPrimaryMonitorRects()
    {
        var rects = new List<RECT>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
        {
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi) && (mi.Flags & MONITORINFOF_PRIMARY) == 0) rects.Add(mi.Monitor);
            return true;
        }, IntPtr.Zero);
        return rects;
    }

    /// <summary>Captures the placement of every visible top-level window whose centre lies on a non-primary monitor.</summary>
    public static List<WindowSnapshot> SnapshotNonPrimaryWindows()
    {
        var result = new List<WindowSnapshot>();
        var rects = NonPrimaryMonitorRects();
        if (rects.Count == 0) return result;
        EnumWindows((h, l) =>
        {
            if (!IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, 4) == 0 && cloaked != 0) return true;
            var p = new WINDOWPLACEMENT { Length = Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(h, ref p) || p.ShowCmd == SW_SHOWMINIMIZED) return true;
            int cx = (p.NormalPosition.Left + p.NormalPosition.Right) / 2;
            int cy = (p.NormalPosition.Top + p.NormalPosition.Bottom) / 2;
            foreach (var r in rects)
                if (cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom) { result.Add(new WindowSnapshot(h, p)); break; }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Re-applies saved placements. Windows that no longer exist are skipped. Returns how many were restored.</summary>
    public static int RestoreWindows(IEnumerable<WindowSnapshot> snapshots)
    {
        int n = 0;
        foreach (var s in snapshots)
        {
            if (!IsWindow(s.Handle)) continue;
            var p = s.Placement;
            p.Length = Marshal.SizeOf<WINDOWPLACEMENT>();
            p.Flags = 0;
            if (SetWindowPlacement(s.Handle, ref p)) n++;
        }
        return n;
    }
}
