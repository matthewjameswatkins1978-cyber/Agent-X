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
/// <c>axw-</c> + id, saves are atomic (temp file + move) AND serialized
/// cross-process: workers.json.lock brackets re-read + revision check +
/// write, so a stale or busy save throws WorkerRegistryConflictException
/// instead of silently overwriting another instance's changes.
/// </summary>
public sealed class WorkerStore
{
    public const string FileName = "workers.json";
    public const string SessionPrefix = "axw-";

    private readonly string _managedRoot;
    private readonly Dictionary<string, WorkerRegistration> _byId = new(StringComparer.Ordinal);

    /// <summary>
    /// SHA-256 of the exact registry bytes this store loaded or last wrote
    /// (null = no file yet). Compared under the cross-process registry lock
    /// on every save, so a stale in-memory copy can never silently
    /// overwrite another instance's changes.
    /// </summary>
    private string? _revision;

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
        byte[] bytes;
        try
        {
            // One read serves BOTH parsing and the revision baseline: the
            // hash and the entries always describe the same bytes, even if
            // another instance writes mid-load (the move is atomic, so the
            // read sees whole-file state either way).
            bytes = File.ReadAllBytes(path);
            using var reader = new StreamReader(
                new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var json = reader.ReadToEnd();
            entries = JsonSerializer.Deserialize<WorkerRegistration[]>(json)
                ?? throw new InvalidDataException($"Worker store '{path}' deserialized to null.");
        }
        catch (Exception ex) when (ex is not InvalidDataException and (JsonException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException(
                $"Worker store '{path}' is unreadable; left in place for inspection, nothing was deleted or reset.", ex);
        }

        store._revision = WorkerRegistryLock.ComputeRevision(bytes);

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

        // Cross-process serialization: the registry lock brackets
        // [re-read → revision check → atomic write], so a stale in-memory
        // copy fails loudly instead of overwriting changes another
        // instance persisted. Lock files are never deleted just because
        // they are unlocked.
        using var registryLock = WorkerRegistryLock.Acquire(path);
        var currentRevision = WorkerRegistryLock.ReadRevision(path);
        if (currentRevision != _revision)
        {
            throw new WorkerRegistryConflictException(
                WorkerRegistryConflictKind.StaleRevision,
                path,
                $"workers.json changed on disk since this manager loaded it (another Agent X instance added or removed workers). " +
                "Nothing was written and the in-memory registry stays at its loaded state (the caller's change is rolled back). " +
                "Recovery: reopen the worker registry — recreate PiWorkerManager after confirming which live worker " +
                "references need migrating — then retry the operation.");
        }

        var temp = path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // A failed save leaves the previous registry file untouched (the
            // move is atomic) and never strands a temp file behind.
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }

        _revision = WorkerRegistryLock.ComputeRevision(System.Text.Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Re-add a previously removed registration — the rollback half of a
    /// failed save, so in-memory store, collections and disk stay in step.
    /// </summary>
    internal void Restore(WorkerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _byId[registration.Id] = registration;
    }
}

/// <summary>
/// Process-wide single-writer claims: sessionId → owning worker. The
/// single-writer rule is per SESSION, so different worker sessions may run
/// simultaneously while one session can never back two live Pi processes.
/// This registry covers workers inside ONE process; the same rule extends
/// across Agent X instances via CrossProcessSessionLock — an OS-held file
/// handle acquired alongside this claim before any Pi process launches.
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

    /// <summary>Bounded wait for DisposeAsync's lifecycle gate; tests shorten it to force a deterministic timeout.</summary>
    internal TimeSpan DisposeGateTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Test seam: awaited inside the lifecycle gate during ConnectAsync so a blocked connect can overlap disposal.</summary>
    internal Func<CancellationToken, Task>? LifecycleGateHookForTests;
    private PiConversation? _conversation;
    private FeedBridge? _bridge;
    private VisibilityLevel _pendingVisibility = VisibilityLevel.Detailed;
    private CancellationTokenSource? _sendCts;
    private bool _claimed;
    private FileStream? _sessionLock;
    private bool _identityVerified;
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

    /// <summary>True only after identity validation and capability probes succeeded on the current connection.</summary>
    internal bool IdentityVerified => Volatile.Read(ref _identityVerified);

    /// <summary>True while this worker holds the cross-process session lock (test seam).</summary>
    internal bool SessionLockHeld
    {
        get
        {
            lock (_gate)
            {
                return _sessionLock is not null;
            }
        }
    }
    /// Test seam: simulate a connection whose validation/probe previously
    /// failed (unverified, capabilities and confirmed state cleared), so
    /// regression tests can prove Send/model actions refuse and Connect
    /// re-verifies instead of taking a bypassing shortcut.
    /// </summary>
    internal void InvalidateVerificationForTests()
    {
        lock (_gate)
        {
            _identityVerified = false;
            _capabilities = null;
            _version = null;
            _lastState = null;
        }
    }

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
                _conversation?.RunningProcessId,
                Volatile.Read(ref _identityVerified));
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
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check INSIDE the gate: a DisposeAsync that completed while
            // this call waited must prevent any process from being started
            // or published after successful disposal.
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Fast path ONLY for a previously verified, still-running
            // connection: identity and capability probes were accepted before
            // this connection was ever published as usable. An unverified or
            // previously-failed connection is quarantined below, so
            // ConnectAsync can never bypass a failed validation.
            if (_conversation is not null)
            {
                if (Volatile.Read(ref _identityVerified) && _conversation.IsConnected)
                {
                    try
                    {
                        var live = await _conversation.GetStateAsync(cancellationToken).ConfigureAwait(false);
                        ValidateSession(live); // identity re-checked on every connect call
                        return live;
                    }
                    catch
                    {
                        await QuarantineUnusableConnectionAsync().ConfigureAwait(false);
                        throw;
                    }
                }

                await QuarantineUnusableConnectionAsync().ConfigureAwait(false);
            }

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

            if (_sessionLock is null)
            {
                try
                {
                    // Cross-process ownership BEFORE any Pi launch: another
                    // Agent X instance holding this session must be refused
                    // before EnsureConversation can create a process.
                    _sessionLock = CrossProcessSessionLock.Acquire(_managedRoot, _reg.SessionId);
                }
                catch
                {
                    // Roll back the in-process claim taken moments ago (no
                    // process was started, so nothing can be alive): a
                    // refusal leaves this worker exactly as it was.
                    if (_claimed)
                    {
                        PiSessionClaims.Release(_reg.SessionId, this);
                        _claimed = false;
                    }

                    throw;
                }
            }

            var conversation = EnsureConversation();

            // Test seam: parks the connect inside the lifecycle gate (after
            // the session claim) so a regression can overlap it with a
            // DisposeAsync contending for the same gate.
            var hook = LifecycleGateHookForTests;
            if (hook is not null)
            {
                await hook(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await conversation.ConnectAsync(cancellationToken).ConfigureAwait(false);
                var state = await conversation.GetStateAsync(cancellationToken).ConfigureAwait(false);
                ValidateSession(state);
                var version = await conversation.DetectVersionAsync(cancellationToken).ConfigureAwait(false);
                var capabilities = await conversation.ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
                PublishUsable(state, version, capabilities);
                Feed.LoadHistory(conversation.History);
                Feed.SetActivity(null);
                return state;
            }
            catch
            {
                // Never publish a half-open connection: the process behind a
                // failed identity check or failed probe is retired (or kept
                // under ownership for a retry if retirement itself fails).
                await QuarantineUnusableConnectionAsync().ConfigureAwait(false);
                throw;
            }
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
    /// Pi upgrade between connect and reconnect must not be assumed), and
    /// every earlier claim is invalidated BEFORE the swap so a failed
    /// replacement can never leave stale capabilities or confirmed state.
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
            // Re-check INSIDE the gate: disposal may have completed while
            // this reconnect waited — never publish after a completed
            // disposal.
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Invalidate first: a failed swap or failed re-probe must leave
            // this worker unverified (Send/model actions refuse) rather than
            // claiming capabilities of a process that no longer exists.
            Volatile.Write(ref _identityVerified, false);
            Volatile.Write(ref _capabilities, null);
            Volatile.Write(ref _version, null);
            Volatile.Write(ref _lastState, null);

            var state = await conversation.ReconnectAsync(cancellationToken).ConfigureAwait(false);
            ValidateSession(state);
            var version = await conversation.DetectVersionAsync(cancellationToken).ConfigureAwait(false);
            var capabilities = await conversation.ProbeCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            PublishUsable(state, version, capabilities);
            Feed.AddSystemNotice(
                $"Reconnected — restored {conversation.History.Count} session message(s) " +
                $"(reconnect #{conversation.ReconnectCount}). Partial streamed text above is preserved as-is.");
            Feed.SetActivity(null);
            return state;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Mark this connection usable: identity verified and probes accepted.</summary>
    private void PublishUsable(PiEngineState state, PiVersionInfo? version, PiRpcCapabilities capabilities)
    {
        lock (_gate)
        {
            _lastState = state;
            _version = version;
            _capabilities = capabilities;
            _identityVerified = true;
        }
    }

    /// <summary>
    /// Retire an unverified or failed connection so no later call can treat
    /// it as usable: detach the bridge, dispose the process, and clear
    /// version/capabilities/confirmed state. Never throws and never loses
    /// ownership: if retirement itself fails, the conversation reference is
    /// KEPT (claim intact) so a retry finishes the job — a preserved handle,
    /// not a silent orphan.
    /// </summary>
    private async Task QuarantineUnusableConnectionAsync()
    {
        PiConversation? conversation;
        FeedBridge? bridge;
        lock (_gate)
        {
            Volatile.Write(ref _identityVerified, false);
            Volatile.Write(ref _capabilities, null);
            Volatile.Write(ref _version, null);
            Volatile.Write(ref _lastState, null);
            conversation = _conversation;
            bridge = _bridge;
        }

        if (bridge is not null)
        {
            try
            {
                bridge.Detach();
                bridge.Dispose();
            }
            catch (Exception)
            {
                // Bridge teardown must never mask the connection failure.
            }

            lock (_gate)
            {
                if (ReferenceEquals(_bridge, bridge))
                {
                    _bridge = null;
                }
            }
        }

        if (conversation is not null)
        {
            conversation.ActivityChanged -= OnActivity;
            conversation.ToolActivityChanged -= OnToolActivity;
            conversation.ConnectionLost -= OnConnectionLost;
            try
            {
                await conversation.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    if (ReferenceEquals(_conversation, conversation))
                    {
                        _conversation = null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Retirement failed: keep the reference and the claim so the
                // process stays owned and the next call retries disposal.
                Feed.AddError($"Could not retire the Pi process after a failed connection — keeping ownership for a retry: {ex.Message}");
            }
        }

        Feed.SetActivity(null);
    }

    /// <summary>
    /// Send a prompt on THIS worker only. One run per worker at a time (a
    /// second concurrent send throws; Pi's own queueing is unaffected).
    /// Cancellation throws after keeping partial text in the feed.
    /// </summary>
    public async Task<(string Text, PiRunReceipt Receipt)> SendAsync(
        string message,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default,
        Action? onPromptAccepted = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        var conversation = RequireUsable();
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
        // Tracked live seq: tool-interleaved deltas split new timeline rows
        // in Core, so every site below must use the latest row, never the
        // turn-start seq captured here. A torn read degrades benignly
        // (guarded no-ops on frozen rows), never loss or duplication.
        var liveSeq = Feed.BeginAssistantTurn();
        try
        {
            var progress = new Progress<string>(delta =>
            {
                liveSeq = Feed.AppendDelta(liveSeq, delta);
                textProgress?.Report(delta);
            });
            var (text, receipt) = await conversation
                .SendAsync(message, progress, sendCts.Token, onPromptAccepted).ConfigureAwait(false);
            Feed.ReconcileStreamingText(liveSeq, text);
            Feed.ReconcileToolResults(conversation.History);
            Feed.EndAssistantTurn(liveSeq, receipt);
            return (text, receipt);
        }
        catch (OperationCanceledException)
        {
            Feed.EndAssistantTurn(liveSeq, null);
            Feed.AddCancelNotice(
                "Run cancelled — partial streamed text above is kept as-is; history reconciles on the next run or reconnect.");
            throw;
        }
        catch (PiRpcCommandException ex)
        {
            Feed.EndAssistantTurn(liveSeq, null);
            Feed.AddError($"Pi rejected the prompt: {ex.Error} — partial streamed text above is kept as-is.");
            throw;
        }
        catch (Exception ex)
        {
            // Transport death or any other unexpected failure closes the
            // visible run as failed/unverified — an assistant turn is never
            // left apparently active, and no receipt is ever fabricated.
            Feed.EndAssistantTurn(liveSeq, null);
            Feed.AddError($"Run failed before a receipt: {ex.Message}");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                // CancelAsync cancels under this same lock, so the shared
                // CTS can never be disposed while another thread uses it.
                if (ReferenceEquals(_sendCts, sendCts))
                {
                    _sendCts = null;
                }

                sendCts.Dispose();
            }

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
        PiConversation? conversation;
        lock (_gate)
        {
            // Cancel under the same lock SendAsync uses to retire the CTS: a
            // completion racing this call can never dispose the source out
            // from under it, so cancellation cannot throw ObjectDisposed.
            try
            {
                _sendCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Unreachable while serialization holds; belt and braces.
            }

            conversation = _conversation;
        }

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

    /// <summary>
    /// Poll THIS worker's session stats (get_session_stats): Pi-reported
    /// totals for this worker's session only — the honest token/cost source
    /// for status binding and Overview (issue #27). Requires a live
    /// connection. Deliberately NOT capability-gated: the probed capability
    /// record does not model stats (same policy as prompt/abort), and an
    /// unsupported command reports itself at use time as
    /// PiRpcCommandException — command-level rejection is the probe.
    /// </summary>
    public async Task<StatsSnapshot?> GetSessionStatsAsync(CancellationToken cancellationToken = default)
    {
        var conversation = RequireConnected();
        return await conversation.GetSessionStatsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Last polled session stats for status binding, or null when no host
    /// poll has been recorded yet. Read-only view over the worker's bridge
    /// binding (post-settle refresh or explicit polls); never polls live.
    /// </summary>
    public StatsSnapshot? LatestStats
    {
        get { lock (_gate) { return _bridge?.LatestStats; } }
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

    /// <summary>
    /// Verify the engine reports THIS worker's session. A reported session
    /// id must match exactly; a MISSING session id is unverified unless
    /// Pi's own session-file name carries the expected id (the only other
    /// trustworthy ownership evidence the wire provides). Contradiction and
    /// total absence both refuse — a worker never shows a history it cannot
    /// prove belongs to it.
    /// </summary>
    internal static void ValidateSessionIdentity(string expectedSessionId, string workerLabel, PiEngineState state)
    {
        if (state.SessionId is not null)
        {
            if (!string.Equals(state.SessionId, expectedSessionId, StringComparison.Ordinal))
            {
                throw new PiRpcTransportException(
                    $"Worker '{workerLabel}' connected to unexpected Pi session '{state.SessionId}' (expected '{expectedSessionId}') — " +
                    "refusing to show a foreign history.");
            }

            return;
        }

        var fileName = string.IsNullOrWhiteSpace(state.SessionFile) ? null : Path.GetFileName(state.SessionFile);
        if (fileName is not null
            && (fileName.EndsWith("_" + expectedSessionId + ".jsonl", StringComparison.Ordinal)
                || fileName.EndsWith(expectedSessionId + ".jsonl", StringComparison.Ordinal)))
        {
            return; // Alternative verification: Pi's own file naming carries our id.
        }

        throw new PiRpcTransportException(
            fileName is null
                ? $"Worker '{workerLabel}' cannot verify session ownership: Pi reported no session id and no session file — treating the connection as unverified."
                : $"Worker '{workerLabel}' cannot verify session ownership: session file '{fileName}' does not carry expected id '{expectedSessionId}' — treating the connection as unverified.");
    }

    private void ValidateSession(PiEngineState state)
        => ValidateSessionIdentity(_reg.SessionId, Id, state);

    /// <summary>
    /// Usable = not disposed, identity verified on THIS connection, and a
    /// conversation present. A connection that failed validation or probing
    /// is never usable until ConnectAsync/ReconnectAsync verifies it again.
    /// </summary>
    private PiConversation RequireUsable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Volatile.Read(ref _identityVerified))
        {
            throw new InvalidOperationException(
                $"Worker '{Id}' is not verified — connect (or reconnect) successfully before sending or changing state.");
        }

        return _conversation
            ?? throw new InvalidOperationException("Worker is not connected. Call ConnectAsync first.");
    }

    private PiConversation RequireConnected() => RequireUsable();

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
            Feed.AddToolStart(activity.ToolName, activity.Summary, activity.Path, activity.IsMutating, activity.CallId, activity.DiffPreview);
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
        // Serialize with connect/reconnect, BOUNDED so a hung connect can
        // never deadlock teardown. If the gate is NOT acquired, no teardown
        // runs and no claim is released: disposal fails in a retryable
        // state with ownership retained, so an in-progress connect can
        // never interleave with a half-done teardown or publish a process
        // after a "successful" disposal.
        var gotGate = await _lifecycle.WaitAsync(DisposeGateTimeout).ConfigureAwait(false);
        if (!gotGate)
        {
            throw new TimeoutException(
                $"Worker '{Id}' teardown did not acquire the lifecycle gate within " +
                $"{DisposeGateTimeout.TotalSeconds:0.#}s because a connect/reconnect is still " +
                "running. Nothing was disposed and the session claim is retained — retry " +
                "DisposeAsync once the in-progress operation completes.");
        }

        try
        {
            FeedBridge? bridge;
            PiConversation? conversation;
            lock (_gate)
            {
                _disposed = true; // rejects new operations from this point
                try
                {
                    _sendCts?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Already retired by a racing completion; nothing to cancel.
                }

                bridge = _bridge;
                conversation = _conversation;
            }

            if (bridge is not null)
            {
                try
                {
                    bridge.Detach();
                    bridge.Dispose();
                }
                catch (Exception)
                {
                    // Bridge teardown must never block process retirement.
                }

                lock (_gate)
                {
                    if (ReferenceEquals(_bridge, bridge))
                    {
                        _bridge = null;
                    }
                }
            }

            if (conversation is not null)
            {
                conversation.ActivityChanged -= OnActivity;
                conversation.ToolActivityChanged -= OnToolActivity;
                conversation.ConnectionLost -= OnConnectionLost;
                await conversation.DisposeAsync().ConfigureAwait(false); // throws → retirement incomplete, refs kept
                lock (_gate)
                {
                    if (ReferenceEquals(_conversation, conversation))
                    {
                        _conversation = null;
                    }
                }
            }

            lock (_gate)
            {
                // The claim is released only AFTER the process is provably
                // retired: a failed disposal above leaves it held so no other
                // worker can ever share a session whose process may live.
                if (_claimed)
                {
                    PiSessionClaims.Release(_reg.SessionId, this);
                    _claimed = false;
                }

                if (_sessionLock is not null)
                {
                    // Released only AFTER the Pi process is provably retired
                    // (this block is reached only when conversation disposal
                    // succeeded) and only on the gated path — a gate-starved
                    // disposal throws earlier and keeps both. Closing the
                    // handle hands ownership back to the OS; the file stays
                    // behind (unlink/recreate is a classic race, and the
                    // file proves nothing by existing).
                    _sessionLock.Dispose();
                    _sessionLock = null;
                }

                Volatile.Write(ref _identityVerified, false);
                Volatile.Write(ref _capabilities, null);
                Volatile.Write(ref _version, null);
                Volatile.Write(ref _lastState, null);
            }
        }
        finally
        {
            _lifecycle.Release();
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
    int? ProcessId,
    bool Verified);

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
    private bool _allDisposed;
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
            try
            {
                _store.Save();
            }
            catch
            {
                // Persistence failed: roll the in-memory store back to the
                // exact bytes still on disk. No worker is constructed or
                // indexed, so no ghost registration exists in any direction
                // and the caller can simply retry.
                _store.Remove(registration.Id);
                throw;
            }

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
            wasSelected = ReferenceEquals(_selected, worker);
        }

        // Retire the process FIRST: the registration is only removed once no
        // live process depends on it, so a failed save can never orphan Pi.
        // If retirement throws, nothing has been mutated and the worker stays
        // reachable for a retry.
        await worker.DisposeAsync().ConfigureAwait(false);

        lock (_gate)
        {
            var registration = _store.TryGet(id, out var existing) ? existing : null;
            _workers.Remove(id);
            _store.Remove(id);
            try
            {
                _store.Save();
            }
            catch
            {
                // Persistence failed AFTER the process was safely retired:
                // restore the in-memory registration and the index so store,
                // collections and disk stay consistent; a retry re-runs the
                // whole removal (dispose is idempotent).
                if (registration is not null)
                {
                    _store.Restore(registration);
                }

                _workers[id] = worker;
                throw;
            }

            if (wasSelected)
            {
                _selected = _store.List()
                    .Select(r => _workers[r.Id])
                    .FirstOrDefault();
            }
        }

        WorkerRemoved?.Invoke(this, id);
        if (wasSelected)
        {
            SelectionChanged?.Invoke(this, Selected); // null when the last worker was removed.
        }
    }

    public async ValueTask DisposeAsync()
    {
        PiWorker[] remaining;
        lock (_gate)
        {
            if (_allDisposed)
            {
                return;
            }

            _disposed = true; // blocks new work; completion tracked separately
            _selected = null;
            remaining = _workers.Values.ToArray();
        }

        // Dispose EVERY worker even when one fails: a worker whose disposal
        // throws keeps its collection slot (still owned, retryable) instead
        // of being cleared away — that is how live processes became
        // unreachable before.
        var failures = new List<Exception>();
        for (var i = remaining.Length - 1; i >= 0; i--)
        {
            var worker = remaining[i];
            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _workers.Remove(worker.Id);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        bool done;
        lock (_gate)
        {
            done = _workers.Count == 0;
            _allDisposed = done;
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(
                $"{failures.Count} worker(s) could not be retired; they remain owned by this manager for a retry.", failures);
        }
    }
}
