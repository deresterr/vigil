using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

/// <summary>
/// Klient named pipe do komunikacji z vigil_watchdog (\\.\pipe\VigilTray).
///
/// Protokół: JSON line-delimited (każda wiadomość = jedna linia).
/// Tray → Watchdog: {"type":"pause_toggle"}
/// Watchdog → Tray: {"type":"state_update","paused":false,"can_pause":true,"minutes_remaining":45}
/// </summary>
public sealed class PipeClient : IDisposable
{
    private const string PipeName = "VigilTray";

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public event Action<TrayStateUpdate>? StateReceived;

    /// <summary>
    /// Próbuje nawiązać połączenie w pętli (watchdog może jeszcze startować).
    /// Po połączeniu słucha na state updates.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(3000, ct).ConfigureAwait(false);

                _reader = new StreamReader(_pipe, Encoding.UTF8);
                _writer = new StreamWriter(_pipe, Encoding.UTF8) { AutoFlush = true };

                await ReadLoopAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Pipe zerwany (np. watchdog zrestartowany) — retry po chwili
                Cleanup();
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
        }
    }

    public void SendPauseToggle()
    {
        try
        {
            _writer?.WriteLine("{\"type\":\"pause_toggle\"}");
        }
        catch { /* jeśli pipe nie działa, watchdog niedostępny */ }
    }

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _reader is not null)
        {
            var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) break;  // pipe zamknięty przez watchdoga

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var t)
                    && t.GetString() == "state_update")
                {
                    var update = JsonSerializer.Deserialize<TrayStateUpdate>(line, _jsonOpts);
                    if (update is not null)
                        StateReceived?.Invoke(update);
                }
            }
            catch (JsonException) { /* malformed — ignoruj */ }
        }
    }

    private void Cleanup()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _pipe?.Dispose();
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose() => Cleanup();
}

public sealed class TrayStateUpdate
{
    public string Type { get; init; } = "";
    public bool Paused { get; init; }
    public bool CanPause { get; init; }
    public int MinutesRemaining { get; init; }
}
