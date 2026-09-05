using MonitorFollow.Core;
using MonitorFollow.Native;

namespace MonitorFollow.UI;

public sealed class SettingsForm : Form
{
    private readonly Settings _settings;
    private readonly Watcher _watcher;
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly Label _monitorInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) };
    private readonly NumericUpDown _interval = new() { Minimum = 250, Maximum = 10000, Increment = 250, Width = 90 };
    private readonly NumericUpDown _debounce = new() { Minimum = 1, Maximum = 10, Width = 90 };
    private readonly CheckBox _restore = new() { Text = "Restore window positions when the screens come back", AutoSize = true };
    private readonly CheckBox _notify = new() { Text = "Show a notification on every change", AutoSize = true };
    private readonly CheckBox _hotkey = new() { Text = "Enable emergency hotkey Ctrl+Alt+Shift+E (restore all screens)", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Start with Windows", AutoSize = true };
    private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 140, Width = 440, Font = new Font("Consolas", 8.5f), BackColor = SystemColors.Window };
    private readonly List<(string Description, string Info)> _found = new();
    private readonly Action<string> _logHandler;

    public SettingsForm(Settings settings, Watcher watcher)
    {
        _settings = settings;
        _watcher = watcher;

        Text = "MonitorFollow — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(480, 560);
        Icon = SystemIcons.Application;

        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16), AutoScroll = true };
        Controls.Add(layout);

        layout.Controls.Add(Header("Monitor to follow"));
        layout.Controls.Add(Hint("The monitor whose physical power button controls the other screens. Only monitors that answer DDC/CI power queries are listed."));
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 4, 0, 0) };
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => Detect();
        row.Controls.Add(_monitors); row.Controls.Add(refresh);
        layout.Controls.Add(row);
        layout.Controls.Add(_monitorInfo);
        _monitors.SelectedIndexChanged += (_, _) => UpdateInfo();

        layout.Controls.Add(Header("Behaviour"));
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        grid.Controls.Add(new Label { Text = "Poll interval (ms)", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0); grid.Controls.Add(_interval, 1, 0);
        grid.Controls.Add(new Label { Text = "Consecutive 'off' readings before acting", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1); grid.Controls.Add(_debounce, 1, 1);
        layout.Controls.Add(grid);
        layout.Controls.Add(_restore);
        layout.Controls.Add(_notify);
        layout.Controls.Add(_hotkey);
        layout.Controls.Add(_startup);

        layout.Controls.Add(Header("How it works"));
        layout.Controls.Add(Hint("Power button OFF → Windows switches to \"Second screen only\" (like Win+P). Power button ON → back to \"Extend\", windows restored. Nothing is ever written to the monitor and the panels are never put in DPMS standby, so any key or mouse move can't get you stuck. If something goes wrong: Ctrl+Alt+Shift+E, or Win+P → Extend."));

        layout.Controls.Add(Header("Live log"));
        layout.Controls.Add(_log);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 440, Margin = new Padding(0, 8, 0, 0) };
        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Close", AutoSize = true };
        save.Click += (_, _) => { if (Save()) Close(); };
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save); buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        AcceptButton = save; CancelButton = cancel;

        _interval.Value = Math.Clamp(_settings.PollIntervalMs, 250, 10000);
        _debounce.Value = Math.Clamp(_settings.OffDebounce, 1, 10);
        _restore.Checked = _settings.RestoreWindows;
        _notify.Checked = _settings.ShowNotifications;
        _hotkey.Checked = _settings.HotkeyEnabled;
        _startup.Checked = Startup.IsEnabled();

        _logHandler = line => { if (!IsDisposed) BeginInvoke(() => AppendLog(line)); };
        Log.Written += _logHandler;
        try { if (File.Exists(Log.FilePath)) foreach (var l in File.ReadLines(Log.FilePath).TakeLast(30)) AppendLog(l); } catch { }

        Detect();
    }

    private static Label Header(string t) => new() { Text = t, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) };
    private static Label Hint(string t) => new() { Text = t, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) };

    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        if (_log.Lines.Length > 200) _log.Text = string.Join(Environment.NewLine, _log.Lines.TakeLast(150)) + Environment.NewLine;
    }

    private void Detect()
    {
        _found.Clear(); _monitors.Items.Clear();
        foreach (var pm in Ddc.Enumerate())
        {
            try
            {
                var power = Ddc.ReadPower(pm.Handle);
                var caps = Ddc.ReadCapabilities(pm.Handle);
                bool supports = power is not null || Ddc.SupportsPowerMode(caps);
                if (!supports) continue;
                var info = $"Power mode now: {Ddc.PowerToText(power)}. VCP D6 in capabilities: {(Ddc.SupportsPowerMode(caps) ? "yes" : "unknown")}.";
                _found.Add((pm.Description, info));
                _monitors.Items.Add(pm.Description);
            }
            finally { Ddc.DestroyPhysicalMonitor(pm.Handle); }
        }
        if (_monitors.Items.Count == 0)
        {
            _monitorInfo.Text = "No DDC/CI-capable monitor found. Check that DDC/CI is enabled in the monitor's OSD menu and that it is connected directly to the GPU (not through DisplayLink).";
            return;
        }
        int idx = -1;
        if (!string.IsNullOrWhiteSpace(_settings.MonitorMatch))
            idx = _found.FindIndex(f => f.Description.Contains(_settings.MonitorMatch, StringComparison.OrdinalIgnoreCase));
        _monitors.SelectedIndex = idx >= 0 ? idx : 0;
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_monitors.SelectedIndex < 0) return;
        _monitorInfo.Text = _found[_monitors.SelectedIndex].Info;
    }

    private bool Save()
    {
        if (_monitors.SelectedIndex < 0)
        {
            MessageBox.Show("Select a monitor first.", "MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        // Store the description without the connector suffix so it survives re-plugging on another port.
        var desc = _found[_monitors.SelectedIndex].Description;
        var paren = desc.IndexOf('(');
        var match = (paren > 0 ? desc[..paren] : desc).Trim();
        bool rebind = !string.Equals(match, _settings.MonitorMatch, StringComparison.OrdinalIgnoreCase);

        _settings.MonitorMatch = match;
        _settings.PollIntervalMs = (int)_interval.Value;
        _settings.OffDebounce = (int)_debounce.Value;
        _settings.RestoreWindows = _restore.Checked;
        _settings.ShowNotifications = _notify.Checked;
        _settings.HotkeyEnabled = _hotkey.Checked;
        _settings.FirstRunDone = true;
        try
        {
            _settings.Save();
            if (_startup.Checked != Startup.IsEnabled()) Startup.SetEnabled(_startup.Checked);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not save: " + ex.Message, "MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        Log.Write($"settings saved: monitor='{match}', poll={_settings.PollIntervalMs}ms, debounce={_settings.OffDebounce}, restore={_settings.RestoreWindows}");
        if (rebind) _watcher.Rebind();
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Log.Written -= _logHandler;
        base.OnFormClosed(e);
    }
}
