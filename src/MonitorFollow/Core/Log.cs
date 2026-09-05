namespace MonitorFollow.Core;

public static class Log
{
    public static string FilePath { get; } = Path.Combine(Settings.Folder, "monitorfollow.log");
    private static readonly object Gate = new();
    public static event Action<string>? Written;

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* logging must never break the app */ }
        }
        Written?.Invoke(line);
    }
}
