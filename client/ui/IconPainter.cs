using System.Drawing;
using System.Drawing.Drawing2D;

namespace Vigil.Ui;

/// <summary>
/// Rysuje ikonę tray przez GDI+ — generowana w locie, bez plików zasobów.
///
/// Jedna statyczna ikona: pomarańczowe koło z białym ⏸.
/// Symbol ⏸ (pauza) jest znany dzieciom z YouTube i gier — od razu wiadomo do czego służy.
/// Kolor pomarańczowy wyróżnia się w tray między szarymi ikonami systemowymi.
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

    // Pomarańczowe koło + dwa białe słupki ⏸
    private static void DrawPause(Graphics g)
    {
        using var bg = new SolidBrush(Color.FromArgb(249, 115, 22));  // orange-500
        g.FillEllipse(bg, 1, 1, Size - 2, Size - 2);
        g.FillRectangle(Brushes.White, 9,  8, 5, 16);
        g.FillRectangle(Brushes.White, 18, 8, 5, 16);
    }
}
