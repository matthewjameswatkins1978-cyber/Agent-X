namespace AgentX.App;

/// <summary>
/// UI-free per-tab view store backing MainWindow's save/restore pairing.
/// Owns the snapshot-per-key lifecycle and nothing else (no controls, no
/// IO, no feed): <see cref="Save"/> reports whether the stored value
/// changed so the host skips redundant disk writes; <see cref="TryGet"/>
/// reports prior existence so the host keeps snapshots over generic hints;
/// <see cref="MarkDirty"/>/<see cref="TakeDirty"/> force a persist retry
/// after a failed write without ever destroying the in-memory copy.
/// </summary>
public sealed class WorkerViewStore
{
    private readonly Dictionary<string, WorkerViewState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);

    public bool Contains(string key) => _states.ContainsKey(key);

    public bool TryGet(string key, out WorkerViewState? state)
        => _states.TryGetValue(key, out state);

    public WorkerViewState GetOrCreate(string key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new WorkerViewState();
            _states[key] = state;
        }

        return state;
    }

    /// <summary>
    /// Store a snapshot under its owning key. Returns true when the stored
    /// value changed (record equality) — the host persists only then.
    /// Never touches any other key: a refused transition cannot clobber
    /// the active tab through this store.
    /// </summary>
    public bool Save(string key, WorkerViewState snapshot)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_states.TryGetValue(key, out var existing) && existing == snapshot)
        {
            return false;
        }

        _states[key] = snapshot;
        return true;
    }

    /// <summary>Force the next save of this key to persist (retry accounting).</summary>
    public void MarkDirty(string key) => _dirty.Add(key);

    /// <summary>Consume a pending dirty flag; true when one was pending.</summary>
    public bool TakeDirty(string key) => _dirty.Remove(key);
}
