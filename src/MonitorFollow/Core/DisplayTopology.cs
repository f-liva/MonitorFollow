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

    /// <summary>How long to keep retrying while the locked session denies the switch, and how often.</summary>
    private const int DeniedRetryMs = 25000;
    private const int DeniedPollMs = 400;

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray,
        uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private static string DisplaySwitch => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");

    private const int ERROR_ACCESS_DENIED = 5;

    /// <summary>
    /// Applies a projection topology through the API, retrying while the call is denied, and falls back
    /// to DisplaySwitch.exe only if it fails for any other reason.
    ///
    /// For the first seconds after the session is locked the secure desktop owns the display and
    /// SetDisplayConfig answers 5 (ERROR_ACCESS_DENIED); it starts working on its own shortly after, so
    /// the right move is to keep asking rather than give up. Giving up here used to leave the laptop panel
    /// powered but black for over ten seconds.
    /// </summary>
    private static void Apply(uint topology, string switchArg)
    {
        var deadline = Environment.TickCount64 + DeniedRetryMs;
        bool logged = false;
        int rc;
        while (true)
        {
            // SDC_TOPOLOGY_* accepts only SDC_APPLY: any other flag (SDC_ALLOW_PATH_ORDER_CHANGES in
            // particular, which needs SDC_USE_SUPPLIED_DISPLAY_CONFIG) makes the call fail with 87.
            rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology);
            if (rc == 0)
            {
                if (logged) Log.Write($"SetDisplayConfig({switchArg}) accepted once the lock screen settled");
                return;
            }
            if (rc != ERROR_ACCESS_DENIED || Environment.TickCount64 >= deadline) break;
            if (!logged) { Log.Write($"SetDisplayConfig({switchArg}) denied (locked session), retrying"); logged = true; }
            Thread.Sleep(DeniedPollMs);
        }

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
