using System.Drawing;
using System.Drawing.Drawing2D;

namespace Vigil.Ui;

/// <summary>
/// The tray icon, drawn with GDI+ (no resource files): an orange circle with a white pause sign, which
/// children know from videos and games, and which stands out among the grey system icons.
/// </summary>
public static class IconPainter
{
    private const int Size = 32;

    public static Icon TrayIcon() => Build(DrawPause);

    private static Icon Build(Action<Graphics> draw)
    {
        using var bmp = new Bitmap(Size, Size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        draw(g);
        return Icon.FromHandle(bmp.GetHicon());
    }

    // Orange circle, two white bars
    private static void DrawPause(Graphics g)
    {
        using var bg = new SolidBrush(Color.FromArgb(249, 115, 22));  // orange-500
        g.FillEllipse(bg, 1, 1, Size - 2, Size - 2);
        g.FillRectangle(Brushes.White, 9,  8, 5, 16);
        g.FillRectangle(Brushes.White, 18, 8, 5, 16);
    }
}
