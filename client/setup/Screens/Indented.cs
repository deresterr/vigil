using Spectre.Console;
using Spectre.Console.Rendering;

namespace Vigil.Setup.Screens;

/// <summary>
/// The screens a little off the console window's left edge (#120): everything written through this console
/// starts <see cref="Margin"/> columns in, wrapped to the width that is left. It tracks where the cursor is:
/// the margin goes in at the start of a line only, so a prompt and the keys typed after it stay on one line.
/// Live displays (the spinner) attach to this console's own pipeline, so their lines are indented too.
/// </summary>
internal sealed class IndentedConsole(IAnsiConsole inner) : IAnsiConsole
{
    public const int Margin = 2;

    private bool _atLineStart = true;

    public Profile Profile => inner.Profile;
    public IAnsiConsoleCursor Cursor => inner.Cursor;
    public IAnsiConsoleInput Input => inner.Input;
    public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
    public RenderPipeline Pipeline { get; } = new();

    public void Clear(bool home)
    {
        lock (_gate)
        {
            inner.Clear(home);
            _atLineStart = true;
        }
    }

    // The spinner redraws from its own thread: one write at a time, or two would share the line-start state.
    // No deadlock with Spectre's progress renderer: it never holds its lock while it writes.
    private readonly object _gate = new();

    public void Write(IRenderable renderable)
    {
        lock (_gate)
        {
            var options = RenderOptions.Create(this, Profile.Capabilities);
            inner.Write(new Indented(this, Pipeline.Process(options, new[] { renderable }).ToList()));
        }
    }

    private sealed class Indented(IndentedConsole owner, IReadOnlyList<IRenderable> items) : IRenderable
    {
        public Measurement Measure(RenderOptions options, int maxWidth) => new(maxWidth, maxWidth);

        public IEnumerable<Segment> Render(RenderOptions options, int maxWidth)
        {
            var width = Math.Max(1, maxWidth - Margin);
            foreach (var item in items)
            {
                foreach (var segment in item.Render(options, width))
                {
                    if (segment.IsControlCode)
                    {
                        // A live display goes back with a carriage return before it redraws.
                        if (segment.Text.Contains('\r')) owner._atLineStart = true;
                    }
                    else if (segment.IsLineBreak)
                    {
                        owner._atLineStart = true;
                    }
                    else if (owner._atLineStart && segment.Text.Length > 0)
                    {
                        owner._atLineStart = false;
                        yield return new Segment(new string(' ', Margin));
                    }
                    yield return segment;
                }
            }
        }
    }
}
