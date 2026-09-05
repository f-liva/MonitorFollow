using MonitorFollow.Native;

namespace MonitorFollow.Core;

public enum MasterState { Unknown, On, Off, NotFound, Paused }

/// <summary>
/// Background loop: reads the master monitor's DDC power mode every PollIntervalMs.
///   on  -> off (power button pressed): snapshot windows, switch to "second screen only".
///   off -> on  (power button pressed again): switch back to "extend", restore windows.
/// Only reads VCP 0xD6 of the selected monitor. Never writes DDC, never touches DPMS.
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
    private int _monitorsBefore = 2;

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
        DisplayTopology.Extend();
        DisplayTopology.WaitForMonitors(_monitorsBefore, 10000);
        Thread.Sleep(400);
        var n = WindowsApi.RestoreWindows(_snapshot);
        Log.Write($"force extend done, windows restored: {n}");
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
        bool bound = false;
        bool firstReading = true;

        while (!_stop)
        {
            if (_paused) { Thread.Sleep(500); continue; }
            tick++;

            if (!bound || failStreak >= 10 || tick % 300 == 0)
            {
                bound = Bind();
                failStreak = 0;
                if (!bound)
                {
                    if (State != MasterState.NotFound) Log.Write($"monitor matching '{_settings.MonitorMatch}' not found, retrying");
                    Set(MasterState.NotFound);
                    Thread.Sleep(5000);
                    continue;
                }
                Log.Write($"bound to '{_description}'");
            }

            var v = Ddc.ReadPower(_handle);
            LastPower = v;
            if (v is null) { failStreak++; Thread.Sleep(_settings.PollIntervalMs); continue; }
            failStreak = 0;

            if (v == Ddc.PowerOn) { onCount++; offCount = 0; }
            else if (v == Ddc.PowerStandby || v == Ddc.PowerOff) { offCount++; onCount = 0; }

            if (State != MasterState.Off && offCount >= Math.Max(1, _settings.OffDebounce))
            {
                OnMasterOff(v.Value);
            }
            else if (State != MasterState.On && onCount >= 1)
            {
                if (State == MasterState.Off) OnMasterOn(v.Value);
                else if (firstReading && WindowsApi.MonitorCount() < 2)
                {
                    Log.Write("startup: master is on but other screens are disabled -> extend");
                    DisplayTopology.Extend();
                }
                else Log.Write($"initial state: master on ({Ddc.PowerToText(v)})");
                Set(MasterState.On);
            }
            firstReading = false;
            Thread.Sleep(_settings.PollIntervalMs);
        }
        Log.Write("watcher stopped");
    }

    private void OnMasterOff(int v)
    {
        _monitorsBefore = Math.Max(2, WindowsApi.MonitorCount());
        _snapshot = _settings.RestoreWindows ? WindowsApi.SnapshotNonPrimaryWindows() : new();
        Log.Write($"master off ({Ddc.PowerToText(v)}) -> second screen only; windows saved: {_snapshot.Count}");
        Set(MasterState.Off);
        DisplayTopology.ExternalOnly();
    }

    private void OnMasterOn(int v)
    {
        Log.Write($"master on ({Ddc.PowerToText(v)}) -> extend");
        DisplayTopology.Extend();
        var ok = DisplayTopology.WaitForMonitors(_monitorsBefore, 10000);
        if (_settings.RestoreWindows && _snapshot.Count > 0)
        {
            Thread.Sleep(400);
            var n = WindowsApi.RestoreWindows(_snapshot);
            Log.Write($"monitors back: {ok}; windows restored: {n}");
            Thread.Sleep(1500);
            n = WindowsApi.RestoreWindows(_snapshot);
            Log.Write($"second restore pass: {n}");
        }
        Set(MasterState.On);
    }

    public void Dispose() => Stop();
}
