using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Shared;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog.Guardian;

/// <summary>
/// Reports what the guardian (`vigil_setup.exe guardian`, #65) repaired to the parent's journal (#36, detection).
///
/// The guardian (SYSTEM task, every 5 min) writes one Event Log entry per finding, source
/// "Vigil Guardian", IDs 7001–7006 (Warning = repaired, Error = repair failed). Every minute this
/// service reads the last day of them and sends each once as tamper_event guardian_repaired. After a
/// restart (often the guardian's own repair) the same records go out again; the server dedupes them
/// by record id + time. An offline server just means they go out on a later round.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GuardianReporter : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FirstRun = TimeSpan.FromSeconds(30);

    // Contract with the guardian and server GUARDIAN_CHECKS: VigilLayout.GuardianChecks (7001–7006).
    private static readonly IReadOnlyDictionary<int, string> Checks = VigilLayout.GuardianChecks;

    private const string Query =
        "*[System[Provider[@Name='" + VigilLayout.GuardianEventSource + "'] and (EventID>=7001 and EventID<=7006) " +
        "and TimeCreated[timediff(@SystemTime) <= 86400000]]]";

    private const byte LevelWarning = 3;

    private readonly HeartbeatClient _client;
    private readonly ILogger<GuardianReporter> _logger;
    private readonly HashSet<long> _sent = new();

    public GuardianReporter(HeartbeatClient client, ILogger<GuardianReporter> logger)
    {
        _client = client;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstRun, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReportNewAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Guardian report round failed");
            }

            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ReportNewAsync(CancellationToken ct)
    {
        foreach (var finding in ReadFindings())
        {
            if (_sent.Contains(finding.RecordId)) continue;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await _client.SendGuardianRepairAsync(finding.Request, cts.Token).ConfigureAwait(false);
                _sent.Add(finding.RecordId);
                _logger.LogInformation("Reported guardian finding {Check} (record {Record})",
                    finding.Request.Check, finding.RecordId);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Server down: the next round tries again.
                _logger.LogDebug(ex, "Guardian finding {Record} not reported yet", finding.RecordId);
                return;
            }
        }
    }

    private List<(long RecordId, GuardianRepairRequest Request)> ReadFindings()
    {
        var result = new List<(long, GuardianRepairRequest)>();
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, Query));
        }
        catch (EventLogException ex)
        {
            _logger.LogDebug(ex, "Event Log query failed");
            return result;
        }
        using (reader)
        {
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.RecordId is not { } id || record.TimeCreated is not { } created
                        || !Checks.TryGetValue(record.Id, out var check))
                        continue;
                    result.Add((id, new GuardianRepairRequest
                    {
                        Check = check,
                        Repaired = record.Level == LevelWarning,
                        RecordId = id,
                        OccurredUtc = created.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                    }));
                }
            }
        }
        return result;
    }
}
