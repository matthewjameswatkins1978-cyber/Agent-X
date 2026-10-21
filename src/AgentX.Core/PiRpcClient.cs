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
    private static readonly string[] RequiredExtensions =
    [
        Path.Combine("matthew-way-studio", "extensions", "index.ts"),
        Path.Combine("rabbit-pi-compactor", "src", "index.ts"),
        Path.Combine("Speedy-Keeper", "pi-extension", "keeper-memory.ts"),
    ];

    private static string BaseArguments => BuildBaseArguments(GetPiAgentDirectory());

    internal static string BuildBaseArguments(string piAgentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(piAgentDirectory);
        var ownerRoot = Path.Combine(
            piAgentDirectory, "git", "github.com", "matthewjameswatkins1978-cyber");
        var extensions = RequiredExtensions
            .Select(relativePath => $"--extension \"{Path.Combine(ownerRoot, relativePath)}\"");
        return $"--mode rpc --no-extensions --no-skills {string.Join(" ", extensions)}";
    }

    private static string GetPiAgentDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent")
            : configured;
    }

    /// <summary>
    /// Default launch: separate Pi process and ephemeral session.
    /// Pi extension discovery and skills stay disabled; only the three approved
    /// entrypoints load explicitly. Provider authentication flows from the
    /// existing environment and config; Agent X never copies or passes credentials.
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

    /// <summary>
    /// Persistent launch for an explicit session location: Pi-native session
    /// in <paramref name=\"sessionDirectory\"/> resumed by stable
    /// <paramref name=\"sessionId\"/>, Pi process rooted in
    /// <paramref name=\"workingDirectory\"/>. Used by AgentXWorkspace;
    /// the legacy PiSessionOptions path above is unchanged.
    /// </summary>
    public static PiRpcOptions ForSession(
        string sessionDirectory,
        string sessionId,
        string workingDirectory,
        string? executablePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionDirectory);
        PiSessionOptions.ValidateSessionId(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        Directory.CreateDirectory(sessionDirectory);
        Directory.CreateDirectory(workingDirectory);
        return new PiRpcOptions(
            executablePath ?? @"D:\AgentTools\bin\pi.cmd",
            $"{BaseArguments} --session-dir \"{sessionDirectory}\" --session-id {sessionId}",
            workingDirectory);
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

    /// <summary>Live Pi process id, or null. Internal test seam for process-death scenarios.</summary>
    internal int? RunningProcessId => _process is { HasExited: false } ? _process.Id : null;

    /// <summary>Executable this client launches (used by the compatibility boundary for version detection).</summary>
    public string ExecutablePath => _options.ExecutablePath;

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

    /// <summary>
    /// Attach an already-running transport instead of spawning a Pi
    /// process — the same connection-layer seam FeedBridge.AttachConnection
    /// exposes: event forwarding, fault propagation and the reader loop are
    /// wired exactly as ConnectAsync wires them, so every command path
    /// (including the bounded post-settle stats poll) behaves identically
    /// to the live pipe. Internal test seam for deterministic loopback
    /// tests; production hosts keep using ConnectAsync.
    /// </summary>
    internal void AttachConnection(PiRpcConnection connection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(connection);
        if (_connection is not null)
        {
            throw new InvalidOperationException("A Pi transport is already attached.");
        }

        _connection = connection;
        _connection.EventReceived += (_, args) => EventReceived?.Invoke(this, args);
        _connection.TransportFailed += (_, error) => TransportFailed?.Invoke(this, error);
        _connection.Start(); // Idempotent: loopback tests may already have started it.
    }

    /// <summary>
    /// Raw get_state response for the host's approved record boundary
    /// (EventReceived never sees it); the CALLER owns disposal. GetStateAsync
    /// delegates to this, so recorded and returned state always derive from
    /// the same observed document. On command failure the typed exception
    /// throws AFTER the failed document is disposed — a rejected poll never
    /// leaks (same policy as SendStatsPollAsync).
    /// </summary>
    public async Task<JsonDocument> GetStateDocumentAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        var response = await connection.SendCommandAsync("get_state", null, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowOnCommandError(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Inspect current engine state (model, streaming flags, session).</summary>
    public async Task<PiEngineState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await GetStateDocumentAsync(cancellationToken).ConfigureAwait(false);
        return PiEngineState.FromResponse(response);
    }

    /// <summary>
    /// Poll session totals (get_session_stats): Pi-reported tool calls,
    /// tokens, cost and context for THIS session — the only honest
    /// token/cost source observed. Returns the raw correlated response for
    /// the host's approved record boundary (EventReceived never sees it);
    /// the CALLER owns disposal. Not capability-probed (baseline protocol,
    /// same policy as prompt/abort): an unsupported command surfaces as
    /// PiRpcCommandException at use time — command-level rejection IS the
    /// probe (see PiRpcCompat), never a hang.
    /// </summary>
    public Task<JsonDocument> GetSessionStatsAsync(CancellationToken cancellationToken = default)
        => SendStatsPollAsync(RequireConnection(), cancellationToken);

    /// <summary>
    /// Connection-level stats poll: exposed for deterministic pipe tests.
    /// On command failure the typed exception throws AFTER the failed
    /// response document is disposed — a rejected poll never leaks.
    /// </summary>
    internal static async Task<JsonDocument> SendStatsPollAsync(
        PiRpcConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var response = await connection.SendCommandAsync("get_session_stats", null, cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowOnCommandError(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every model Pi 1.1.0 reports as selectable (get_available_models).
    /// Malformed entries are skipped, never guessed. The list reflects THIS
    /// process's configured providers — query it per connection; do not cache
    /// it across processes.
    /// </summary>
    public async Task<IReadOnlyList<PiModelOption>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync("get_available_models", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        var models = new List<PiModelOption>();
        if (response.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("models", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("provider", out var prov) || prov.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelId = id.GetString();
                var provider = prov.GetString();
                if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(provider))
                {
                    continue;
                }

                var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString() ?? modelId
                    : modelId;
                models.Add(new PiModelOption(provider, modelId, name));
            }
        }

        return models;
    }

    /// <summary>
    /// Switch model (set_model). Returns only what Pi accepted — a caller
    /// that needs PROOF must re-read get_state and compare: an accepted
    /// request is not evidence the selection took effect.
    /// </summary>
    public async Task SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync(
            "set_model",
            new Dictionary<string, object?> { ["provider"] = provider, ["modelId"] = modelId },
            cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
    }

    /// <summary>Thinking levels the CURRENT model reports (get_available_thinking_levels).</summary>
    public async Task<IReadOnlyList<string>> GetAvailableThinkingLevelsAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync("get_available_thinking_levels", null, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        var levels = new List<string>();
        if (response.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("levels", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } level)
                {
                    levels.Add(level);
                }
            }
        }

        return levels;
    }

    /// <summary>
    /// Set the thinking level (set_thinking_level). Like SetModelAsync: the
    /// acceptance response is not proof — confirm via get_state.
    /// </summary>
    public async Task SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync(
            "set_thinking_level",
            new Dictionary<string, object?> { ["level"] = level },
            cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
    }

    /// <summary>
    /// Send a prompt and return the raw acceptance response for the host's
    /// approved record boundary (EventReceived never sees it); the CALLER
    /// owns disposal. The returned disposition (started/queued/handled) only
    /// means the prompt was accepted. Never confuse it with completion.
    /// Disposition extraction is shared via <see cref="DispositionOf"/> so
    /// the recorded and returned acceptance always agree. Same dispose-on-
    /// failure policy as the other document-returning polls.
    /// </summary>
    public async Task<JsonDocument> SendPromptDocumentAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        var connection = RequireConnection();
        var response = await connection
            .SendCommandAsync("prompt", new Dictionary<string, object?> { ["message"] = message }, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ThrowOnCommandError(response);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal static string DispositionOf(JsonDocument response)
        => response.RootElement.TryGetProperty("data", out var data)
            && data.TryGetProperty("disposition", out var disposition)
            ? disposition.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// Send a prompt. The returned disposition (started/queued/handled) only
    /// means the prompt was accepted. Never confuse it with completion.
    /// Subscribe to events (or use PromptAndWaitAsync) and wait for
    /// agent_settled to detect completion.
    /// </summary>
    public async Task<string> SendPromptAsync(string message, CancellationToken cancellationToken = default)
    {
        using var response = await SendPromptDocumentAsync(message, cancellationToken).ConfigureAwait(false);
        return DispositionOf(response);
    }

    /// <summary>
    /// Send a prompt, stream text deltas to progress, and resolve with the full
    /// streamed text once agent_settled arrives. Event subscription happens
    /// before the prompt is sent so a fast completion cannot be missed.
    /// <paramref name="onPromptAccepted"/> (optional) observes the live
    /// acceptance document synchronously after send; see the body for the
    /// ownership rules.
    /// </summary>
    public async Task<(string Text, bool Aborted)> PromptAndWaitAsync(
        string message,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default,
        Action<JsonDocument>? onPromptAccepted = null)
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
            // The acceptance document stays alive for the hook, then is
            // disposed here: the hook must decode synchronously and must
            // neither dispose nor retain the document. A throwing hook aborts
            // the wait loudly — acceptance recording is host telemetry, and
            // a broken host hook must not pass as a settled run.
            using var acceptance = await SendPromptDocumentAsync(message, cancellationToken).ConfigureAwait(false);
            onPromptAccepted?.Invoke(acceptance);
            var disposition = DispositionOf(acceptance);
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

    /// <summary>
    /// Execute a shell command immediately (no LLM run) and persist a
    /// BashExecutionMessage in the session. Used by hosts to verify tool
    /// execution paths and to seed disposable verification sessions without
    /// inference cost. Output beyond the truncation limit stays in the
    /// session; check <see cref=\"PiBashResult.Truncated\"/>.
    /// </summary>
    public async Task<PiBashResult> RunBashAsync(string command, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);
        var connection = RequireConnection();
        using var response = await connection.SendCommandAsync(
            "bash", new Dictionary<string, object?> { ["command"] = command }, cancellationToken).ConfigureAwait(false);
        ThrowOnCommandError(response);
        var data = response.RootElement.TryGetProperty("data", out var d) ? d : (JsonElement?)null;
        string? Str(string name)
            => data is { } obj && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new PiBashResult(
            Str("output") ?? string.Empty,
            data is { } obj2 && obj2.TryGetProperty("exitCode", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : null,
            data is { } obj3 && obj3.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True,
            data is { } obj4 && obj4.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True);
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
        if (_connection is null)
        {
            throw new PiRpcTransportException("Pi client is not connected. Call ConnectAsync first.");
        }

        // Liveness is checked only when a process exists: ConnectAsync always
        // creates connection+process together, so every live host path is
        // unchanged. A connection attached without a process is the
        // connection-layer seam (AttachConnection) used by loopback tests.
        if (_process is { HasExited: true })
        {
            throw new PiRpcTransportException($"Pi has exited (code {_process.ExitCode}). {StderrTail}");
        }

        return _connection;
    }

    /// <summary>Send an arbitrary RPC command and return the raw response (compat probes).</summary>
    internal Task<JsonDocument> SendRawAsync(string command, Dictionary<string, object?>? payload, CancellationToken cancellationToken)
        => RequireConnection().SendCommandAsync(command, payload, cancellationToken);

    /// <summary>True when a response carries success != false (missing field counts as success, as with ThrowOnCommandError).</summary>
    internal static bool IsCommandSuccess(JsonDocument response)
        => !response.RootElement.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.False;

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
