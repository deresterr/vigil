using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Vigil.Watchdog.Ui;

/// <summary>
/// Named pipe server \\.\pipe\VigilUI for vigil_ui (one client). JSON, one message per line
/// (shared/IpcMessages.cs). One reader task dispatches each line to MessageReceived; writes are
/// serialized with a semaphore. The client is verified by PID (WaitForClientAsync).
///
/// Pipe DACL: SYSTEM + Administrators full, Authenticated Users read/write — the PID check, not the
/// DACL, keeps other programs out.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeIpcServer : IDisposable
{
    public const string PipeName = "VigilUI";

    private readonly ILogger<NamedPipeIpcServer> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    /// <summary>Raised for every message received; the subscriber decides what to do.</summary>
    public event Func<object, Task>? MessageReceived;

    /// <summary>True while vigil_ui is connected.</summary>
    public bool IsConnected => _pipe?.IsConnected ?? false;

    public NamedPipeIpcServer(ILogger<NamedPipeIpcServer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates the pipe and waits for vigil_ui. The reader starts right after the connect.
    ///
    /// The client must be the process we just spawned: <paramref name="expectedPid"/> completes with
    /// its PID once CreateProcessAsUser returns. A copy of vigil_ui.exe started by the child, or any
    /// other program, is rejected even if it races ours to the pipe.
    /// </summary>
    public async Task WaitForClientAsync(Task<uint> expectedPid, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _pipe = NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: BuildPipeSecurity());

        _logger.LogInformation("Pipe \\\\.\\pipe\\{Name} waiting for vigil_ui", PipeName);
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
        var pid = await expectedPid.WaitAsync(ct).ConfigureAwait(false);

        if (!Win32.GetNamedPipeClientProcessId(_pipe.SafePipeHandle.DangerousGetHandle(), out var clientPid)
            || clientPid != pid)
        {
            _logger.LogWarning("Pipe client pid={Client} is not the vigil_ui we spawned (pid={Expected}), rejecting",
                clientPid, pid);
            try { _pipe.Disconnect(); } catch { /* ignore */ }
            _pipe.Dispose();
            _pipe = null;
            throw new UnauthorizedAccessException("Pipe peer verification failed");
        }

        _logger.LogInformation("vigil_ui connected (pid={Pid})", pid);
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), _readerCts.Token);
    }

    public async Task SendAsync(object message, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            throw new InvalidOperationException("The pipe is not connected");
        }

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

    public async Task DisconnectAsync()
    {
        if (_readerCts is not null)
        {
            _readerCts.Cancel();
            try
            {
                if (_readerTask is not null)
                    await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected */ }
            _readerCts.Dispose();
            _readerCts = null;
            _readerTask = null;
        }

        if (_pipe is not null)
        {
            try
            {
                if (_pipe.IsConnected) _pipe.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Pipe disconnect failed; ignored");
            }
            _pipe.Dispose();
            _pipe = null;
        }
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        if (_pipe is null) return;

        var buffer = new byte[4096];
        var lineBuf = new MemoryStream();
        const int maxLineBytes = 64 * 1024;  // guard against unbounded growth without a newline

        try
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                int read;
                try
                {
                    read = await _pipe.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    // The client went away (usually vigil_ui exiting).
                    _logger.LogInformation("Pipe broken: {Message}", ex.Message);
                    break;
                }
                if (read == 0) break;  // EOF

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        var line = Encoding.UTF8.GetString(lineBuf.GetBuffer(), 0, (int)lineBuf.Length);
                        lineBuf.SetLength(0);
                        await DispatchAsync(line).ConfigureAwait(false);
                    }
                    else
                    {
                        if (lineBuf.Length >= maxLineBytes)
                        {
                            // More than 64 KB without a newline: a bug or an attack. Drop the buffer.
                            _logger.LogWarning("Pipe: a line exceeded {Limit} bytes; buffer dropped", maxLineBytes);
                            lineBuf.SetLength(0);
                        }
                        lineBuf.WriteByte(buffer[i]);
                    }
                }
            }
        }
        // Only our own cancellation is expected; any other OperationCanceledException (e.g. a timeout
        // inside a handler) must be logged, not silently end the loop (#31).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reader loop crashed");
        }
    }

    private async Task DispatchAsync(string line)
    {
        var msg = IpcSerializer.TryDeserialize(line);
        if (msg is null)
        {
            _logger.LogWarning("Pipe: unknown message (line truncated): {Line}",
                line.Length > 200 ? line[..200] + "..." : line);
            return;
        }

        var handler = MessageReceived;
        if (handler is null) return;

        try
        {
            await handler(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MessageReceived handler crashed for {Type}", msg.GetType().Name);
        }
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var sec = new PipeSecurity();
        // SYSTEM full
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        // Administrators full
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        // Authenticated Users RW: the child's vigil_ui; the PID check keeps other programs out
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize,
            System.Security.AccessControl.AccessControlType.Allow));
        return sec;
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisconnectAsync().GetAwaiter().GetResult();
        _writeLock.Dispose();
        _disposed = true;
    }
}
