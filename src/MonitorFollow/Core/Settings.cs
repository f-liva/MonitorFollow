using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorFollow.Core;

public sealed class Settings
{
    public static string Folder { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorFollow");
    public static string FilePath { get; } = Path.Combine(Folder, "settings.json");

    /// <summary>Substring of the monitor description (e.g. "Dell U3415W") identifying the monitor whose power button drives everything.</summary>
    public string MonitorMatch { get; set; } = "";
    public int PollIntervalMs { get; set; } = 1000;
    /// <summary>Consecutive "off" readings required before acting (filters glitches).</summary>
    public int OffDebounce { get; set; } = 2;
    public bool RestoreWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = false;
    public bool HotkeyEnabled { get; set; } = true;
    public bool FirstRunDone { get; set; } = false;

    [JsonIgnore] public bool Exists => File.Exists(FilePath);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOpts) ?? new Settings();
        }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
    }
}
