using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using MonitorFollow.Core;
using MonitorFollow.Native;

namespace MonitorFollow.UI;

/// <summary>Notification-area icon, its context menu and the global emergency hotkey.</summary>
public sealed class TrayIcon : IDisposable
{
    private const int HotkeyId = 0x4D46;
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    private const uint VK_E = 0x45;
    private const int WM_HOTKEY = 0x0312;

    private readonly App _app;
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _status;
    private readonly MenuItem _pause;
    private readonly MenuItem _startup;
    private readonly HwndSource _hotkeySource;

    public TrayIcon(App app)
    {
        _app = app;

        _status = new MenuItem { Header = "Starting…", IsEnabled = false };
        _pause = new MenuItem { Header = "Pause" };
        _pause.Click += (_, _) => TogglePause();
        _startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = Startup.IsEnabled() };
        _startup.Click += (_, _) => { try { Startup.SetEnabled(_startup.IsChecked); } catch (Exception ex) { Log.Write("startup toggle failed: " + ex.Message); } _startup.IsChecked = Startup.IsEnabled(); };

        var open = new MenuItem { Header = "Open MonitorFollow", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _app.ShowWindow();
        var restore = new MenuItem { Header = "Restore all screens now", InputGestureText = "Ctrl+Alt+Shift+E" };
        restore.Click += (_, _) => _app.ForceExtend();
        var log = new MenuItem { Header = "Open log" };
        log.Click += (_, _) => OpenLog();
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _app.ExitApp();

        var menu = new ContextMenu();
        menu.Items.Add(_status);
        menu.Items.Add(new Separator());
        menu.Items.Add(open);
        menu.Items.Add(_pause);
        menu.Items.Add(restore);
        menu.Items.Add(new Separator());
        menu.Items.Add(_startup);
        menu.Items.Add(log);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "MonitorFollow",
            Icon = IconFactory.TrayIcon(MasterState.Unknown),
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true
        };
        _icon.LeftClickCommand = new RelayCommand(() => _app.ShowWindow());
        _icon.ForceCreate();

        _app.Watcher.StateChanged += s => _app.Dispatcher.BeginInvoke(() => Apply(s));

        _hotkeySource = new HwndSource(new HwndSourceParameters("MonitorFollow.Hotkey") { Width = 0, Height = 0, WindowStyle = 0 });
        _hotkeySource.AddHook(WndProc);
        RegisterHotkey();
    }

    public void RegisterHotkey()
    {
        WindowsApi.UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
        if (!_app.Settings.HotkeyEnabled) return;
        if (!WindowsApi.RegisterHotKey(_hotkeySource.Handle, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_SHIFT | MOD_NOREPEAT, VK_E))
            Log.Write("could not register Ctrl+Alt+Shift+E (already taken by another app)");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            Log.Write("emergency hotkey pressed");
            _app.ForceExtend();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void TogglePause()
    {
        _app.Watcher.Pause(!_app.Watcher.Paused);
        _pause.Header = _app.Watcher.Paused ? "Resume" : "Pause";
    }

    public static void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            if (!File.Exists(Log.FilePath)) File.WriteAllText(Log.FilePath, "");
            Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("open log failed: " + ex.Message); }
    }

    public static string StatusText(MasterState s) => s switch
    {
        MasterState.On => "Monitor on · all screens active",
        MasterState.Off => "Monitor off · second screen only",
        MasterState.NotFound => "Monitor not found",
        MasterState.Paused => "Paused",
        _ => "Starting…"
    };

    private void Apply(MasterState s)
    {
        _icon.Icon = IconFactory.TrayIcon(s);
        var text = StatusText(s);
        _status.Header = text;
        _icon.ToolTipText = "MonitorFollow · " + text;
        if (_app.Settings.ShowNotifications && (s == MasterState.On || s == MasterState.Off))
            _icon.ShowNotification("MonitorFollow", text);
    }

    public void Dispose()
    {
        WindowsApi.UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
        _hotkeySource.RemoveHook(WndProc);
        _hotkeySource.Dispose();
        _icon.Dispose();
    }

    private sealed class RelayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => action();
    }
}
