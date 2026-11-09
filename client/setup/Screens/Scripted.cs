using Spectre.Console;
using Spectre.Console.Rendering;

namespace Vigil.Setup.Screens;

/// <summary>The key script ran out: the run stops instead of waiting for a key nobody will press.</summary>
internal sealed class ScriptEndedException() : Exception(Strings.UiScriptEnded);

/// <summary>
/// The wizard and the menu driven by a key script (-Keys &lt;file&gt;), for the CI e2e: the real screens, written
/// to a redirected stdout as plain text (no ANSI, no colors), with keys read from the file. Not in the usage:
/// a test hook, not a feature.
///
/// One line = one input: <c>{Enter}</c>, <c>{Esc}</c>, <c>{Up}</c>, <c>{Down}</c>, <c>{X}</c> = the single
/// key X (a menu digit); any other line is typed character by character and confirmed with Enter
/// (an empty line = Enter). Lines starting with # are comments.
/// </summary>
internal sealed class ScriptedConsole : IAnsiConsole
{
    private readonly IAnsiConsole _inner;
    private readonly ScriptedInput _input;

    private ScriptedConsole(IAnsiConsole inner, ScriptedInput input)
    {
        _inner = inner;
        _input = input;
    }

    public static IAnsiConsole Create(string scriptFile)
    {
        var inner = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(Console.Out),
        });
        inner.Profile.Width = 100;
        return new ScriptedConsole(inner, new ScriptedInput(File.ReadAllLines(scriptFile)));
    }

    public Profile Profile => _inner.Profile;
    public IAnsiConsoleCursor Cursor => _inner.Cursor;
    public IAnsiConsoleInput Input => _input;
    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
    public RenderPipeline Pipeline => _inner.Pipeline;
    public void Clear(bool home) => _inner.Clear(home);
    public void Write(IRenderable renderable) => _inner.Write(renderable);
}

internal sealed class ScriptedInput : IAnsiConsoleInput
{
    private readonly Queue<ConsoleKeyInfo> _keys = new();

    public ScriptedInput(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith('#')) continue;
            if (line.Length >= 3 && line[0] == '{' && line[^1] == '}')
            {
                var name = line[1..^1];
                _keys.Enqueue(name.ToLowerInvariant() switch
                {
                    "enter" => Key(ConsoleKey.Enter, '\r'),
                    "esc" => Key(ConsoleKey.Escape, '\u001b'),
                    "up" => Key(ConsoleKey.UpArrow, '\0'),
                    "down" => Key(ConsoleKey.DownArrow, '\0'),
                    _ when name.Length == 1 => Char(name[0]),
                    _ => throw new FormatException($"-Keys: unknown key {line}"),
                });
                continue;
            }
            foreach (var ch in line) _keys.Enqueue(Char(ch));
            _keys.Enqueue(Key(ConsoleKey.Enter, '\r'));
        }
    }

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch) => new(ch, key, false, false, false);

    private static ConsoleKeyInfo Char(char ch)
    {
        var key = ch switch
        {
            >= '0' and <= '9' => ConsoleKey.D0 + (ch - '0'),
            >= 'a' and <= 'z' => ConsoleKey.A + (ch - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo(ch, key, false, false, false);
    }

    public bool IsKeyAvailable() => _keys.Count > 0;

    public ConsoleKeyInfo? ReadKey(bool intercept) =>
        _keys.TryDequeue(out var key) ? key : throw new ScriptEndedException();

    public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken) =>
        Task.FromResult(ReadKey(intercept));
}
