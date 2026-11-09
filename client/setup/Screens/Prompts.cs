using System.Text;
using Spectre.Console;

namespace Vigil.Setup.Screens;

/// <summary>One menu entry: its shortcut key (a digit), the label and the value it returns.</summary>
internal sealed record Choice<T>(char Key, string Label, T Value);

/// <summary>
/// Input on the console: a menu (arrows + Enter, or the item's digit) and a text field with an editable
/// initial value. Spectre's SelectionPrompt has no shortcuts and refuses to run without ANSI; this works
/// in a legacy console too (no redraw: digits only) and in the CI key script.
/// </summary>
internal static class Prompts
{
    public static T Menu<T>(IAnsiConsole c, IReadOnlyList<Choice<T>> items, char? escapeKey = null)
    {
        var ansi = c.Profile.Capabilities.Ansi;
        var selected = 0;
        var width = items.Max(i => i.Label.Length);

        void Render()
        {
            for (var i = 0; i < items.Count; i++)
            {
                var label = Theme.Esc(items[i].Label.PadRight(width));
                c.MarkupLine(i == selected
                    ? $" [bold aqua]›[/] [aqua]{items[i].Key}[/]  [bold]{label}[/]"
                    : $"   [grey]{items[i].Key}[/]  {label}");
            }
            c.MarkupLine($"[grey]{Theme.Esc(Strings.UiMenuHint)}[/]");
        }

        if (ansi) c.Cursor.Show(false);
        try
        {
            Render();
            while (true)
            {
                var key = c.Input.ReadKey(intercept: true);
                if (key is null) continue;
                var k = key.Value;
                var chosen = -1;
                var moved = selected;
                switch (k.Key)
                {
                    case ConsoleKey.UpArrow: moved = (selected + items.Count - 1) % items.Count; break;
                    case ConsoleKey.DownArrow: moved = (selected + 1) % items.Count; break;
                    case ConsoleKey.Home: moved = 0; break;
                    case ConsoleKey.End: moved = items.Count - 1; break;
                    case ConsoleKey.Enter: chosen = selected; break;
                    case ConsoleKey.Escape when escapeKey is { } esc:
                        chosen = IndexOf(items, esc);
                        break;
                    default:
                        chosen = IndexOf(items, k.KeyChar);
                        break;
                }
                if (chosen >= 0) moved = chosen;
                if (moved != selected && ansi)
                {
                    selected = moved;
                    c.Cursor.MoveUp(items.Count + 1);
                    Render();
                }
                selected = moved;
                if (chosen < 0) continue;
                if (!ansi) c.MarkupLine($" [bold aqua]›[/] [bold]{Theme.Esc(items[chosen].Label)}[/]");
                c.WriteLine();
                return items[chosen].Value;
            }
        }
        finally
        {
            if (ansi) c.Cursor.Show(true);
        }
    }

    private static int IndexOf<T>(IReadOnlyList<Choice<T>> items, char key)
    {
        for (var i = 0; i < items.Count; i++)
            if (char.ToLowerInvariant(items[i].Key) == char.ToLowerInvariant(key)) return i;
        return -1;
    }

    /// <summary>One line of text. <paramref name="initial"/> is already typed (Backspace edits it, Esc clears
    /// the field). Returns the text trimmed; empty when the user confirms an empty field.</summary>
    public static string Line(IAnsiConsole c, string prompt, string initial = "")
    {
        c.Markup($"[bold]{Theme.Esc(prompt)}[/] ");
        var text = new StringBuilder(initial);
        c.Write(initial, Theme.Accent);
        while (true)
        {
            var key = c.Input.ReadKey(intercept: true);
            if (key is null) continue;
            var k = key.Value;
            if (k.Key == ConsoleKey.Enter)
            {
                c.WriteLine();
                return text.ToString().Trim();
            }
            if (k.Key == ConsoleKey.Backspace)
            {
                if (text.Length == 0) continue;
                text.Length--;
                c.Write("\b \b");
                continue;
            }
            if (k.Key == ConsoleKey.Escape)
            {
                if (text.Length > 0) c.Write(string.Concat(Enumerable.Repeat("\b \b", text.Length)));
                text.Clear();
                continue;
            }
            if (char.IsControl(k.KeyChar)) continue;
            text.Append(k.KeyChar);
            c.Write(k.KeyChar.ToString(), Theme.Accent);
        }
    }

    /// <summary>Waits for Enter (any key in a real console would do, but Enter is what the hint says).</summary>
    public static void Continue(IAnsiConsole c, string hint)
    {
        c.WriteLine();
        c.MarkupLine($"[grey]{Theme.Esc(hint)}[/]");
        while (c.Input.ReadKey(intercept: true) is not { Key: ConsoleKey.Enter or ConsoleKey.Escape }) { }
    }
}
