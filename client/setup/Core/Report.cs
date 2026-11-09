namespace Vigil.Setup.Core;

public enum CheckLevel { Pass, Warn, Fail }

/// <summary>
/// Where the core reports progress and results. The core never touches the console: the parameter mode
/// prints these lines, the wizard (D22) renders them its own way.
/// </summary>
public interface IReport
{
    void Section(string title);
    void Check(CheckLevel level, string message);
    /// <summary>Information that is not a check (does not count).</summary>
    void Info(string message);
}

/// <summary>Counts checks and passes everything on.</summary>
public sealed class CountingReport : IReport
{
    private readonly IReport _inner;
    public int Pass { get; private set; }
    public int Warn { get; private set; }
    public int Fail { get; private set; }

    public CountingReport(IReport inner) => _inner = inner;

    public void Section(string title) => _inner.Section(title);
    public void Info(string message) => _inner.Info(message);

    public void Check(CheckLevel level, string message)
    {
        switch (level)
        {
            case CheckLevel.Pass: Pass++; break;
            case CheckLevel.Warn: Warn++; break;
            default: Fail++; break;
        }
        _inner.Check(level, message);
    }

    public void Check(bool ok, string message, CheckLevel otherwise = CheckLevel.Fail) =>
        Check(ok ? CheckLevel.Pass : otherwise, message);
}
