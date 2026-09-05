using System.Diagnostics;
using System.Drawing.Drawing2D;
using MonitorFollow.Core;
using MonitorFollow.Native;

namespace MonitorFollow.UI;

/// <summary>Tray icon, context menu, global emergency hotkey. Owns the watcher.</summary>
public sealed class TrayContext : ApplicationContext
{
    private const int HotkeyId = 0x4D46; // "MF"
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    private const uint VK_E = 0x45;

    private readonly Settings _settings;
    private readonly Watcher _watcher;
    private readonly NotifyIcon _tray;
    private readonly HotkeyWindow _hotkeyWindow;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly Dictionary<MasterState, Icon> _icons = new();
    private SettingsForm? _settingsForm;

    public TrayContext()
    {
        _settings = Settings.Load();
        _watcher = new Watcher(_settings);
        _watcher.StateChanged += OnStateChanged;

        foreach (MasterState s in Enum.GetValues<MasterState>()) _icons[s] = MakeIcon(s);

        _statusItem = new ToolStripMenuItem("Status: starting…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = Startup.IsEnabled() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripMenuItem("Restore all screens now  (Ctrl+Alt+Shift+E)", null, (_, _) => ForceExtend()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) => OpenLog()));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => ShowAbout()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _icons[MasterState.Unknown],
            Text = "MonitorFollow",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _hotkeyWindow = new HotkeyWindow(ForceExtend);
        RegisterHotkey();

        if (string.IsNullOrWhiteSpace(_settings.MonitorMatch))
        {
            Log.Write("first run: no monitor selected");
            _tray.ShowBalloonTip(5000, "MonitorFollow", "Choose which monitor to follow in Settings.", ToolTipIcon.Info);
            ShowSettings();
        }
        _watcher.Start();
    }

    // ----- actions -----

    private void TogglePause()
    {
        _watcher.Pause(!_watcher.Paused);
        _pauseItem.Text = _watcher.Paused ? "Resume" : "Pause";
    }

    private void ForceExtend()
    {
        // Runs on a worker so the hotkey/menu never blocks the UI thread.
        Task.Run(() => { try { _watcher.ForceExtend(); } catch (Exception ex) { Log.Write("force extend failed: " + ex.Message); } });
    }

    private void ToggleStartup()
    {
        try { Startup.SetEnabled(!Startup.IsEnabled()); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        _startupItem.Checked = Startup.IsEnabled();
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm(_settings, _watcher);
        _settingsForm.FormClosed += (_, _) => { RegisterHotkey(); _settingsForm = null; };
        _settingsForm.Show();
    }

    private static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            if (!File.Exists(Log.FilePath)) File.WriteAllText(Log.FilePath, "");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void ShowAbout()
    {
        var v = typeof(TrayContext).Assembly.GetName().Version?.ToString(3);
        MessageBox.Show(
            $"MonitorFollow {v}\n\n" +
            "Your laptop screen follows the power button of your external monitor.\n" +
            "Press the monitor's power button: the other screens switch off.\n" +
            "Press it again: everything comes back, windows included.\n\n" +
            "Emergency: Ctrl+Alt+Shift+E restores all screens.\n" +
            "Or press Win+P and choose Extend.\n\n" +
            "MIT License — https://github.com/esperoweb/MonitorFollow",
            "About MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _watcher.Stop();
        Application.Exit();
    }

    // ----- state -----

    private void OnStateChanged(MasterState s)
    {
        if (_tray.ContextMenuStrip?.InvokeRequired == true) { _tray.ContextMenuStrip.BeginInvoke(() => OnStateChanged(s)); return; }
        _tray.Icon = _icons[s];
        var text = s switch
        {
            MasterState.On => "master on — all screens active",
            MasterState.Off => "master off — second screen only",
            MasterState.NotFound => "monitor not found",
            MasterState.Paused => "paused",
            _ => "starting…"
        };
        _statusItem.Text = "Status: " + text;
        _tray.Text = ("MonitorFollow — " + text).Length > 63 ? "MonitorFollow" : "MonitorFollow — " + text;
        if (_settings.ShowNotifications && (s == MasterState.On || s == MasterState.Off))
            _tray.ShowBalloonTip(2000, "MonitorFollow", text, ToolTipIcon.None);
    }

    // ----- hotkey -----

    private void RegisterHotkey()
    {
        WindowsApi.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
        if (!_settings.HotkeyEnabled) return;
        if (!WindowsApi.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_E))
            Log.Write("could not register Ctrl+Alt+Shift+E (already taken by another app)");
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private readonly Action _onHotkey;
        public HotkeyWindow(Action onHotkey) { _onHotkey = onHotkey; CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HotkeyId) { Log.Write("emergency hotkey pressed"); _onHotkey(); }
            base.WndProc(ref m);
        }
        public void Dispose() => DestroyHandle();
    }

    // ----- icon -----

    /// <summary>Draws a small monitor glyph; the screen colour encodes the state. No external assets needed.</summary>
    private static Icon MakeIcon(MasterState s)
    {
        var screen = s switch
        {
            MasterState.On => Color.FromArgb(46, 204, 113),
            MasterState.Off => Color.FromArgb(110, 110, 110),
            MasterState.NotFound => Color.FromArgb(231, 76, 60),
            MasterState.Paused => Color.FromArgb(241, 196, 15),
            _ => Color.FromArgb(52, 152, 219)
        };
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var frame = new SolidBrush(Color.FromArgb(235, 235, 235));
            using var fill = new SolidBrush(screen);
            g.FillRoundedRectangle(frame, new Rectangle(2, 4, 28, 19), 3);
            g.FillRectangle(fill, new Rectangle(5, 7, 22, 13));
            g.FillRectangle(frame, new Rectangle(13, 23, 6, 3));
            g.FillRectangle(frame, new Rectangle(8, 26, 16, 2));
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WindowsApi.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
            _hotkeyWindow.Dispose();
            _watcher.Dispose();
            _tray.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush b, Rectangle r, int radius)
    {
        using var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(b, path);
    }
}
