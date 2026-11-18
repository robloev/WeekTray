using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WeekTray;

internal static class WeekIcon
{
    public static int Number(DateTime localDate) => ISOWeek.GetWeekOfYear(localDate);
    public static string Digits(int week) => week.ToString("00", CultureInfo.InvariantCulture);

    public static int TraySize()
    {
        nint taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar == 0 ? 0 : GetDpiForWindow(taskbar);
        int size = GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi); // SM_CXSMICON
        return size > 0 ? size : 16;
    }

    public static Bitmap Render(int week, SleepMode? mode, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            using var path = new GraphicsPath();
            graphics.Clear(mode switch
            {
                SleepMode.On => Color.Black,
                SleepMode.Off => Color.FromArgb(194, 0, 0),
                _ => Color.FromArgb(77, 77, 77)
            });
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var family = new FontFamily("Segoe UI");
            path.AddString(Digits(week), family, (int)FontStyle.Bold, 48, PointF.Empty,
                StringFormat.GenericTypographic);
            RectangleF ink = path.GetBounds();
            float scale = Math.Min(size * .88f / ink.Width, size * .76f / ink.Height);
            using var placement = new Matrix(scale, 0, 0, scale,
                (size - ink.Width * scale) / 2 - ink.X * scale,
                (size - ink.Height * scale) / 2 - ink.Y * scale);
            path.Transform(placement);
            graphics.FillPath(Brushes.White, path);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    public static Icon Create(int week, SleepMode? mode, int size)
    {
        using Bitmap bitmap = Render(week, mode, size);
        nint handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? windowName);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
