using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Vigil.Ui;

/// <summary>
/// The pipe to vigil_watchdog: bidirectional, one JSON message per line.
/// - connected at startup (no watchdog: vigil_ui quits)
/// - a background reader raises OnMessage for each line
/// - SendAsync serialized by a SemaphoreSlim
/// - SendTotpAsync pairs a request with its IpcTotpResponse through a pending TaskCompletionSource that the
///   reader completes (the pipe itself has no request/response)
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

    /// <summary>Raised for every message except a TOTP response (that completes the pending request).</summary>
    public event Action<object>? OnMessage;

    /// <summary>Raised when the pipe breaks.</summary>
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
    /// Sends a TOTP code and waits for the answer. One at a time: throws while another is pending.
    /// </summary>
    public async Task<IpcTotpResponse> SendTotpAsync(string code, int minutes, CancellationToken ct)
    {
        TaskCompletionSource<IpcTotpResponse> tcs;
        lock (_totpLock)
        {
            if (_pendingTotp is not null && !_pendingTotp.Task.IsCompleted)
            {
                throw new InvalidOperationException("A TOTP attempt is already pending");
            }
            _pendingTotp = new TaskCompletionSource<IpcTotpResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = _pendingTotp;
        }

        await SendAsync(new IpcTotpAttempt { Code = code, Minutes = minutes }, ct).ConfigureAwait(false);

        // 30 s for the answer
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
            // Quiet: OnDisconnected tells the UI anyway
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

        // A TOTP response completes the pending request; everything else is an event
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
