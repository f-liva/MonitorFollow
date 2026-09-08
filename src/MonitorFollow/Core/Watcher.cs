using MonitorFollow.Native;

namespace MonitorFollow.Core;

public enum MasterState { Unknown, On, Off, NotFound, Paused }

/// <summary>
/// Background loop: reads the master monitor's DDC power mode every PollIntervalMs.
///   on  -> off (power button pressed): snapshot windows, switch to "second screen only".
///   off -> on  (power button pressed again): switch back to "extend", restore windows.
/// Only reads VCP 0xD6 of the selected monitor. Never writes DDC, never touches DPMS.
///
/// Robustness rules (learned the hard way):
///   * Every topology change is verified against the real monitor count and retried, because
///     DisplaySwitch is a no-op while Windows is still renegotiating an HDMI/DP link.
///   * After the monitor disappears (power cut, cable) and comes back, the state is reset so the
///     next valid reading re-applies the right topology instead of trusting stale state.
///   * Physical monitor handles go stale after a re-plug: rebind quickly on read failures.
/// </summary>
public sealed class Watcher : IDisposable
{
    private readonly Settings _settings;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _paused;
    private IntPtr _handle = IntPtr.Zero;
    private string _description = "";
    private List<WindowsApi.WindowSnapshot> _snapshot = new();
    private int _monitorsAll = 2;

    public MasterState State { get; private set; } = MasterState.Unknown;
    public int? LastPower { get; private set; }
    public string MonitorDescription => _description;
    public bool Paused => _paused;

    public event Action<MasterState>? StateChanged;

    public Watcher(Settings settings) => _settings = settings;

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "MonitorFollow.Watcher" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(3000);
        _thread = null;
        ReleaseHandle();
    }

    public void Pause(bool paused)
    {
        _paused = paused;
        Set(paused ? MasterState.Paused : MasterState.Unknown);
        Log.Write(paused ? "paused by user" : "resumed by user");
    }

    /// <summary>Called after the user changed the monitor selection: drop the handle so the loop re-binds.</summary>
    public void Rebind() { ReleaseHandle(); Set(MasterState.Unknown); }

    /// <summary>Emergency: bring every monitor back and restore windows, regardless of the current state.</summary>
    public void ForceExtend()
    {
        Log.Write("force extend requested");
        var ok = ExtendVerified();
        var n = WindowsApi.RestoreWindows(_snapshot);
        Log.Write($"force extend {(ok ? "done" : "could not verify")}, windows restored: {n}");
        if (State == MasterState.Off) Set(MasterState.On);
    }

    private void Set(MasterState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(s);
    }

    private void ReleaseHandle()
    {
        if (_handle != IntPtr.Zero) { Ddc.DestroyPhysicalMonitor(_handle); _handle = IntPtr.Zero; }
    }

    private bool Bind()
    {
        ReleaseHandle();
        var match = _settings.MonitorMatch;
        if (string.IsNullOrWhiteSpace(match)) return false;
        foreach (var pm in Ddc.Enumerate())
        {
            if (_handle == IntPtr.Zero && pm.Description.Contains(match, StringComparison.OrdinalIgnoreCase))
            { _handle = pm.Handle; _description = pm.Description; }
            else Ddc.DestroyPhysicalMonitor(pm.Handle);
        }
        return _handle != IntPtr.Zero;
    }

    private void Loop()
    {
        Log.Write("watcher started");
        int offCount = 0, onCount = 0, failStreak = 0, tick = 0;
        int? lastUnexpected = null;
        bool bound = false;
        bool needResync = true;   // first reading, or monitor just (re)appeared: re-apply topology from scratch

        while (!_stop)
        {
            if (_paused) { Thread.Sleep(500); continue; }
            tick++;

            if (!bound || failStreak >= 3 || tick % 300 == 0)
            {
                bool wasBound = bound;
                bound = Bind();
                failStreak = 0;
                if (!bound)
                {
                    if (State != MasterState.NotFound)
                    {
                        Log.Write($"monitor matching '{_settings.MonitorMatch}' not found (unplugged or unpowered?), retrying");
                        Set(MasterState.NotFound);
                    }
                    needResync = true;
                    Thread.Sleep(3000);
                    continue;
                }
                if (!wasBound || State == MasterState.NotFound) { Log.Write($"bound to '{_description}'"); needResync = true; }
                if (State == MasterState.NotFound) Set(MasterState.Unknown);
            }

            var v = Ddc.ReadPower(_handle);
            LastPower = v;
            if (v is null) { failStreak++; Thread.Sleep(_settings.PollIntervalMs); continue; }
            failStreak = 0;

            if (v == Ddc.PowerOn) { onCount++; offCount = 0; }
            else if (v == Ddc.PowerStandby || v == Ddc.PowerOff) { offCount++; onCount = 0; }
            else if (lastUnexpected != v) { Log.Write($"power mode {Ddc.PowerToText(v)} (ignored)"); lastUnexpected = v; }

            bool wantOff = offCount >= Math.Max(1, _settings.OffDebounce);
            bool wantOn = onCount >= 1;

            if (wantOff && (State != MasterState.Off || needResync))
            {
                OnMasterOff(v.Value, needResync);
                needResync = false;
            }
            else if (wantOn && (State != MasterState.On || needResync))
            {
                OnMasterOn(v.Value, needResync);
                needResync = false;
            }
            Thread.Sleep(_settings.PollIntervalMs);
        }
        Log.Write("watcher stopped");
    }

    private void OnMasterOff(int v, bool resync)
    {
        int count = WindowsApi.MonitorCount();
        if (resync && count <= 1)
        {
            Log.Write($"resync: master {Ddc.PowerToText(v)}, other screens already off");
            Set(MasterState.Off);
            return;
        }
        _monitorsAll = Math.Max(2, count);
        _snapshot = _settings.RestoreWindows ? WindowsApi.SnapshotNonPrimaryWindows() : new();
        Log.Write($"master {Ddc.PowerToText(v)} -> second screen only; windows saved: {_snapshot.Count}");
        Set(MasterState.Off);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            DisplayTopology.ExternalOnly();
            if (WaitForCount(c => c < _monitorsAll, 4000)) { if (attempt > 1) Log.Write($"second screen only applied on attempt {attempt}"); return; }
            Log.Write($"second screen only not applied yet (monitors: {WindowsApi.MonitorCount()}), retrying");
            Thread.Sleep(1500 * attempt);
        }
        Log.Write("WARNING: could not switch to second screen only");
    }

    private void OnMasterOn(int v, bool resync)
    {
        int count = WindowsApi.MonitorCount();
        if (resync && count >= _monitorsAll)
        {
            Log.Write(resync && State == MasterState.Unknown ? $"initial state: master on, {count} screens active" : "resync: all screens already active");
            Set(MasterState.On);
            return;
        }
        Log.Write($"master {Ddc.PowerToText(v)} -> extend");
        var ok = ExtendVerified();
        if (_settings.RestoreWindows && _snapshot.Count > 0)
        {
            Thread.Sleep(400);
            var n = WindowsApi.RestoreWindows(_snapshot);
            Log.Write($"windows restored: {n}");
            Thread.Sleep(1500);
            n = WindowsApi.RestoreWindows(_snapshot);
            Log.Write($"second restore pass: {n}");
        }
        if (!ok) Log.Write("WARNING: extend could not be verified, use Ctrl+Alt+Shift+E or Win+P if a screen is missing");
        Set(MasterState.On);
    }

    /// <summary>Runs "extend" and retries until the monitor count is back, tolerating slow HDMI/DP renegotiation.</summary>
    private bool ExtendVerified()
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            DisplayTopology.Extend();
            if (WaitForCount(c => c >= _monitorsAll, 5000)) { if (attempt > 1) Log.Write($"extend applied on attempt {attempt}"); return true; }
            Log.Write($"extend not applied yet (monitors: {WindowsApi.MonitorCount()}), retrying");
            Thread.Sleep(1000 * attempt);
        }
        return false;
    }

    private static bool WaitForCount(Func<int, bool> ok, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ok(WindowsApi.MonitorCount())) return true;
            Thread.Sleep(200);
        }
        return ok(WindowsApi.MonitorCount());
    }

    public void Dispose() => Stop();
}
