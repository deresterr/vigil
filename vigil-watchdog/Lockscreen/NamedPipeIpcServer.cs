using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Named pipe server (\\.\pipe\VigilUI) — przyjmuje połączenie z vigil_lockscreen.exe,
/// czyta JSON line-delimited messages, wysyła w drugą stronę.
///
/// Concurrency model: jeden background reader task czyta linia-po-linii, każda linia
/// dispatch'owana na event MessageReceived. Pisanie zsynchronizowane przez SemaphoreSlim
/// (NamedPipeServerStream nie jest thread-safe dla równoczesnych operacji).
///
/// Jeden lockscreen client per watchdog. Jeśli przyjdzie drugi connect — odrzucamy.
///
/// Pipe DACL: SYSTEM full + Authenticated Users RW. Bardziej restrictive (per session SID)
/// w przyszłości — child user też jest Authenticated User więc to działa.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeIpcServer : IDisposable
{
    public const string PipeName = "VigilUI";

    private readonly ILogger<NamedPipeIpcServer> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private string? _expectedClientPath;

    private NamedPipeServerStream? _pipe;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    /// <summary>Wywoływany dla każdego received message. Subscriber decyduje co robić.</summary>
    public event Func<object, Task>? MessageReceived;

    /// <summary>True kiedy klient (lockscreen) jest podpięty.</summary>
    public bool IsConnected => _pipe?.IsConnected ?? false;

    public NamedPipeIpcServer(ILogger<NamedPipeIpcServer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Tworzy pipe i czeka na klienta. Zwraca po tym jak lockscreen się połączy.
    /// Niblokujący względem dispatch — reader startuje od razu po connect.
    ///
    /// <paramref name="expectedClientPath"/> — jeśli niepusta, weryfikujemy po accept
    /// że proces klienta to dokładnie ten plik exe (chroni przed fake-client attack:
    /// child user mógłby się normalnie podłączyć i wysłać shutdown_request, ale jego
    /// proces nie jest vigil_lockscreen.exe z naszego install dir).
    /// </summary>
    public async Task WaitForClientAsync(CancellationToken ct, string? expectedClientPath = null)
    {
        await DisconnectAsync().ConfigureAwait(false);  // cleanup poprzedniego connection jeśli był

        _expectedClientPath = expectedClientPath;

        // PipeSecurity dla DACL — trzeba ustawić zanim WaitForConnection.
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

        _logger.LogInformation("Pipe \\\\.\\pipe\\{Name} czeka na połączenie...", PipeName);
        await _pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

        // Weryfikacja peer process — chroni przed fake lockscreen-impersonator z child user.
        if (!VerifyPeerProcess(_pipe))
        {
            _logger.LogWarning("Pipe peer process nie pasuje do expected — odrzucam connection");
            try { _pipe.Disconnect(); } catch { /* ignore */ }
            _pipe.Dispose();
            _pipe = null;
            throw new UnauthorizedAccessException("Pipe peer verification failed");
        }

        _logger.LogInformation("Lockscreen podłączony do pipe (weryfikacja peer OK).");

        // Start reader task
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), _readerCts.Token);
    }

    /// <summary>
    /// Weryfikuje że proces po drugiej stronie pipe to dokładnie _expectedClientPath.
    /// Używa GetNamedPipeClientProcessId + Process.MainModule.FileName.
    /// Jeśli _expectedClientPath == null lub puste, zwraca true (verification disabled).
    /// </summary>
    private bool VerifyPeerProcess(NamedPipeServerStream pipe)
    {
        if (string.IsNullOrEmpty(_expectedClientPath)) return true;

        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid))
        {
            _logger.LogWarning("GetNamedPipeClientProcessId failed, errno={Err}",
                Marshal.GetLastWin32Error());
            return false;
        }

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            // MainModule może rzucić Win32Exception jeśli proces 32-bit a my 64-bit lub vice versa,
            // ale CreateProcessAsUser tworzy lockscreen z tego samego pakietu więc bitness matches.
            var modulePath = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(modulePath))
            {
                _logger.LogWarning("Peer process pid={Pid} ma puste MainModule.FileName", pid);
                return false;
            }
            // Compare case-insensitive — Windows paths
            if (!string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(_expectedClientPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Peer process path mismatch: expected={Expected}, actual={Actual}",
                    _expectedClientPath, modulePath);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VerifyPeerProcess crashed dla pid={Pid}", pid);
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr Pipe, out uint ClientProcessId);

    public async Task SendAsync(object message, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            throw new InvalidOperationException("Pipe nie podłączony — nie można wysłać.");
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
                _logger.LogDebug(ex, "Disconnect pipe — ignoruję");
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
        const int maxLineBytes = 64 * 1024;  // 64 KB guard — zapobiega OOM przy braku '\n'

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
                    // Pipe broken — klient zniknął. Zwykle przy zamknięciu lockscreen.
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
                            // Klient wysłał >64 KB bez newline — najpewniej bug lub atak. Resetuj bufor.
                            _logger.LogWarning("Pipe: linia przekroczyła {Limit} bajtów — porzucam bufor", maxLineBytes);
                            lineBuf.SetLength(0);
                        }
                        lineBuf.WriteByte(buffer[i]);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* expected */ }
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
            _logger.LogWarning("Pipe: nieznany message (line truncated): {Line}",
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
            _logger.LogError(ex, "MessageReceived handler crashed dla {Type}", msg.GetType().Name);
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
        // Administrators full (administracja, rzadko używane)
        sec.AddAccessRule(new PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        // Authenticated Users RW — child user też tu wpada, lockscreen się dostanie
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
