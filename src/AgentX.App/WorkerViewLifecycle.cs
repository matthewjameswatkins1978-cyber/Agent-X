namespace AgentX.App;

/// <summary>
/// Save/restore ordering policy for per-tab view state (App-only, UI-free).
/// The FIRST restore after construction must not be preceded by a snapshot:
/// the composer still holds its default empty string, and snapshotting it
/// would overwrite a persisted draft with "" before the restore ever reads
/// it (startup draft-loss). Every later rebuild snapshots the outgoing view
/// first, including refused pinned switches (same-key round-trip).
/// MainWindow reads controls into snapshots and applies restored states;
/// this class owns ordering, the store, and the startup flag.
/// </summary>
public sealed class WorkerViewLifecycle
{
    private readonly WorkerViewStore _store = new();
    private bool _startupComplete;

    public WorkerViewStore Store => _store;

    /// True once the startup restore has run: only then is the visible view
    /// initialized, and only then may rebuilds snapshot the outgoing view.
    public bool StartupComplete => _startupComplete;

    /// Restore one tab's view, seeding from persisted drafts on first visit.
    /// Returns the state to show and whether a prior snapshot existed (so
    /// callers keep it over generic hints). Marks startup complete.
    public (WorkerViewState State, bool Existed) Restore(string key, Func<string, string?> readPersistedDraft)
    {
        var existed = _store.Contains(key);
        var state = _store.GetOrCreate(key);
        if (!existed && readPersistedDraft(key) is { } draft)
        {
            state.DraftText = draft;
            existed = true;
        }

        _startupComplete = true;
        return (state, existed);
    }
}
