using Vigil.Setup.Core;

namespace Vigil.Setup;

/// <summary>Plain console lines for the parameter mode (scripts, CI): one line per step and check.</summary>
public sealed class ConsoleReport : IReport
{
    public void Section(string title)
    {
        Write("==> ", ConsoleColor.Cyan);
        Console.WriteLine(title);
    }

    public void Check(CheckLevel level, string message)
    {
        var (mark, color) = level switch
        {
            CheckLevel.Pass => ("    ✓ ", ConsoleColor.Green),
            CheckLevel.Warn => ("    ! ", ConsoleColor.Yellow),
            _ => ("    ✗ ", ConsoleColor.Red),
        };
        Write(mark, color);
        Console.WriteLine(message);
    }

    public void Info(string message)
    {
        Write("    - ", ConsoleColor.DarkGray);
        Console.WriteLine(message);
    }

    public static void Title(string title)
    {
        Console.WriteLine();
        Write($"[ {title} ]", ConsoleColor.Magenta);
        Console.WriteLine();
        Console.WriteLine();
    }

    public static void Summary(string title, string text, CheckLevel level)
    {
        Console.WriteLine();
        Write($"[ {title}: {text} ]", level switch
        {
            CheckLevel.Pass => ConsoleColor.Green,
            CheckLevel.Warn => ConsoleColor.Yellow,
            _ => ConsoleColor.Red,
        });
        Console.WriteLine();
        Console.WriteLine();
    }

    public static void Error(string message) => Write(message + Environment.NewLine, ConsoleColor.Red);

    private static void Write(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}

public static class ConsoleWindow
{
    /// <summary>Started by a double click (its own console window, which closes on exit): wait for a key.
    /// From a terminal, a script or CI the console is shared with the caller: no pause.</summary>
    public static void PauseIfOwn()
    {
        var processes = new uint[2];
        if (Native.Win32.GetConsoleProcessList(processes, 2) != 1 || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.WriteLine(Strings.PressAnyKey);
        Console.ReadKey(intercept: true);
    }

    /// <summary>True when the window closes with this process (started by a double click or by UAC).</summary>
    public static bool IsOwn()
    {
        var processes = new uint[2];
        return Native.Win32.GetConsoleProcessList(processes, 2) == 1;
    }
}
