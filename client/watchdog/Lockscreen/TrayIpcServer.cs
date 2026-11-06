using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Serwer named pipe \\.\pipe\VigilTray — komunikacja z vigil_tray.exe.
///
/// Tray → Watchdog: {"type":"pause_toggle"}
/// Watchdog → Tray: {"type":"state_update","paused":false,"can_pause":true,"minutes_remaining":45}
///
/// Watchdog wywołuje SendStateUpdate() po każdym heartbeat żeby tray miał aktualną ikonę.
/// </summary>
public sealed class TrayIpcServer : IDisposable
{
    private const string PipeName = "VigilTray";

    private readonly ILogger _log;
    private readonly Func<Task> _onPauseToggle;

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public TrayIpcServer(ILogger log, Func<Task> onPauseToggle)
    {
        _log = log;
        _onPauseToggle = onPauseToggle;
    }

    /// <summary>Uruchamia pętlę akceptowania połączeń (restart po rozłączeniu).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await AcceptAndServeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "TrayIpc: pipe error, recreating");
                DisposeCurrentPipe();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task AcceptAndServeAsync(CancellationToken ct)
    {
        // PipeSecurity spójna z VigilUI: SYSTEM full + Administrators full + Authenticated Users RW.
        // Tray nie wymaga peer verification (pause_toggle jest low-privilege), ale DACL
        // ogranicza dostęp do pipe do uprawnionych userów.
        var security = BuildPipeSecurity();
        _pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);

        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        _log.LogInformation("TrayIpc: tray connected");

        var utf8 = new UTF8Encoding(false);
        _reader = new StreamReader(_pipe, utf8, true, 4096, leaveOpen: true);
        _writer = new StreamWriter(_pipe, utf8, 1024, leaveOpen: true) { AutoFlush = true };

        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var t)
                        && t.GetString() == "pause_toggle")
                    {
                        _log.LogInformation("TrayIpc: pause toggle requested by the tray");
                        await _onPauseToggle().ConfigureAwait(false);
                    }
                }
                catch (JsonException) { }
            }
            _log.LogInformation("TrayIpc: tray disconnected");
        }
        finally
        {
            // The single pipe instance must be closed before the next accept, otherwise creating it
            // again fails with "all pipe instances are busy".
            DisposeCurrentPipe();
        }
    }

    public async Task SendStateUpdateAsync(bool paused, bool canPause, int minutesRemaining)
    {
        if (_writer is null) return;

        var msg = JsonSerializer.Serialize(new
        {
            type             = "state_update",
            paused,
            can_pause        = canPause,
            minutes_remaining= minutesRemaining,
        });

        // Timeout zamiast nieograniczonego WaitAsync — przy shutdown service semaphore nie
        // powinien blokować w nieskończoność.
        if (!await _writeLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            return;
        try
        {
            await _writer.WriteLineAsync(msg).ConfigureAwait(false);
        }
        catch { /* tray rozłączony */ }
        finally
        {
            _writeLock.Release();
        }
    }

    private void DisposeCurrentPipe()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        // Authenticated Users — child user musi móc się połączyć z tray procesu
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            AccessControlType.Allow));
        return sec;
    }

    public void Dispose()
    {
        DisposeCurrentPipe();
        _writeLock.Dispose();
    }
}
