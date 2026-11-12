using Spectre.Console;
using Spectre.Console.Rendering;
using Vigil.Setup.Core;

namespace Vigil.Setup.Screens;

/// <summary>
/// The look of the wizard and the menu: a few colors and glyphs that read well in Windows 10 conhost (Consolas,
/// no font fallback: no ✓/✗, braille spinners or rounded corners there) and in Windows Terminal.
/// </summary>
internal static class Theme
{
    /// <summary>Windows Terminal (and other terminals that set it) fall back to fonts that have ✓ and ✗.</summary>
    private static readonly bool RichFont = Environment.GetEnvironmentVariable("WT_SESSION") is not null;

    public static string OkGlyph(IAnsiConsole c) => !c.Profile.Capabilities.Unicode ? "+" : RichFont ? "✓" : "√";
    public static string FailGlyph(IAnsiConsole c) => !c.Profile.Capabilities.Unicode ? "x" : RichFont ? "✗" : "×";
    public const string WarnGlyph = "!";

    public static Spinner SpinnerFor(IAnsiConsole c) =>
        c.Profile.Capabilities.Unicode && RichFont ? Spectre.Console.Spinner.Known.Dots : Spectre.Console.Spinner.Known.Line;

    /// <summary>The parent panel's accent (theme.css --accent-line, #120): the same turquoise as the panel. A console
    /// without 24-bit color gets the nearest one Spectre finds (aqua/cyan).</summary>
    public const string AccentTag = "#22d3ee";
    public static readonly Style Accent = new(new Color(0x22, 0xd3, 0xee));
    public static readonly Style Muted = new(Color.Grey);

    public static string Esc(string? text) => Markup.Escape(text ?? "");

    /// <summary>"[green]√[/]" etc. for a check level.</summary>
    public static string Mark(IAnsiConsole c, CheckLevel level) => level switch
    {
        CheckLevel.Pass => $"[green]{OkGlyph(c)}[/]",
        CheckLevel.Warn => $"[yellow]{WarnGlyph}[/]",
        _ => $"[red]{FailGlyph(c)}[/]",
    };

    public static string Tint(CheckLevel level) => level switch
    {
        CheckLevel.Pass => "green",
        CheckLevel.Warn => "yellow",
        _ => "red",
    };

    /// <summary>A screen: cleared, "Vigil · title · version" over a rule, then the step line.</summary>
    public static void Header(IAnsiConsole c, string title, string? step = null)
    {
        c.Clear(true);
        c.Write(new Rule($"[bold {AccentTag}]Vigil[/] [grey]·[/] [bold]{Esc(title)}[/] [grey]· {Esc(AppVersion.Display)}[/]")
            .LeftJustified().RuleStyle(Muted));
        if (step is not null) c.MarkupLine($"[bold]{Esc(step)}[/]");
        c.WriteLine();
    }

    /// <summary>Plain text, wrapped by the console.</summary>
    public static void Text(IAnsiConsole c, string text) => c.MarkupLine(Esc(text));

    public static void Hint(IAnsiConsole c, string text) => c.MarkupLine($"[grey]{Esc(text)}[/]");

    public static void Problem(IAnsiConsole c, CheckLevel level, string text) =>
        c.MarkupLine($"{Mark(c, level)} [{Tint(level)}]{Esc(text)}[/]");

    public static void Blank(IAnsiConsole c) => c.WriteLine();

    /// <summary>Two columns without borders: label (grey) and value.</summary>
    public static IRenderable Facts(IEnumerable<(string Label, string ValueMarkup)> rows)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(2)).AddColumn();
        foreach (var (label, value) in rows) grid.AddRow(new Markup($"[grey]{Esc(label)}[/]"), new Markup(value));
        return grid;
    }

    /// <summary>A list item with a hanging indent.</summary>
    public static void Bullet(IAnsiConsole c, string text)
    {
        var grid = new Grid().AddColumn(new GridColumn().NoWrap().PadRight(1)).AddColumn();
        grid.AddRow(new Markup("[grey]•[/]"), new Markup(Esc(text)));
        c.Write(grid);
    }
}
