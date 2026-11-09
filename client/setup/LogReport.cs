using System.Text;
using Vigil.Setup.Core;

namespace Vigil.Setup;

/// <summary>
/// Everything a run reports, with times, in %TEMP%\vigil_setup_&lt;time&gt;.log: the screen shows the parent a
/// short version, the log has the details to paste into a bug report.
/// </summary>
public sealed class LogReport : IReport, IDisposable
{
    private readonly StreamWriter? _writer;
    public string? Path { get; }

    public LogReport()
    {
        try
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vigil_setup_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _writer = new StreamWriter(Path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
            Line($"vigil_setup {AppVersion.Display} · {Environment.OSVersion} · {Environment.CommandLine}");
        }
        catch (Exception) { Path = null; }  // no log is no reason not to install
    }

    public void Section(string title) => Line($"== {title}");
    public void Check(CheckLevel level, string message) =>
        Line($"   {level switch { CheckLevel.Pass => "OK  ", CheckLevel.Warn => "WARN", _ => "FAIL" }} {message}");
    public void Info(string message) => Line($"   ..   {message}");

    public void Error(Exception ex) => Line($"!! {ex}");

    private void Line(string text)
    {
        try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}"); }
        catch (Exception) { }
    }

    public void Dispose() => _writer?.Dispose();
}

/// <summary>Reports to several places at once (the screen and the log).</summary>
public sealed class TeeReport(params IReport[] targets) : IReport
{
    public void Section(string title) { foreach (var t in targets) t.Section(title); }
    public void Check(CheckLevel level, string message) { foreach (var t in targets) t.Check(level, message); }
    public void Info(string message) { foreach (var t in targets) t.Info(message); }
}
