using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MonitorFollow.Native;

/// <summary>DDC/CI access to physical monitors through dxva2.dll. Only VCP 0xD6 (Power Mode) is read; nothing is ever written.</summary>
public static class Ddc
{
    public const byte VcpPowerMode = 0xD6;
    public const int PowerOn = 1;      // MCCS: 01 = on
    public const int PowerStandby = 4; // MCCS: 04 = standby / off (soft)
    public const int PowerOff = 5;     // MCCS: 05 = power off (hard, button)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr handle);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr handle, byte code, out uint type, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool GetCapabilitiesStringLength(IntPtr handle, out uint length);
    [DllImport("dxva2.dll")] private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr handle, StringBuilder caps, uint length);

    /// <summary>Enumerates every physical monitor. The caller owns the handles and must call <see cref="DestroyPhysicalMonitor"/> on each.</summary>
    public static List<PhysicalMonitor> Enumerate()
    {
        var hmons = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) => { hmons.Add(h); return true; }, IntPtr.Zero);
        var result = new List<PhysicalMonitor>();
        foreach (var hm in hmons)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hm, out var n) || n == 0) continue;
            var arr = new PhysicalMonitor[n];
            if (GetPhysicalMonitorsFromHMONITOR(hm, n, arr)) result.AddRange(arr);
        }
        return result;
    }

    /// <summary>Reads VCP 0xD6. Returns null when the monitor does not answer (also happens briefly during power transitions).</summary>
    public static int? ReadPower(IntPtr handle)
        => GetVCPFeatureAndVCPFeatureReply(handle, VcpPowerMode, out _, out var cur, out _) ? (int)cur : null;

    public static string? ReadCapabilities(IntPtr handle)
    {
        if (!GetCapabilitiesStringLength(handle, out var len) || len == 0) return null;
        var sb = new StringBuilder((int)len);
        return CapabilitiesRequestAndCapabilitiesReply(handle, sb, len) ? sb.ToString() : null;
    }

    /// <summary>True when the MCCS capabilities string advertises VCP D6.</summary>
    public static bool SupportsPowerMode(string? caps)
    {
        // MCCS capabilities look like "(prot(monitor)type(LCD)cmds(...)vcp(02 04 ... 14(04 0B) ... D6(01 04 05) ...)mccs_ver(2.1))".
        // Nested groups inside vcp(...) make a simple [^)]* match fail, so scan the vcp section token by token.
        if (caps is null) return false;
        var m = Regex.Match(caps, @"vcp\s*\(", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        int depth = 0, i = m.Index + m.Length - 1;
        var start = i + 1;
        for (; i < caps.Length; i++)
        {
            if (caps[i] == '(') depth++;
            else if (caps[i] == ')' && --depth == 0) break;
        }
        var section = caps.Substring(start, Math.Max(0, i - start));
        return Regex.IsMatch(section, @"(^|[\s(])D6([\s()]|$)", RegexOptions.IgnoreCase);
    }

    public static string PowerToText(int? v) => v switch
    {
        null => "no reply",
        1 => "on",
        2 => "standby (DPMS)",
        3 => "suspend (DPMS)",
        4 => "off (soft)",
        5 => "off (power button)",
        _ => $"unknown ({v})"
    };
}
