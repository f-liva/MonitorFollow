using System.Windows;
using System.Windows.Threading;
using MonitorFollow.Core;
using MonitorFollow.UI;
using Wpf.Ui.Appearance;

namespace MonitorFollow;

public partial class App : Application
{
    private static Mutex? _mutex;
    private Settings _settings = null!;
    private Watcher _watcher = null!;
    private TrayIcon _tray = null!;
    private MainWindow? _window;

    public Settings Settings => _settings;
    public Watcher Watcher => _watcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\MonitorFollow.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("MonitorFollow is already running. Look for its icon in the notification area.",
                "MonitorFollow", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex = null;
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Write("fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Write("task: " + ex.Exception.Message); ex.SetObserved(); };

        Log.Write($"MonitorFollow {typeof(App).Assembly.GetName().Version?.ToString(3)} starting");
        ApplicationThemeManager.ApplySystemTheme();

        _settings = Settings.Load();
        _watcher = new Watcher(_settings);
        _tray = new TrayIcon(this);

        if (string.IsNullOrWhiteSpace(_settings.MonitorMatch))
        {
            Log.Write("first run: no monitor selected");
            ShowWindow();
        }
        else if (e.Args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase)))
        {
            ShowWindow();
        }
        _watcher.Start();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("unhandled: " + e.Exception);
        e.Handled = true;
    }

    public void ShowWindow()
    {
        if (_window is null || !_window.IsLoaded)
        {
            _window = new MainWindow(this);
            _window.Closed += (_, _) => _window = null;
        }
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void RefreshHotkey() => _tray.RegisterHotkey();

    public void ForceExtend()
        => Task.Run(() => { try { _watcher.ForceExtend(); } catch (Exception ex) { Log.Write("force extend failed: " + ex.Message); } });

    public void ExitApp()
    {
        Log.Write("exit requested");
        _tray.Dispose();
        _watcher.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }
}
