using System.Windows.Media;

namespace Vigil.Ui;

/// <summary>
/// Every color of vigil_ui (#120). The states and the accent are the parent panel's
/// (server/app/static/theme.css: --time, --night, --lock, --pause, --accent-*), so a lock looks the same on the
/// PC and in the panel. The overlay stays black with dim text: a locked screen at night must not light up the
/// room. XAML reads these with {x:Static ui:Palette.…}.
/// </summary>
public static class Palette
{
    public static readonly SolidColorBrush Background = Make(0x00, 0x00, 0x00);
    public static readonly SolidColorBrush Title = Make(0xc4, 0xc4, 0xc4);
    public static readonly SolidColorBrush TitleNight = Make(0x8a, 0x8a, 0x8a);
    public static readonly SolidColorBrush Detail = Make(0x80, 0x80, 0x80);
    public static readonly SolidColorBrush DetailNight = Make(0x5e, 0x5e, 0x5e);
    public static readonly SolidColorBrush Muted = Make(0x5a, 0x5a, 0x5a);
    public static readonly SolidColorBrush Control = Make(0x8a, 0x8a, 0x8a);
    public static readonly SolidColorBrush ControlBorder = Make(0x33, 0x33, 0x33);
    public static readonly SolidColorBrush ControlHover = Make(0x12, 0x12, 0x12);
    public static readonly SolidColorBrush Field = Make(0x05, 0x05, 0x05);
    public static readonly SolidColorBrush FieldText = Make(0xb8, 0xb8, 0xb8);
    /// <summary>A wrong code or a failure: dim red, readable without glowing at night.</summary>
    public static readonly SolidColorBrush Problem = Make(0xa0, 0x58, 0x58);

    // theme.css --accent-border, --accent-text, --accent-line
    public static readonly SolidColorBrush AccentBorder = Make(0x15, 0x5e, 0x75);
    public static readonly SolidColorBrush AccentText = Make(0x67, 0xe8, 0xf9);
    public static readonly SolidColorBrush AccentLine = Make(0x22, 0xd3, 0xee);
    // theme.css --accent: the tray icon
    public static readonly System.Drawing.Color TrayIcon = System.Drawing.Color.FromArgb(0x08, 0x91, 0xb2);

    // theme.css state colors
    public static readonly SolidColorBrush Time = Make(0x8f, 0xa5, 0xc2);
    public static readonly SolidColorBrush Night = Make(0xa9, 0x9c, 0xd6);
    public static readonly SolidColorBrush Lock = Make(0xd6, 0x8f, 0x8f);
    public static readonly SolidColorBrush Pause = Make(0xa3, 0xa3, 0xa3);
    /// <summary>No server, no network or a rejected key: not a parent's lock, so no state color.</summary>
    public static readonly SolidColorBrush Offline = Make(0x9a, 0x9a, 0x9a);

    public static SolidColorBrush ForState(LockReason reason) => reason switch
    {
        LockReason.Limit => Time,
        LockReason.Night => Night,
        LockReason.Manual => Lock,
        LockReason.Pause => Pause,
        _ => Offline,
    };

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The badge icons: outlines on a 24×24 grid, drawn with the state's color (no icon font: Segoe Fluent
/// Icons is not on Windows 10).</summary>
public static class Icons
{
    public static readonly Geometry Hourglass = Make(
        "M6,3 H18 M6,21 H18 M7.5,3 C7.5,8.5 12,9.5 12,12 C12,14.5 7.5,15.5 7.5,21 " +
        "M16.5,3 C16.5,8.5 12,9.5 12,12 C12,14.5 16.5,15.5 16.5,21");
    public static readonly Geometry Moon = Make("M20,14.2 A8.5,8.5 0 1 1 9.8,4 A6.8,6.8 0 0 0 20,14.2 Z");
    public static readonly Geometry Padlock = Make("M6,11 H18 V21 H6 Z M8.5,11 V7.5 A3.5,3.5 0 0 1 15.5,7.5 V11 M12,15 V17");
    public static readonly Geometry PauseBars = Make("M9,6 V18 M15,6 V18");
    public static readonly Geometry NoSignal = Make("M5,20 V17 M10,20 V13 M15,20 V9 M20,20 V5 M3,3 L21,21");
    public static readonly Geometry KeyOff = Make(
        "M5,15.5 A3.5,3.5 0 1 0 12,15.5 A3.5,3.5 0 1 0 5,15.5 M11,13 L19.5,4.5 M17,7 L19,9 M3,3 L21,21");
    public static readonly Geometry Power = Make("M12,3 V11 M7.5,6.2 A8,8 0 1 0 16.5,6.2");

    public static Geometry ForState(LockReason reason) => reason switch
    {
        LockReason.Limit => Hourglass,
        LockReason.Night => Moon,
        LockReason.Manual => Padlock,
        LockReason.Pause => PauseBars,
        LockReason.DeviceUnpaired => KeyOff,
        _ => NoSignal,
    };

    private static Geometry Make(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
