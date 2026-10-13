using System.Collections.Concurrent;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// One persisted worker identity. Immutable facts only: the label is
/// display metadata; the session id is derived from the id (axw- prefix)
/// and never stored independently of it.
/// </summary>
public sealed record WorkerRegistration(
    string Id,
    string Label,
    string ProjectRoot,
    string SessionId,
    DateTime CreatedUtc);

/// <summary>
/// Worker identities, persisted as JSON in the managed root
/// (<c>workers.json</c>). Deliberately separate from
/// <see cref="WorkspaceRegistry"/> (workspaces.json): workers never
/// register into, read from, or migrate workspace entries, and the
/// <c>axw-</c> session namespace is disjoint from workspace (<c>ax-</c>)
/// and legacy (<c>agent-x-main</c>) sessions by construction. Rules, all
/// tested: fail-closed validation (corrupt file throws InvalidDataException
/// and is left in place), duplicate ids rejected, session ids must equal
/// <c>axw-</c> + id, saves are atomic (temp file + move).
/// </summary>
public sealed class WorkerStore
{
    public const string FileName = "workers.json";
    public const string SessionPrefix = "axw-";

    private readonly string _managedRoot;
    private readonly Dictionary<string, WorkerRegistration> _byId = new(StringComparer.Ordinal);

    public WorkerStore(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = managedRoot;
    }

    public static string StorePath(string managedRoot) => Path.Combine(managedRoot, FileName);

    public IReadOnlyList<WorkerRegistration> List()
        => _byId.Values.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id, StringComparer.Ordinal).ToArray();

    public bool TryGet(string id, out WorkerRegistration? registration)
        => _byId.TryGetValue(id, out registration);

    /// <summary>
    /// Register a worker for an existing project root. The root must exist —
    /// a vanished project fails loudly and nothing is created on disk.
    /// Two workers MAY target one project root: they get distinct sessions
    /// and distinct processes; only one process per session is ever allowed.
    /// </summary>
    public WorkerRegistration Add(string projectRoot, string? label = null)
    {
        var canonical = AgentXWorkspace.CanonicalizeProjectRoot(projectRoot);
        if (!Directory.Exists(canonical))
        {
            throw new DirectoryNotFoundException(
                $"Project directory does not exist: {projectRoot} — refusing to create it or run a worker elsewhere.");
        }

        var resolvedLabel = string.IsNullOrWhiteSpace(label) ? string.Empty : label.Trim();
        if (resolvedLabel.Length > 64)
        {
            throw new ArgumentException("Worker labels are capped at 64 characters.", nameof(label));
        }

        string id;
        do
        {
            id = AgentXWorkspace.StableHash8(Guid.NewGuid().ToString("N"));
        }
        while (_byId.ContainsKey(id));

        var registration = new WorkerRegistration(id, resolvedLabel, canonical, SessionPrefix + id, DateTime.UtcNow);
        _byId[id] = registration;
        return registration;
    }

    public bool Remove(string id) => _byId.Remove(id);

    public static WorkerStore Load(string managedRoot)
    {
        var store = new WorkerStore(managedRoot);
        var path = StorePath(managedRoot);
        if (!File.Exists(path))
        {
            return store;
        }

        WorkerRegistration[] entries;
        try
        {
            var json = File.ReadAllText(path);
            entries = JsonSerializer.Deserialize<WorkerRegistration[]>(json)
                ?? throw new InvalidDataException($"Worker store '{path}' deserialized to null.");
        }
        catch (Exception ex) when (ex is not InvalidDataException and (JsonException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException(
                $"Worker store '{path}' is unreadable; left in place for inspection, nothing was deleted or reset.", ex);
        }

        foreach (var entry in entries)
        {
            var validated = ValidateStoredEntry(path, entry);
            if (!store._byId.TryAdd(validated.Id, validated))
            {
                throw new InvalidDataException(
                    $"Worker store '{path}' lists worker id '{validated.Id}' twice; left in place for inspection.");
            }
        }

        return store;
    }

    /// <summary>
    /// Fail-closed validation for one stored entry: blank/oversized fields,
    /// malformed ids, session ids that disagree with their id, and
    /// non-rooted project paths are all rejected — a planted workers.json
    /// cannot mint arbitrary session names or point workers at relative
    /// paths. The file is never modified here.
    /// </summary>
    public static WorkerRegistration ValidateStoredEntry(string storePath, WorkerRegistration? entry)
    {
        void Fail(string reason) => throw new InvalidDataException(
            $"Worker store '{storePath}' {reason}; left in place for inspection.");

        if (entry is null)
        {
            Fail("contains a null entry");
        }

        if (string.IsNullOrEmpty(entry!.Id)
            || entry.Id.Length != 8
            || !System.Text.RegularExpressions.Regex.IsMatch(entry.Id, "^[a-f0-9]{8}$"))
        {
            Fail($"rejects worker id '{entry.Id ?? ""}' (eight lowercase hex characters required)");
        }

        if (entry.SessionId != SessionPrefix + entry.Id)
        {
            Fail($"rejects session id '{entry.SessionId ?? ""}' disagreeing with worker id '{entry.Id}'");
        }

        if (entry.Label is { Length: > 64 })
        {
            Fail("rejects an oversized worker label");
        }

        if (string.IsNullOrWhiteSpace(entry.ProjectRoot) || !Path.IsPathRooted(entry.ProjectRoot))
        {
            Fail("rejects a blank or non-rooted project path");
        }

        string canonical;
        try
        {
            canonical = AgentXWorkspace.CanonicalizeProjectRoot(entry.ProjectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Worker store '{storePath}' contains an unusable project path '{entry.ProjectRoot}'; left in place for inspection.", ex);
        }

        return entry with { ProjectRoot = canonical, Label = entry.Label?.Trim() ?? string.Empty };
    }

    public void Save()
    {
        Directory.CreateDirectory(_managedRoot);
        var path = StorePath(_managedRoot);
        var json = JsonSerializer.Serialize(List(), new JsonSerializerOptions { WriteIndented = true });
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Process-wide single-writer claims: sessionId → owning worker. The
/// single-writer rule is per SESSION, so different worker sessions may run
/// simultaneously while one session can never back two live Pi processes.
/// Cross-process protection (two Agent X instances) is out of scope and
/// documented; this registry only covers workers inside one process.
/// </summary>
internal static class PiSessionClaims
{
    private static readonly ConcurrentDictionary<string, object> Claims = new(StringComparer.Ordinal);

    internal static bool TryClaim(string sessionId, object owner) => Claims.TryAdd(sessionId, owner);

    internal static bool IsClaimedBy(string sessionId, object owner)
        => Claims.TryGetValue(sessionId, out var existing) && ReferenceEquals(existing, owner);

    internal static void Release(string sessionId, object owner)
        => Claims.TryRemove(new KeyValuePair<string, object>(sessionId, owner));

    internal static string DescribeOwner(string sessionId)
        => Claims.TryGetValue(sessionId, out var owner)
            ? $"{owner.GetType().Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(owner)}"
            : "(none)";
}

/// <summary>
/// One independent Pi worker: its own process, session, feed, bridge,
/// cancellation state and lifecycle. The project directory is bound to the
/// session at creation; changing the directory means disposing this worker
/// and creating another — an active process is never retargeted.
///
/// Confirmed state operations: model and thinking changes are validated
/// against the connection's PROBED capabilities and the available lists,
/// then confirmed by re-reading engine state — Pi's acceptance response is
/// never treated as proof (verified on 1.1.0/1.2.0: set_thinking_level
/// silently accepts invalid levels and can clamp them).
/// </summary>
public sealed class PiWorker : IAsyncDisposable
{
    private readonly string _managedRoot;
    private readonly WorkerRegistration _reg;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _send = new(1, 1);
    private PiConversation? _conversation;
    private FeedBridge? _bridge;
    private VisibilityLevel _pendingVisibility = VisibilityLevel.Detailed;
    private CancellationTokenSource? _sendCts;
    private bool _claimed;
    private bool _disposed;
    private PiEngineState? _lastState;
    private PiVersionInfo? _version;
    private PiRpcCapabilities? _capabilities;

    internal PiWorker(string managedRoot, WorkerRegistration registration)
    {
        _managedRoot = managedRoot;
        _reg = registration;
        Feed = new ConversationFeed();
    }

    /// <summary>This worker's feed. The host renders whichever worker is selected; workers never share one.</summary>
    public ConversationFeed Feed { get; }

    public string Id => _reg.Id;
    public string Label => _reg.Label;
    public string ProjectRoot => _reg.ProjectRoot;
    public string SessionId => _reg.SessionId;
    public WorkerRegistration Registration => _reg;

    /// <summary>Persisted session history as restored by this worker's conversation (empty before first connect).</summary>
    public IReadOnlyList<ConversationMessage> History => _conversation?.History ?? Array.Empty<ConversationMessage>();

    /// <summary>Live Pi process id of this worker, or null when not running. Internal test seam.</summary>
    internal int? RunningProcessId => _conversation?.RunningProcessId;

    public bool IsConnected => _conversation?.IsConnected == true;
    public int ReconnectCount => _conversation?.ReconnectCount ?? 0;
    public PiRunReceipt? LastReceipt => _conversation?.LastReceipt;

    /// <summary>Reported Pi version (null when undeterminable or not yet connected).</summary>
    public PiVersionInfo? Version => Volatile.Read(ref _version);

    /// <summary>Probed RPC capabilities of the CURRENT connection (null before the first connect).</summary>
    public PiRpcCapabilities? Capabilities => Volatile.Read(ref _capabilities);

    /// <summary>Engine state as last confirmed by this worker (connect, or a confirmed operation).</summary>
    public PiEngineState? LastConfirmedState => Volatile.Read(ref _lastState);

    /// <summary>Bridge visibility for subsequently created detail rows. Detailed by default.</summary>
    public VisibilityLevel Visibility
    {
        get
        {
            lock (_gate)
            {
                return _bridge?.Level ?? _pendingVisibility;
            }
        }

        set
        {
            lock (_gate)
            {
                _pendingVisibility = value;
                if (_bridge is not null)
                {
                    _bridge.Level = value;
                }
            }
        }
    }

    /// <summary>Compact activity line (forwarded from the conversation).</summary>
    public event EventHandler<string>? ActivityChanged;

    /// <summary>Structured tool activity (forwarded; raw call id never rendered here).</summary>
    public event EventHandler<PiToolActivity>? ToolActivityChanged;

    /// <summary>Transport death of THIS worker's process (forwarded).</summary>
    public event EventHandler<Exception>? ConnectionLost;

    public WorkerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new WorkerSnapshot(
                Id, Label, ProjectRoot, SessionId,
                !_disposed && _conversation?.IsConnected == true,
                _conversation?.ReconnectCount ?? 0,
                _conversation?.LastReceipt,
                Volatile.Read(ref _version),
                Volatile.Read(ref _capabilities),
                Volatile.Read(ref _lastState),
                _conversation?.RunningProcessId);
        }
    }

    /// <summary>
    /// Start this worker's Pi process, validate the session identity, load
    /// restored history into the feed, and probe version + capabilities.
    /// Claims the session (single writer) on first connect; a session
    /// already claimed by another worker is refused loudly.
    /// </summary>
    public async Task<PiEngineState> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PiConversation? existing;
        lock (_gate)
        {
            existing = _conversation;
        }

        if (existing is not null)
        {
            // Idempotent when already up; after a dead process the safe path
            // is the reconnect swap (re-attaches bridge and subscribers).
            return existing.IsConnected
                ? await existing.GetStateAsync(cancellationToken).ConfigureAwait(false)
                : await ReconnectCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_claimed)
            {
                if (!PiSessionClaims.TryClaim(_reg.SessionId, this))
                {
                    throw new InvalidOperationException(
                        $"Worker session '{_reg.SessionId}' is already owned by another worker in this process — " +
                        $"one Pi process per session (owner: {PiSessionClaims.DescribeOwner(_reg.SessionId)}).");
                }

                _claimed = true;
            }

            var conversation = EnsureConversation();
            await conversation.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var state = await conversation.GetStateAsync(cancellationToken).ConfigureAwait(false);
            ValidateSession(state);
            Volatile.Write(ref _lastState, state);
            Feed.LoadHistory(conversation.History);
            Feed.SetActivity(null);
            Volatile.Write(ref _version, await conversation.DetectVersionAsync(cancellationToken).ConfigureAwait(false));
            Volatile.Write(ref _capabilities, await conversation.ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
            return state;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Attach a fresh Pi process to this worker's persisted session after a
    /// transport failure. The feed keeps everything it holds; the notice
    /// records the restore truth. Version and capabilities are re-probed (a
    /// Pi upgrade between connect and reconnect must not be assumed).
    /// </summary>
    public async Task<PiEngineState> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_conversation is null)
        {
            throw new InvalidOperationException("Worker has never connected; call ConnectAsync first.");
        }

        return await ReconnectCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PiEngineState> ReconnectCoreAsync(CancellationToken cancellationToken)
    {
        var conversation = _conversation
            ?? throw new InvalidOperationException("Worker has never connected; call ConnectAsync first.");
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await conversation.ReconnectAsync(cancellationToken).ConfigureAwait(false);
            ValidateSession(state);
            Volatile.Write(ref _lastState, state);
            Feed.AddSystemNotice(
                $"Reconnected — restored {conversation.History.Count} session message(s) " +
                $"(reconnect #{conversation.ReconnectCount}). Partial streamed text above is preserved as-is.");
            Feed.SetActivity(null);
            Volatile.Write(ref _version, await conversation.DetectVersionAsync(cancellationToken).ConfigureAwait(false));
            Volatile.Write(ref _capabilities, await conversation.ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false));
            return state;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Send a prompt on THIS worker only. One run per worker at a time (a
    /// second concurrent send throws; Pi's own queueing is unaffected).
    /// Cancellation throws after keeping partial text in the feed.
    /// </summary>
    public async Task<(string Text, PiRunReceipt Receipt)> SendAsync(
        string message,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var conversation = _conversation
            ?? throw new InvalidOperationException("Worker is not connected. Call ConnectAsync first.");
        if (!await _send.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Worker '{Id}' already has a run in progress; wait for it or cancel it first.");
        }

        var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _sendCts = sendCts;
        }

        Feed.AddUser(message);
        var seq = Feed.BeginAssistantTurn();
        try
        {
            var progress = new Progress<string>(delta =>
            {
                Feed.AppendDelta(seq, delta);
                textProgress?.Report(delta);
            });
            var (text, receipt) = await conversation
                .SendAsync(message, progress, sendCts.Token).ConfigureAwait(false);
            Feed.ReconcileStreamingText(seq, text);
            Feed.ReconcileToolResults(conversation.History);
            Feed.EndAssistantTurn(seq, receipt);
            return (text, receipt);
        }
        catch (OperationCanceledException)
        {
            Feed.EndAssistantTurn(seq, null);
            Feed.AddSystemNotice(
                "Run cancelled — partial streamed text above is kept as-is; history reconciles on the next run or reconnect.");
            throw;
        }
        catch (PiRpcCommandException ex)
        {
            Feed.EndAssistantTurn(seq, null);
            Feed.AddError($"Pi rejected the prompt: {ex.Error} — partial streamed text above is kept as-is.");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _sendCts = null;
            }

            sendCts.Dispose();
            _send.Release();
        }
    }

    /// <summary>
    /// Cancel THIS worker's in-flight run: cancel the pending wait and
    /// best-effort abort the engine run. No other worker is touched — each
    /// runs its own process.
    /// </summary>
    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? sendCts;
        PiConversation? conversation;
        lock (_gate)
        {
            sendCts = _sendCts;
            conversation = _conversation;
        }

        sendCts?.Cancel();
        if (conversation is { IsConnected: true })
        {
            try
            {
                await conversation.AbortAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is PiRpcTransportException or PiRpcCommandException)
            {
                // Best effort: the run may already be dead; the wait above
                // is unblocked either way.
            }
        }
    }

    /// <summary>Models this connection reports selectable (capability-gated).</summary>
    public Task<IReadOnlyList<PiModelOption>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        var conversation = RequireConnected();
        RequireCapability(c => c.AvailableModels, "get_available_models");
        return conversation.GetAvailableModelsAsync(cancellationToken);
    }

    /// <summary>
    /// Switch model on THIS worker: validate against the live available
    /// list, send set_model, then CONFIRM via engine state. Accepted-but-
    /// not-applied responses throw; nothing is cached or guessed.
    /// </summary>
    public async Task<PiEngineState> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var conversation = RequireConnected();
        RequireCapability(c => c.SetModel && c.AvailableModels, "set_model");
        var available = await conversation.GetAvailableModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!available.Any(m => string.Equals(m.Provider, provider, StringComparison.Ordinal)
            && string.Equals(m.Id, modelId, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"'{provider}/{modelId}' is not in this connection's available model list ({available.Count} models). " +
                "Query GetModelsAsync on this worker first.",
                nameof(modelId));
        }

        await conversation.SetModelAsync(provider, modelId, cancellationToken).ConfigureAwait(false);
        var confirmed = await conversation.GetStateAsync(cancellationToken).ConfigureAwait(false);
        RequireModelConfirmed(confirmed, provider, modelId);
        Volatile.Write(ref _lastState, confirmed);
        Feed.AddSystemNotice($"Model switched to {provider}/{modelId} (confirmed by engine state).");
        return confirmed;
    }

    /// <summary>Thinking levels the CURRENT model reports (capability-gated; model-dependent).</summary>
    public Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken = default)
    {
        var conversation = RequireConnected();
        RequireCapability(c => c.AvailableThinkingLevels, "get_available_thinking_levels");
        return conversation.GetAvailableThinkingLevelsAsync(cancellationToken);
    }

    /// <summary>
    /// Set the thinking level on THIS worker: pre-validate against the live
    /// per-model level list (Pinned Pi 1.1.0/1.2.0 silently accepts unknown
    /// levels and can clamp them — verified), send, then CONFIRM via engine
    /// state. Mismatch after send throws.
    /// </summary>
    public async Task<PiEngineState> SetThinkingLevelAsync(string level, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        var conversation = RequireConnected();
        RequireCapability(c => c.SetThinkingLevel && c.AvailableThinkingLevels, "set_thinking_level");
        var levels = await conversation.GetAvailableThinkingLevelsAsync(cancellationToken).ConfigureAwait(false);
        var canonical = RequireCanonicalLevel(levels, level);
        await conversation.SetThinkingLevelAsync(canonical, cancellationToken).ConfigureAwait(false);
        var confirmed = await conversation.GetStateAsync(cancellationToken).ConfigureAwait(false);
        RequireLevelConfirmed(confirmed, canonical);
        Volatile.Write(ref _lastState, confirmed);
        Feed.AddSystemNotice($"Thinking level set to {canonical} (confirmed by engine state).");
        return confirmed;
    }

    /// <summary>Engine state of the CURRENT process (fresh read; capability-gated on get_state).</summary>
    public Task<PiEngineState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var conversation = RequireConnected();
        RequireCapability(c => c.GetState, "get_state");
        return RefreshStateAsync(conversation, cancellationToken);
    }

    private async Task<PiEngineState> RefreshStateAsync(PiConversation conversation, CancellationToken ct)
    {
        var state = await conversation.GetStateAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _lastState, state);
        return state;
    }

    /// <summary>One of the model's listed levels (case-insensitive), or a loud ArgumentException.</summary>
    internal static string RequireCanonicalLevel(IReadOnlyList<string> levels, string requested)
    {
        var match = levels.FirstOrDefault(l => string.Equals(l, requested, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException(
                $"Thinking level '{requested}' is not supported by the current model (available: {string.Join(", ", levels)}).",
                nameof(requested));
        }

        return match;
    }

    /// <summary>Engine state must report exactly the requested model.</summary>
    internal static void RequireModelConfirmed(PiEngineState state, string provider, string modelId)
    {
        if (!string.Equals(state.Provider, provider, StringComparison.Ordinal)
            || !string.Equals(state.ModelId, modelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pi accepted the model request but engine state reports '{state.Provider}/{state.ModelId}' — " +
                $"refusing to claim '{provider}/{modelId}' is active.");
        }
    }

    /// <summary>Engine state must report exactly the requested level.</summary>
    internal static void RequireLevelConfirmed(PiEngineState state, string level)
    {
        if (!string.Equals(state.ThinkingLevel, level, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Pi accepted the thinking-level request but engine state reports '{state.ThinkingLevel}' — " +
                $"refusing to claim '{level}' is active.");
        }
    }

    private void ValidateSession(PiEngineState state)
    {
        if (state.SessionId is not null
            && !string.Equals(state.SessionId, _reg.SessionId, StringComparison.Ordinal))
        {
            throw new PiRpcTransportException(
                $"Worker '{Id}' connected to unexpected Pi session '{state.SessionId}' (expected '{_reg.SessionId}') — " +
                "refusing to show a foreign history.");
        }
    }

    private PiConversation RequireConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _conversation
            ?? throw new InvalidOperationException("Worker is not connected. Call ConnectAsync first.");
    }

    private void RequireCapability(Func<PiRpcCapabilities, bool> feature, string command)
    {
        var caps = Volatile.Read(ref _capabilities)
            ?? throw new InvalidOperationException("Worker capabilities are unknown; connect first.");
        if (!feature(caps))
        {
            var version = Volatile.Read(ref _version);
            throw new NotSupportedException(
                $"This Pi connection does not support '{command}' (probed; Pi version: {(version?.ToString() ?? "unknown")}). " +
                "The compatibility boundary refuses to assume the command exists.");
        }
    }

    private PiConversation EnsureConversation()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_conversation is not null)
            {
                return _conversation;
            }

            // Existence check happens BEFORE any directory creation: a
            // moved/deleted project must fail loudly, never be recreated.
            if (!Directory.Exists(_reg.ProjectRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Project directory for worker '{Id}' is missing (moved or deleted?): {_reg.ProjectRoot} — " +
                    "refusing to recreate it or run Pi elsewhere. Remove this worker and create a new one.");
            }

            var sessionDirectory = Path.Combine(_managedRoot, "sessions");
            Directory.CreateDirectory(sessionDirectory);
            var options = PiRpcOptions.ForSession(sessionDirectory, _reg.SessionId, _reg.ProjectRoot);
            var conversation = new PiConversation(() => new PiRpcClient(options));
            conversation.ActivityChanged += OnActivity;
            conversation.ToolActivityChanged += OnToolActivity;
            conversation.ConnectionLost += OnConnectionLost;
            var bridge = new FeedBridge(Feed, _pendingVisibility);
            conversation.AttachBridge(bridge);
            _conversation = conversation;
            _bridge = bridge;
            return conversation;
        }
    }

    private void OnActivity(object? sender, string activity)
    {
        Feed.SetActivity(string.IsNullOrWhiteSpace(activity) ? null : activity);
        ActivityChanged?.Invoke(this, activity);
    }

    private void OnToolActivity(object? sender, PiToolActivity activity)
    {
        if (activity.IsStart)
        {
            Feed.AddToolStart(activity.ToolName, activity.Summary, activity.Path, activity.IsMutating, activity.CallId);
        }
        else
        {
            Feed.AddToolEnd(activity.ToolName, activity.IsError, activity.CallId);
        }

        ToolActivityChanged?.Invoke(this, activity);
    }

    private void OnConnectionLost(object? sender, Exception error)
    {
        Feed.AddError($"Connection to Pi lost: {error.Message}");
        Feed.SetActivity(null);
        ConnectionLost?.Invoke(this, error);
    }

    public async ValueTask DisposeAsync()
    {
        PiConversation? conversation;
        FeedBridge? bridge;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            conversation = _conversation;
            bridge = _bridge;
            _conversation = null;
            _bridge = null;
            _sendCts?.Cancel();
        }

        if (bridge is not null)
        {
            bridge.Detach();
            bridge.Dispose();
        }

        if (conversation is not null)
        {
            conversation.ActivityChanged -= OnActivity;
            conversation.ToolActivityChanged -= OnToolActivity;
            conversation.ConnectionLost -= OnConnectionLost;
            await conversation.DisposeAsync().ConfigureAwait(false);
        }

        if (_claimed)
        {
            PiSessionClaims.Release(_reg.SessionId, this);
            _claimed = false;
        }
    }
}

/// <summary>Point-in-time description of one worker for status binding.</summary>
public sealed record WorkerSnapshot(
    string Id,
    string Label,
    string ProjectRoot,
    string SessionId,
    bool IsConnected,
    int ReconnectCount,
    PiRunReceipt? LastReceipt,
    PiVersionInfo? Version,
    PiRpcCapabilities? Capabilities,
    PiEngineState? LastConfirmedState,
    int? ProcessId);

/// <summary>
/// Owns the set of workers and the selection. UI-independent: Agent TWO
/// renders <see cref="Selected"/>'s feed and swaps the bound feed on
/// <see cref="SelectionChanged"/> — selection NEVER touches processes, so
/// background workers keep running untouched. Creation/removal persist to
/// workers.json; nothing here migrates, deletes or rewrites workspace
/// registrations or legacy sessions.
/// </summary>
public sealed class PiWorkerManager : IAsyncDisposable
{
    private readonly string _managedRoot;
    private readonly WorkerStore _store;
    private readonly Dictionary<string, PiWorker> _workers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private PiWorker? _selected;
    private bool _disposed;

    /// <summary>Managed root for sessions and workers.json; defaults to the real one.</summary>
    public PiWorkerManager(string? managedRoot = null)
    {
        _managedRoot = managedRoot ?? PiSessionOptions.Default().ManagedRoot;
        _store = WorkerStore.Load(_managedRoot);
        foreach (var registration in _store.List())
        {
            _workers[registration.Id] = new PiWorker(_managedRoot, registration);
        }

        // Reload convenience: the first stored worker starts selected (view
        // pointer only — no process is touched).
        _selected = _store.List().Select(r => _workers[r.Id]).FirstOrDefault();
    }

    /// <summary>Worker created (persisted before the event fires).</summary>
    public event EventHandler<PiWorker>? WorkerAdded;

    /// <summary>Worker id removed (disposed and unpersisted before the event fires).</summary>
    public event EventHandler<string>? WorkerRemoved;

    /// <summary>Selection changed. The host rebinds its view; no process action occurs.</summary>
    public event EventHandler<PiWorker?>? SelectionChanged;

    public IReadOnlyList<PiWorker> Workers
    {
        get
        {
            lock (_gate)
            {
                return _store.List()
                    .Select(r => _workers[r.Id])
                    .ToArray();
            }
        }
    }

    public PiWorker? Selected
    {
        get
        {
            lock (_gate)
            {
                return _selected;
            }
        }
    }

    public static string StorePath(string managedRoot) => WorkerStore.StorePath(managedRoot);

    /// <summary>
    /// Create and persist a worker for an existing project. An empty label
    /// becomes "Worker N" (display metadata only). The first worker becomes
    /// the selection (convenience, not a process action).
    /// </summary>
    public PiWorker CreateWorker(string projectRoot, string? label = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PiWorker worker;
        bool becameSelection;
        lock (_gate)
        {
            var effectiveLabel = string.IsNullOrWhiteSpace(label) ? $"Worker {_workers.Count + 1}" : label.Trim();
            var registration = _store.Add(projectRoot, effectiveLabel);
            _store.Save();
            worker = new PiWorker(_managedRoot, registration);
            _workers[registration.Id] = worker;
            becameSelection = _selected is null;
            if (becameSelection)
            {
                _selected = worker;
            }
        }

        WorkerAdded?.Invoke(this, worker);
        if (becameSelection)
        {
            SelectionChanged?.Invoke(this, worker);
        }

        return worker;
    }

    /// <summary>Select a worker by id (no process side effects).</summary>
    public PiWorker SelectWorker(string id)
    {
        PiWorker worker;
        PiWorker? previous;
        lock (_gate)
        {
            if (!_workers.TryGetValue(id, out var found))
            {
                throw new ArgumentException($"Unknown worker id '{id}'.", nameof(id));
            }

            worker = found;
            previous = _selected;
            if (!ReferenceEquals(previous, worker))
            {
                _selected = worker;
            }
        }

        if (!ReferenceEquals(previous, worker))
        {
            SelectionChanged?.Invoke(this, worker);
        }

        return worker;
    }

    /// <summary>Dispose a worker (killing its Pi process) and unpersist its identity.</summary>
    public async Task RemoveWorkerAsync(string id)
    {
        PiWorker worker;
        bool wasSelected;
        lock (_gate)
        {
            if (!_workers.TryGetValue(id, out var found))
            {
                throw new ArgumentException($"Unknown worker id '{id}'.", nameof(id));
            }

            worker = found;
            _workers.Remove(id);
            _store.Remove(id);
            _store.Save();
            wasSelected = ReferenceEquals(_selected, worker);
            if (wasSelected)
            {
                _selected = null;
            }
        }

        await worker.DisposeAsync().ConfigureAwait(false);
        WorkerRemoved?.Invoke(this, id);

        if (wasSelected)
        {
            PiWorker? next;
            lock (_gate)
            {
                next = _selected = _store.List()
                    .Select(r => _workers[r.Id])
                    .FirstOrDefault();
            }

            SelectionChanged?.Invoke(this, next); // null when the last worker was removed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        PiWorker[] all;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            all = _workers.Values.ToArray();
            _workers.Clear();
            _selected = null;
        }

        // Reverse creation order; every worker disposes its own process, so
        // no orphan Pi processes remain.
        for (var i = all.Length - 1; i >= 0; i--)
        {
            await all[i].DisposeAsync().ConfigureAwait(false);
        }
    }
}
