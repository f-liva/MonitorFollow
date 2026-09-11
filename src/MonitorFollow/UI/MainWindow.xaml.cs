using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MonitorFollow.Core;
using MonitorFollow.Native;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace MonitorFollow.UI;

public partial class MainWindow : FluentWindow
{
    private readonly App _app;
    private readonly List<(string Description, string Info)> _found = new();
    private readonly Action<string> _logHandler;
    private readonly DispatcherTimer _savedTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _loading = true;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        HeroIcon.Source = IconFactory.TrayImage(MasterState.Unknown, 64);

        var s = _app.Settings;
        IntervalBox.Value = Math.Clamp(s.PollIntervalMs, 250, 10000);
        DebounceBox.Value = Math.Clamp(s.OffDebounce, 1, 10);
        RestoreToggle.IsChecked = s.RestoreWindows;
        NotifyToggle.IsChecked = s.ShowNotifications;
        HotkeyToggle.IsChecked = s.HotkeyEnabled;
        StartupToggle.IsChecked = Startup.IsEnabled();
        _loading = false;

        _logHandler = line => Dispatcher.BeginInvoke(() => AppendLog(line));
        Log.Written += _logHandler;
        try { if (File.Exists(Log.FilePath)) foreach (var l in File.ReadLines(Log.FilePath).TakeLast(40)) AppendLog(l); } catch { }

        _app.Watcher.StateChanged += OnState;
        _savedTimer.Tick += (_, _) => { SavedText.Text = ""; _savedTimer.Stop(); };

        ApplyState(_app.Watcher.State);
        Detect();
    }

    // ----- status -----

    private void OnState(MasterState s) => Dispatcher.BeginInvoke(() => ApplyState(s));

    private void ApplyState(MasterState s)
    {
        StatusDot.Fill = new SolidColorBrush(IconFactory.StateColor(s));
        StatusText.Text = TrayIcon.StatusText(s);
        HeroIcon.Source = IconFactory.TrayImage(s, 64);
        var w = _app.Watcher;
        StatusDetail.Text = s switch
        {
            MasterState.NotFound => $"No monitor matching “{_app.Settings.MonitorMatch}”. Pick one below.",
            MasterState.Paused => "Polling is paused. The screens will not change until you resume.",
            _ when !string.IsNullOrEmpty(w.MonitorDescription) => $"{w.MonitorDescription} · power mode: {Ddc.PowerToText(w.LastPower)}",
            _ => " "
        };
        PauseButton.Content = w.Paused ? "Resume" : "Pause";
        PauseButton.Icon = new SymbolIcon(w.Paused ? SymbolRegular.Play24 : SymbolRegular.Pause24);
    }

    // ----- monitor detection -----

    private bool _detecting;

    /// <summary>DDC/CI calls can block for seconds (or hang while a monitor renegotiates its link), so they run off the UI thread.</summary>
    private async void Detect()
    {
        if (_detecting) return;
        _detecting = true;
        MonitorCombo.IsEnabled = false;
        MonitorInfo.Text = "Detecting monitors…";
        List<(string Description, string Info)> found;
        try
        {
            found = await Task.Run(() =>
            {
                var list = new List<(string, string)>();
                foreach (var pm in Ddc.Enumerate())
                {
                    try
                    {
                        var power = Ddc.ReadPower(pm.Handle);
                        var caps = Ddc.ReadCapabilities(pm.Handle);
                        bool inCaps = Ddc.SupportsPowerMode(caps);
                        if (power is null && !inCaps) continue;
                        list.Add((pm.Description, $"Power mode now: {Ddc.PowerToText(power)} · VCP D6 advertised: {(inCaps ? "yes" : "unknown")}"));
                    }
                    finally { Ddc.DestroyPhysicalMonitor(pm.Handle); }
                }
                return list;
            });
        }
        catch (Exception ex)
        {
            Log.Write("monitor detection failed: " + ex.Message);
            found = new();
        }
        if (IsDisposedOrClosed()) return;

        _found.Clear();
        _found.AddRange(found);
        MonitorCombo.Items.Clear();
        foreach (var f in _found) MonitorCombo.Items.Add(new ComboBoxItem { Content = f.Description });
        MonitorCombo.IsEnabled = true;
        _detecting = false;

        if (_found.Count == 0)
        {
            MonitorInfo.Text = "No DDC/CI-capable monitor found. Enable DDC/CI in the monitor's OSD menu and make sure it is connected directly to the GPU (DisplayLink docks don't pass DDC/CI).";
            return;
        }
        int idx = string.IsNullOrWhiteSpace(_app.Settings.MonitorMatch) ? -1
            : _found.FindIndex(f => f.Description.Contains(_app.Settings.MonitorMatch, StringComparison.OrdinalIgnoreCase));
        MonitorCombo.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private bool _closed;
    private bool IsDisposedOrClosed() => _closed;

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorCombo.SelectedIndex >= 0 && MonitorCombo.SelectedIndex < _found.Count)
            MonitorInfo.Text = _found[MonitorCombo.SelectedIndex].Info;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Detect();

    // ----- actions -----

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _app.Watcher.Pause(!_app.Watcher.Paused);
        ApplyState(_app.Watcher.State);
    }

    private void ForceExtend_Click(object sender, RoutedEventArgs e) => _app.ForceExtend();

    private void OpenLog_Click(object sender, RoutedEventArgs e) => TrayIcon.OpenLog();

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var v = typeof(App).Assembly.GetName().Version?.ToString(3);
        var box = new MessageBox
        {
            Title = "About MonitorFollow",
            Content = $"MonitorFollow {v}\n\nYour laptop screen follows the power button of your external monitor.\n\n" +
                      "Read-only DDC/CI · no DPMS · Win+P-style topology switch · window restore.\n\n" +
                      "MIT License · github.com/f-liva/MonitorFollow",
            CloseButtonText = "Close"
        };
        await box.ShowDialogAsync();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (MonitorCombo.SelectedIndex < 0 || MonitorCombo.SelectedIndex >= _found.Count)
        {
            SavedText.Text = "Select a monitor first.";
            _savedTimer.Start();
            return;
        }
        var desc = _found[MonitorCombo.SelectedIndex].Description;
        var paren = desc.IndexOf('(');
        var match = (paren > 0 ? desc[..paren] : desc).Trim();
        var s = _app.Settings;
        bool rebind = !string.Equals(match, s.MonitorMatch, StringComparison.OrdinalIgnoreCase);

        s.MonitorMatch = match;
        s.PollIntervalMs = (int)(IntervalBox.Value ?? 1000);
        s.OffDebounce = (int)(DebounceBox.Value ?? 2);
        s.RestoreWindows = RestoreToggle.IsChecked == true;
        s.ShowNotifications = NotifyToggle.IsChecked == true;
        s.HotkeyEnabled = HotkeyToggle.IsChecked == true;
        s.FirstRunDone = true;
        try
        {
            s.Save();
            if ((StartupToggle.IsChecked == true) != Startup.IsEnabled()) Startup.SetEnabled(StartupToggle.IsChecked == true);
        }
        catch (Exception ex)
        {
            SavedText.Text = "Could not save: " + ex.Message;
            _savedTimer.Start();
            return;
        }
        Log.Write($"settings saved: monitor='{match}', poll={s.PollIntervalMs}ms, debounce={s.OffDebounce}, restore={s.RestoreWindows}, hotkey={s.HotkeyEnabled}");
        if (rebind) _app.Watcher.Rebind();
        _app.RefreshHotkey();
        SavedText.Text = "Saved ✓";
        _savedTimer.Start();
    }

    // ----- log -----

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        if (LogBox.LineCount > 300) LogBox.Text = string.Join(Environment.NewLine, LogBox.Text.Split(Environment.NewLine).TakeLast(200));
        LogBox.ScrollToEnd();
    }

    // Closing the window keeps the app alive in the tray.
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closed = true;
        Log.Written -= _logHandler;
        _app.Watcher.StateChanged -= OnState;
        base.OnClosing(e);
    }
}
