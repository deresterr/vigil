using Spectre.Console;
using Vigil.Setup.Core;

namespace Vigil.Setup.Screens;

/// <summary>
/// What the parent sees of an operation: one line per step (IReport.Section) with √, ! or ×, and below a step
/// only its warnings and failures. Details (Info, passed checks) go to the log only. The step that is running
/// shows next to a spinner.
/// </summary>
internal sealed class StepsView : IReport
{
    private readonly IAnsiConsole _c;
    private StatusContext? _status;
    private string? _current;
    private readonly List<(CheckLevel Level, string Text)> _notes = new();

    public int Warnings { get; private set; }
    public int Failures { get; private set; }

    public StepsView(IAnsiConsole c) => _c = c;

    public void Section(string title)
    {
        Complete();
        _current = title;
        if (_status is not null) _status.Status = Theme.Esc(title) + "…";
    }

    public void Check(CheckLevel level, string message)
    {
        if (level == CheckLevel.Pass) return;
        if (level == CheckLevel.Warn) Warnings++; else Failures++;
        _notes.Add((level, message));
    }

    public void Info(string message) { }

    /// <summary>Prints the running step as finished: its worst level and its notes.</summary>
    public void Complete()
    {
        if (_current is null) return;
        var level = _notes.Count == 0 ? CheckLevel.Pass : _notes.Max(n => n.Level);
        _c.MarkupLine($"  {Theme.Mark(_c, level)} {Theme.Esc(_current)}");
        foreach (var (noteLevel, text) in _notes)
            _c.MarkupLine($"      [{Theme.Tint(noteLevel)}]{Theme.Esc(text)}[/]");
        _notes.Clear();
        _current = null;
    }

    /// <summary>The running step failed with <paramref name="message"/> (an exception ended the operation).</summary>
    public void Fail(string message)
    {
        if (_current is null) _current = Strings.UiActionFailed;
        _notes.Add((CheckLevel.Fail, message));
        Failures++;
        Complete();
    }

    /// <summary>
    /// Runs <paramref name="work"/> with this view (and the log) as its report, behind a spinner. Returns the
    /// exception that ended it, or null. Expected failures (SetupException) show as the failed step.
    /// </summary>
    public static Exception? Run(IAnsiConsole c, LogReport log, StepsView view, Action<IReport> work)
    {
        var report = new TeeReport(view, log);
        try
        {
            c.Status().Spinner(Theme.SpinnerFor(c)).SpinnerStyle(Theme.Accent).Start(Theme.Esc(Strings.UiWorking), ctx =>
            {
                view._status = ctx;
                try { work(report); }
                finally { view._status = null; }
            });
            view.Complete();
            return null;
        }
        catch (Exception ex) when (ex is not ScriptEndedException)
        {
            log.Error(ex);
            view.Fail(ex.Message);
            return ex;
        }
    }
}

/// <summary>Verify's checks grouped by section, for a short table (after a change) or the full list.</summary>
internal sealed class VerifyView : IReport
{
    private readonly List<(string Title, List<(CheckLevel Level, string Text)> Checks)> _sections = new();

    public int Pass { get; private set; }
    public int Warn { get; private set; }
    public int Fail { get; private set; }

    public void Section(string title) => _sections.Add((title, new()));

    public void Check(CheckLevel level, string message)
    {
        if (_sections.Count == 0) Section("");
        _sections[^1].Checks.Add((level, message));
        switch (level)
        {
            case CheckLevel.Pass: Pass++; break;
            case CheckLevel.Warn: Warn++; break;
            default: Fail++; break;
        }
    }

    public void Info(string message) { }

    /// <summary>Runs verify behind a spinner (it takes a few seconds: tasks, Event Log, the server).</summary>
    public static VerifyView Run(IAnsiConsole c, LogReport log, string? childUserName)
    {
        var view = new VerifyView();
        c.Status().Spinner(Theme.SpinnerFor(c)).SpinnerStyle(Theme.Accent).Start(Theme.Esc(Strings.UiVerifyRunning), _ =>
            new Verify(new TeeReport(view, log), childUserName, AppContext.BaseDirectory).Run());
        log.Info(Strings.VerifySummary(view.Pass, view.Fail, view.Warn));
        return view;
    }

    private CheckLevel Worst => Fail > 0 ? CheckLevel.Fail : Warn > 0 ? CheckLevel.Warn : CheckLevel.Pass;

    /// <summary>One row per section (√ 7 · ! 1 · × 0), then only what is not OK.</summary>
    public void RenderShort(IAnsiConsole c)
    {
        var table = new Table().Border(TableBorder.Square).BorderColor(Color.Grey)
            .AddColumn(new TableColumn($"[grey]{Theme.Esc(Strings.UiVerifyArea)}[/]"))
            .AddColumn(new TableColumn($"[grey]{Theme.Esc(Strings.UiVerifyResult)}[/]").RightAligned());
        foreach (var (title, checks) in _sections)
        {
            var pass = checks.Count(x => x.Level == CheckLevel.Pass);
            var warn = checks.Count(x => x.Level == CheckLevel.Warn);
            var fail = checks.Count(x => x.Level == CheckLevel.Fail);
            var parts = new List<string> { $"{Theme.Mark(c, CheckLevel.Pass)} {pass}" };
            if (warn > 0) parts.Add($"{Theme.Mark(c, CheckLevel.Warn)} {warn}");
            if (fail > 0) parts.Add($"{Theme.Mark(c, CheckLevel.Fail)} {fail}");
            table.AddRow(new Markup(Theme.Esc(title)), new Markup(string.Join("  ", parts)));
        }
        c.MarkupLine($"[bold]{Theme.Esc(Strings.UiVerifyTitle)}[/]");
        c.Write(table);
        Problems(c);
        Verdict(c);
    }

    /// <summary>Every check, by section.</summary>
    public void RenderFull(IAnsiConsole c)
    {
        foreach (var (title, checks) in _sections)
        {
            c.MarkupLine($"[bold]{Theme.Esc(title)}[/]");
            foreach (var (level, text) in checks)
                c.MarkupLine($"  {Theme.Mark(c, level)} {(level == CheckLevel.Pass ? Theme.Esc(text) : $"[{Theme.Tint(level)}]{Theme.Esc(text)}[/]")}");
            c.WriteLine();
        }
        Verdict(c);
    }

    private void Problems(IAnsiConsole c)
    {
        foreach (var (title, checks) in _sections)
        foreach (var (level, text) in checks.Where(x => x.Level != CheckLevel.Pass))
            c.MarkupLine($"  {Theme.Mark(c, level)} [{Theme.Tint(level)}]{Theme.Esc(title)}: {Theme.Esc(text)}[/]");
    }

    private void Verdict(IAnsiConsole c)
    {
        var level = Worst;
        var text = level == CheckLevel.Pass ? Strings.UiVerifyAllOk : Strings.UiVerifyProblems(Fail, Warn);
        c.MarkupLine($"{Theme.Mark(c, level)} [{Theme.Tint(level)}]{Theme.Esc(text)}[/]");
    }
}
