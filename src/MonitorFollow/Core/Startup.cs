using Microsoft.Win32;

namespace MonitorFollow.Core;

/// <summary>"Start with Windows" through HKCU\...\Run (per-user, no admin rights needed).</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorFollow";

    private static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string s && s.Trim('"').Equals(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(ValueName, $"\"{ExePath}\"");
        else k.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write($"start with Windows: {enabled}");
    }
}
