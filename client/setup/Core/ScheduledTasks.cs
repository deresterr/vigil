using System.Globalization;
using System.Xml;

namespace Vigil.Setup.Core;

/// <summary>A registered task's definition (its XML) and last run.</summary>
public sealed class TaskInfo
{
    private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly XmlDocument _xml;
    private readonly XmlNamespaceManager _ns;

    public int? LastResult { get; }
    public string LastRunTime { get; }

    internal TaskInfo(XmlDocument xml, int? lastResult, string lastRunTime)
    {
        _xml = xml;
        _ns = new XmlNamespaceManager(xml.NameTable);
        _ns.AddNamespace("t", Ns);
        LastResult = lastResult;
        LastRunTime = lastRunTime;
    }

    private string? Text(string xpath) => _xml.SelectSingleNode(xpath, _ns)?.InnerText;

    public string UserId => Text("//t:Principals/t:Principal/t:UserId") ?? "";
    public string RunLevel => Text("//t:Principals/t:Principal/t:RunLevel") ?? "";
    public bool Enabled => Text("//t:Settings/t:Enabled") != "false";
    public bool HasBootTrigger => _xml.SelectSingleNode("//t:Triggers/t:BootTrigger", _ns) is not null;
    public bool RepeatsEvery(string isoInterval) =>
        _xml.SelectNodes("//t:Triggers/*/t:Repetition/t:Interval", _ns)!.Cast<XmlNode>().Any(n => n.InnerText == isoInterval);
    public string Command => Text("//t:Actions/t:Exec/t:Command") ?? "";
    public string Arguments => Text("//t:Actions/t:Exec/t:Arguments") ?? "";

    /// <summary>The task has not run yet (SCHED_S_TASK_HAS_NOT_RUN): right after install.</summary>
    public const int HasNotRun = 0x41303;
}

/// <summary>
/// Task Scheduler through schtasks.exe, reading only what does not depend on the display language: the
/// task XML, exit codes and CSV columns by position. No COM: it does not survive trimming.
/// </summary>
public static class ScheduledTasks
{
    // schtasks /Query /V /FO CSV columns: HostName, TaskName, Next Run Time, Status, Logon Mode,
    // Last Run Time, Last Result, ...
    private const int ColLastRunTime = 5;
    private const int ColLastResult = 6;

    /// <returns>null when there is no such task.</returns>
    public static TaskInfo? Get(string name)
    {
        var (rc, xmlText) = NativeTool.Run("schtasks.exe", "/Query", "/TN", name, "/XML");
        if (rc != 0) return null;
        var xml = new XmlDocument();
        xml.LoadXml(xmlText.Trim());

        int? lastResult = null;
        var lastRun = "";
        var (rcV, csv) = NativeTool.Run("schtasks.exe", "/Query", "/TN", name, "/V", "/FO", "CSV", "/NH");
        var line = csv.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith('"'));
        if (rcV == 0 && line is not null)
        {
            var fields = NativeTool.SplitCsv(line);
            if (fields.Count > ColLastResult)
            {
                lastRun = fields[ColLastRunTime];
                if (int.TryParse(fields[ColLastResult], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                    lastResult = r;
            }
        }
        return new TaskInfo(xml, lastResult, lastRun);
    }

    public static bool Exists(string name) => NativeTool.Run("schtasks.exe", "/Query", "/TN", name).ExitCode == 0;

    /// <summary>Names (without the folder) of all tasks this account can see.</summary>
    public static List<string> AllNames()
    {
        var (_, csv) = NativeTool.Run("schtasks.exe", "/Query", "/FO", "CSV", "/NH");
        return csv.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('"'))
            .Select(l => NativeTool.SplitCsv(l)[0])
            .Select(path => path[(path.LastIndexOf('\\') + 1)..])
            .ToList();
    }
}
