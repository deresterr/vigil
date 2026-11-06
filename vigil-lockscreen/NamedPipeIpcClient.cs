using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vigil.Lockscreen;

/// <summary>
/// Pipe client łączący się z vigil_watchdog. Bidirectional pipe, JSON line-delimited.
///
/// Architektura:
/// - Connect na startup; jeśli failed → application można uruchomić w standalone-test mode
/// - Background reader task ciągle czyta liny i emituje przez OnMessage
/// - SendAsync zsynchronizowane przez SemaphoreSlim
/// - SendTotpAsync używa TaskCompletionSource do korelacji request → response
///   (na pipe nie ma natywnego request/response — IpcTotpResponse to "kolejny message",
///   więc trzymamy pending TCS w polu i resolving go z reader thread)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeIpcClient : IDisposable
{
    public const string PipeName = "VigilUI";

    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;

    private TaskCompletionSource<IpcTotpResponse>? _pendingTotp;
    private readonly object _totpLock = new();

    private bool _disposed;

    /// <summary>Wywoływany dla każdego received message (poza TOTP response który idzie do TCS).</summary>
    public event Action<object>? OnMessage;

    /// <summary>Wywoływany gdy pipe się rozerwie (broken connection). UI może pokazać błąd.</summary>
    public event Action? OnDisconnected;

    public bool IsConnected => _pipe.IsConnected;

    public NamedPipeIpcClient()
    {
        _pipe = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
    }

    public async Task ConnectAsync(int timeoutMs, CancellationToken ct)
    {
        await _pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), _readerCts.Token);
    }

    public async Task SendAsync(object message, CancellationToken ct)
    {
        var json = IpcSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Wyślij TOTP attempt i czekaj na response. Jeden TOTP w locie naraz —
    /// wyrzuca jeśli już jest pending.
    /// </summary>
    public async Task<IpcTotpResponse> SendTotpAsync(string code, int minutes, CancellationToken ct)
    {
        TaskCompletionSource<IpcTotpResponse> tcs;
        lock (_totpLock)
        {
            if (_pendingTotp is not null && !_pendingTotp.Task.IsCompleted)
            {
                throw new InvalidOperationException("Już jest TOTP attempt w locie.");
            }
            _pendingTotp = new TaskCompletionSource<IpcTotpResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _pendingTotp;
        }

        await SendAsync(new IpcTotpAttempt { Code = code, Minutes = minutes }, ct).ConfigureAwait(false);

        // Czekaj na response z timeout (server może być zajęty, ale 30s wystarczy)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("TOTP response timeout (30s)");
        }
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[4096];
        var lineBuf = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                int read;
                try
                {
                    read = await _pipe.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }
                if (read == 0) break;  // EOF

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(lineBuf.GetBuffer(), 0, (int)lineBuf.Length);
                        lineBuf.SetLength(0);
                        Dispatch(line);
                    }
                    else
                    {
                        lineBuf.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch
        {
            // Cicho — UI tak czy inaczej zobaczy disconnect przez event
        }
        finally
        {
            OnDisconnected?.Invoke();
        }
    }

    private void Dispatch(string line)
    {
        var msg = IpcSerializer.TryDeserialize(line);
        if (msg is null) return;

        // TOTP response → TCS, reszta → event
        if (msg is IpcTotpResponse totpResp)
        {
            lock (_totpLock)
            {
                _pendingTotp?.TrySetResult(totpResp);
            }
            return;
        }

        OnMessage?.Invoke(msg);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _readerCts?.Cancel();
        try { _readerTask?.Wait(1000); } catch { }
        _readerCts?.Dispose();
        _pipe.Dispose();
        _writeLock.Dispose();
        _disposed = true;
    }
}
