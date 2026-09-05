using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MonitorFollow.Core;

namespace MonitorFollow.UI;

/// <summary>Draws the monitor glyph used for the tray and the status dot. The screen colour encodes the watcher state.</summary>
public static class IconFactory
{
    public static Color StateColor(MasterState s) => s switch
    {
        MasterState.On => Color.FromRgb(0x3F, 0xB9, 0x50),        // green
        MasterState.Off => Color.FromRgb(0x8A, 0x8A, 0x8A),       // grey
        MasterState.NotFound => Color.FromRgb(0xE8, 0x4C, 0x3D),  // red
        MasterState.Paused => Color.FromRgb(0xF2, 0xC0, 0x1E),    // amber
        _ => Color.FromRgb(0x3A, 0x96, 0xDD)                      // blue
    };

    public static ImageSource TrayImage(MasterState s, int size = 32)
    {
        var screen = new SolidColorBrush(StateColor(s));
        var frame = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        var dark = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
        screen.Freeze(); frame.Freeze(); dark.Freeze();

        var dv = new DrawingVisual();
        using (var g = dv.RenderOpen())
        {
            double u = size / 32.0;
            // bezel
            g.DrawRoundedRectangle(frame, null, new Rect(2 * u, 4 * u, 28 * u, 19 * u), 3 * u, 3 * u);
            // screen
            g.DrawRoundedRectangle(screen, null, new Rect(4.5 * u, 6.5 * u, 23 * u, 14 * u), 1.5 * u, 1.5 * u);
            // subtle gloss
            var gloss = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            g.DrawRoundedRectangle(gloss, null, new Rect(4.5 * u, 6.5 * u, 23 * u, 7 * u), 1.5 * u, 1.5 * u);
            // stand
            g.DrawRectangle(frame, null, new Rect(13 * u, 23 * u, 6 * u, 3 * u));
            g.DrawRoundedRectangle(frame, null, new Rect(8 * u, 26 * u, 16 * u, 2.5 * u), 1 * u, 1 * u);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Same glyph as a GDI icon, which is what the notification area actually needs.</summary>
    public static System.Drawing.Icon TrayIcon(MasterState s)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create((BitmapSource)TrayImage(s, 32)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var gdi = new System.Drawing.Bitmap(ms);
        var h = gdi.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
