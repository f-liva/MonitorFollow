using MonitorFollow.Core;
using MonitorFollow.UI;

namespace MonitorFollow;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\MonitorFollow.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("MonitorFollow is already running. Look for its icon in the notification area.",
                "MonitorFollow", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("unhandled: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);

        Log.Write($"MonitorFollow {typeof(Program).Assembly.GetName().Version} starting");
        using var ctx = new TrayContext();
        Application.Run(ctx);
    }
}
