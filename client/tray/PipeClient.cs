using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

/// <summary>
/// Named pipe client for vigil_watchdog (\\.\pipe\VigilTray). JSON, one message per line.
/// Tray -> Watchdog: {"type":"pause_toggle"}
/// Watchdog -> Tray: {"type":"state_update","paused":false,"can_pause":true,"minutes_remaining":45}
///
/// The connection is treated as disposable: any end of it (EOF, error, no state for SilenceLimit,
/// failed write) tears it down and reconnects. The watchdog restarts (recovery after a kill,
/// update-config, reinstall) and the tray must follow without being restarted itself.
/// </summary>
public sealed class PipeClient : IDisposable
{
    private const string PipeName = "VigilTray";
    // The watchdog pushes state on every tick (<= 60 s); longer silence means a dead connection.
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _connCts;
    private string? _lastError;

    public event Action<TrayStateUpdate>? StateReceived;
    public event Action? Disconnected;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                lock (_gate) { _pipe = pipe; _connCts = connCts; }
                await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
                lock (_gate) { _writer = new StreamWriter(pipe, Utf8NoBom, 1024, leaveOpen: true) { AutoFlush = true }; }
                _lastError = null;
                TrayLog.Write("connected");
                await ReadLoopAsync(pipe, connCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Log once per distinct error, not every retry while the watchdog is down.
                var err = ex.GetType().Name + ": " + ex.Message;
                if (err != _lastError) TrayLog.Write("connection error: " + err);
                _lastError = err;
            }
            finally
            {
                Cleanup();
                Disconnected?.Invoke();
            }
            try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void SendPauseToggle()
    {
        StreamWriter? writer;
        CancellationTokenSource? conn;
        lock (_gate) { writer = _writer; conn = _connCts; }
        if (writer is null)
        {
            TrayLog.Write("pause click: not connected to the watchdog");
            return;
        }
        try
        {
            writer.WriteLine("{\"type\":\"pause_toggle\"}");
            TrayLog.Write("pause click sent");
        }
        catch (Exception ex)
        {
            TrayLog.Write("pause click failed, reconnecting: " + ex.Message);
            try { conn?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, Utf8NoBom, true, 4096, leaveOpen: true);
        while (true)
        {
            string? line;
            using (var silence = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                silence.CancelAfter(SilenceLimit);
                try
                {
                    line = await reader.ReadLineAsync(silence.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // ct = this connection (failed write or app exit); otherwise the silence timer fired.
                    TrayLog.Write(ct.IsCancellationRequested ? "connection closed (failed write or exit)"
                                                             : "no state from the watchdog for 90 s, reconnecting");
                    return;
                }
            }
            if (line is null)
            {
                TrayLog.Write("watchdog closed the connection");
                return;
            }
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "state_update")
                {
                    var update = JsonSerializer.Deserialize<TrayStateUpdate>(line, _jsonOpts);
                    if (update is not null) StateReceived?.Invoke(update);
                }
            }
            catch (JsonException) { /* malformed line: ignore */ }
        }
    }

    private void Cleanup()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { /* broken pipe on flush */ }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _pipe = null;
            _connCts = null;
        }
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
