using System.Diagnostics;
using System.Runtime.InteropServices;
using MonitorFollow.Native;

namespace MonitorFollow.Core;

/// <summary>
/// Turns the other screens off/on by changing the Windows projection mode (the same thing Win+P does).
/// This deliberately avoids DPMS: polling DDC on a DPMS-off output makes the panel flicker on many GPU drivers.
///
/// The switch goes through SetDisplayConfig, not DisplaySwitch.exe: DisplaySwitch is a desktop app and does
/// nothing at all while the session is locked (the active desktop is LogonUI's), which left the laptop panel
/// powered but black. SetDisplayConfig is the API DisplaySwitch calls internally and works regardless.
/// DisplaySwitch stays as a fallback for the rare case the API refuses the topology.
/// </summary>
public static class DisplayTopology
{
    private const uint SDC_APPLY = 0x00000080;
    private const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    private const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray,
        uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private static string DisplaySwitch => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");

    /// <summary>Applies a projection topology through the API; falls back to DisplaySwitch.exe if it fails.</summary>
    private static void Apply(uint topology, string switchArg)
    {
        // SDC_TOPOLOGY_* accepts only SDC_APPLY: any other flag (SDC_ALLOW_PATH_ORDER_CHANGES in
        // particular, which needs SDC_USE_SUPPLIED_DISPLAY_CONFIG) makes the call fail with 87.
        int rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);
        if (rc == 0) return;

        Log.Write($"SetDisplayConfig({switchArg}) failed with {rc}, falling back to DisplaySwitch");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(DisplaySwitch, switchArg) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
        }
        catch (Exception ex) { Log.Write($"DisplaySwitch {switchArg} could not start: {ex.Message}"); }
    }

    /// <summary>"Second screen only": keeps the external monitor, disables the others.</summary>
    public static void ExternalOnly() => Apply(SDC_TOPOLOGY_EXTERNAL, "/external");

    /// <summary>"Extend": re-enables every monitor in its saved position.</summary>
    public static void Extend() => Apply(SDC_TOPOLOGY_EXTEND, "/extend");

    /// <summary>Waits until at least <paramref name="count"/> monitors are back in the topology.</summary>
    public static bool WaitForMonitors(int count, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (WindowsApi.MonitorCount() >= count) return true;
            Thread.Sleep(200);
        }
        return WindowsApi.MonitorCount() >= count;
    }
}
