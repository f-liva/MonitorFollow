using System.Diagnostics;
using MonitorFollow.Native;

namespace MonitorFollow.Core;

/// <summary>
/// Turns the other screens off/on by changing the Windows projection mode (the same thing Win+P does),
/// through the built-in DisplaySwitch.exe. This deliberately avoids DPMS: polling DDC on a DPMS-off output
/// makes the panel flicker on many GPU drivers.
/// </summary>
public static class DisplayTopology
{
    private static string DisplaySwitch => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");

    private static void Run(string arg)
    {
        using var p = Process.Start(new ProcessStartInfo(DisplaySwitch, arg) { UseShellExecute = false, CreateNoWindow = true });
        p?.WaitForExit(5000);
    }

    /// <summary>"Second screen only": keeps the external monitor, disables the others.</summary>
    public static void ExternalOnly() => Run("/external");

    /// <summary>"Extend": re-enables every monitor in its saved position.</summary>
    public static void Extend() => Run("/extend");

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
