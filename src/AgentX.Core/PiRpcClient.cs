using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>How to launch the Pi engine subprocess.</summary>
public sealed record PiRpcOptions(
    string ExecutablePath,
    string Arguments,
    string? WorkingDirectory = null)
{
    private const string BaseArguments = "--mode rpc --no-extensions --no-skills";

    /// <summary>
    /// Default launch: separate Pi process, ephemeral session, extensions
    /// and skills disabled. Provider authentication flows from the existing
    /// environment and config; Agent X never copies or passes credentials.
    /// </summary>
    public static PiRpcOptions Default(string? workingDirectory = null) => new(
        @"D:\AgentTools\bin\pi.cmd",
        $"{BaseArguments} --no-session",
        workingDirectory);

    /// <summary>
    /// Persistent launch: Pi-native session in an Agent X-managed directory,
    /// resumed by stable session id across GUI restarts.
    /// </summary>
    public static PiRpcOptions Persistent(PiSessionOptions session, string? executablePath = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.EnsureDirectories();
        return new PiRpcOptions(
            executablePath ?? @"D:\AgentTools\bin\pi.cmd",
            $"{BaseArguments} {session.ToArguments()}",
            session.WorkingDirectory);
    }
}

/// <summary>
/// Owns the Pi RPC child process and exposes typed commands plus streamed
/// session events. The GUI observes events and stays independent of the agent
/// process: all I/O is asynchronous and cancellation never blocks the caller.
/// </summary>
public sealed class PiRpcClient : IAsyncDisposable
{
    private readonly PiRpcOptions _options;
    private Process? _process;
    private PiRpcConnection? _connection;
    private readonly StringBuilder _stderrTail = new();
    private readonly Lock _stderrGate = new();
    private bool _disposed;

    public PiRpcClient(PiRpcOptions? options = null)
    {
        _options = options ?? PiRpcOptions.Default();
    }

    public event EventHandler<PiRpcEventArgs>? EventReceived;

    /// <summary>Forwarded when the RPC transport dies (stdout EOF or stream error).</summary>
    public event EventHandler<Exception>? TransportFailed;

    public bool IsRunning => _process is { HasExited: false };

    public string? StderrTail
    {
        get
        {
            lock (_stderrGate)
            {
                return _stderrTail.Length == 0 ? null : _stderrTail.ToString();
            }
        }
    }

    /// <summary>Start Pi and begin reading its stdout continuously.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return;
        }

        var (fileName, arguments) = ResolveLaunch(_options.ExecutablePath, _options.Arguments);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(_options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = _options.WorkingDirectory;
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new PiRpcTransportException($"Failed to start Pi: {startInfo.FileName}");
            }
        }
        catch (Exception ex) when (ex is not PiRpcTransportException)
        {
            process.Dispose();
            throw new PiRpcTransportException($"Failed to start Pi: {ex.Message}", ex);
        }

        _process = process;
        _connection = new PiRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, leaveOpen: true);
        _connection.EventReceived += (_, args) => EventReceived?.Invoke(this, args);
        _connection.TransportFailed += (_, error) => TransportFailed?.Invoke(this, error);
        _connection.Start();
        process.Exited += (_, _) => _connection?.FailPendingCommands(
            new PiRpcTransportException($"Pi exited with code {process.ExitCode}. {StderrTail}"));
        _ = Task.Run(() => DrainStderrAsync(process, CancellationToken.None));

        // Confirm the engine answers before reporting a successful connection.
        await GetStateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inspect current engine state (model, streaming flags, session).</summary>
    public async Task<PiEngineState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        return PiEngineState.FromResponse(response);
    }

    /// <summary>
    /// Send a prompt. The returned disposition (started/queued/handled) only
    /// means the prompt was accepted — never confuse it with completion.
    /// Subscribe to events (or use PromptAndWaitAsync) and wait for
    /// agent_settled to detect completion.
    /// </summary>
    public async Task<string> SendPromptAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        var connection = RequireConnection();
        using var response = await connection
            .SendCommandAsync("prompt", new Dictionary<string, object?> { ["message"] = message }, cancellationToken)
            .ConfigureAwait(false);
        ThrowOnCommandError(response);
        return response.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("disposition", out var disposition)
            ? disposition.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>
    /// Send a prompt, stream text deltas to progress, and resolve with the full
    /// streamed text once agent_settled arrives. Event subscription happens
    /// before the prompt is sent so a fast completion cannot be missed.
    /// </summary>
    public async Task<(string Text, bool Aborted)> PromptAndWaitAsync(
        string message,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        var connection = RequireConnection();
        var text = new StringBuilder();
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => settled.TrySetCanceled(cancellationToken));

        void Handler(object? sender, PiRpcEventArgs args)
        {
            var delta = PiRpcFraming.GetTextDelta(args.Record);
            if (delta is not null)
            {
                lock (text)
                {
                    text.Append(delta);
                }

                textProgress?.Report(delta);
            }

            if (PiRpcFraming.IsSettledEvent(args.Record, out var aborted))
            {
                settled.TrySetResult(aborted);
            }
        }

        void Fault(object? sender, Exception error)
        {
            settled.TrySetException(error);
        }

        EventReceived += Handler;
        connection.TransportFailed += Fault;
        try
        {
            var disposition = await SendPromptAsync(message, cancellationToken).ConfigureAwait(false);
            if (disposition == "handled")
            {
                // No run will follow this prompt; do not wait for agent_settled.
                return (text.ToString(), false);
            }

            var aborted = await settled.Task.ConfigureAwait(false);
            return (text.ToString(), aborted);
        }
        finally
        {
            EventReceived -= Handler;
            connection.TransportFailed -= Fault;
        }
    }

    /// <summary>Abort the current run and wait until the session is idle.</summary>
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync("abort", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
    }

    /// <summary>All messages in the conversation (AgentMessage union; tolerate unknown roles).</summary>
    public async Task<JsonDocument> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        var response = await connection.SendCommandAsync("get_messages", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        return response;
    }

    /// <summary>Text of the last assistant message, or null when there is none.</summary>
    public async Task<string?> GetLastAssistantTextAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync("get_last_assistant_text", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        return response.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
    }

    public Task<bool> WaitForSettledAsync(CancellationToken cancellationToken = default)
        => RequireConnection().WaitForSettledAsync(cancellationToken);

    internal static (string FileName, string Arguments) ResolveLaunch(string executablePath, string arguments)
    {
        // Batch shims (pi.cmd) need cmd.exe when started without a shell.
        if (executablePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            || executablePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            return ("cmd.exe", $"/c \"\"{executablePath}\" {arguments}\"");
        }

        return (executablePath, arguments);
    }

    private PiRpcConnection RequireConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is null || _process is null)
        {
            throw new PiRpcTransportException("Pi client is not connected. Call ConnectAsync first.");
        }

        if (_process.HasExited)
        {
            throw new PiRpcTransportException($"Pi has exited (code {_process.ExitCode}). {StderrTail}");
        }

        return _connection;
    }

    private static void ThrowOnCommandError(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.False)
        {
            var command = response.RootElement.TryGetProperty("command", out var c) ? c.GetString() ?? "unknown" : "unknown";
            var error = response.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unknown error";
            throw new PiRpcCommandException(command, error);
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new char[4 * 1024];
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await process.StandardError.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                lock (_stderrGate)
                {
                    _stderrTail.Append(buffer, 0, read);
                    const int max = 8 * 1024;
                    if (_stderrTail.Length > max)
                    {
                        _stderrTail.Remove(0, _stderrTail.Length - max);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Diagnostics must never break the protocol reader.
        }
    }

    /// <summary>Request orderly shutdown (close stdin), then kill if it lingers.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var process = _process;
        var connection = _connection;
        _process = null;
        _connection = null;

        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed; fall through to the grace wait.
                    }

                    if (!process.WaitForExit(5_000))
                    {
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch (Exception)
                        {
                            // Process may have exited concurrently.
                        }
                    }
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
