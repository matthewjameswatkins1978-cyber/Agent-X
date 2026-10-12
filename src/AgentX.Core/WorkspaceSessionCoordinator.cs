namespace AgentX.Core;

/// <summary>
/// Outcome of one workspace identity transition request.
/// </summary>
public sealed record WorkspaceTransition(bool Accepted, string? RefusalReason)
{
    public static WorkspaceTransition Accept() => new(true, null);
    public static WorkspaceTransition Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Authoritative workspace-ownership state machine (UI-free): which workspace
/// key owns the loaded feed, which (if any) owns the live Pi conversation,
/// and per-workspace feed snapshots. EVERY identity change — user tab click
/// or programmatic reselect after a registry rebuild — goes through
/// <see cref="RequestTransition"/>, so the guarded switch and the
/// feed-memory sync can never be bypassed by a suppressed UI event.
///
/// Rules:
/// - A connected workspace pins its tab: transitions elsewhere are refused
///   with a reason; the feed and the active key stay untouched.
/// - Disconnected transitions snapshot the outgoing feed under its key and
///   restore the target's snapshot (or establish an empty view).
/// - The initial key is seeded at construction: the first workspace always
///   has a memory owner, so its live-only view is never discarded for lack
///   of one.
/// - Same-key requests are no-ops. Unknown keys are treated like any other
///   disconnected target (the host only requests keys it displays).
/// </summary>
public sealed class WorkspaceSessionCoordinator
{
    private readonly ConversationFeed _feed;
    private readonly Dictionary<string, FeedEntry[]> _memory = new();
    private string _activeKey;
    private string? _connectedKey;

    public WorkspaceSessionCoordinator(ConversationFeed feed, string initialKey)
    {
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        ArgumentException.ThrowIfNullOrWhiteSpace(initialKey);
        _activeKey = initialKey;
        _memory[initialKey] = [];
    }

    /// <summary>Workspace key whose view is currently loaded.</summary>
    public string ActiveKey => _activeKey;

    /// <summary>Workspace key owning the live Pi conversation, if any.</summary>
    public string? ConnectedKey => _connectedKey;

    public void NoteConnected(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _connectedKey = key;
        _activeKey = key;
    }

    public void NoteDisconnected()
    {
        _connectedKey = null;
    }

    /// <summary>
    /// Store the currently loaded feed as the active workspace's view. The
    /// host calls this after loading server history on connect, so a later
    /// away-and-back restores the connected history rather than the
    /// pre-connect snapshot.
    /// </summary>
    public void RefreshMemory()
    {
        _memory[_activeKey] = _feed.Snapshot().ToArray();
    }

    /// <summary>
    /// Snapshot the outgoing feed and load the target view. Refuses when a
    /// live conversation pins a different workspace.
    /// </summary>
    public WorkspaceTransition RequestTransition(string newKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);
        if (_connectedKey is not null && newKey != _connectedKey)
        {
            return WorkspaceTransition.Refuse(
                $"Workspace '{_connectedKey}' owns the live Pi conversation.");
        }

        if (_activeKey != newKey)
        {
            _memory[_activeKey] = _feed.Snapshot().ToArray();
            _activeKey = newKey;
            if (_memory.TryGetValue(newKey, out var saved))
            {
                _feed.LoadSnapshot(saved);
            }
            else
            {
                _feed.LoadHistory([]);
                _memory[newKey] = [];
            }
        }

        return WorkspaceTransition.Accept();
    }

    /// <summary>True when the coordinator holds a snapshot for the key.</summary>
    public bool HasMemory(string key) => _memory.ContainsKey(key);
}
