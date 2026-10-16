using AgentX.Core;

namespace AgentX.App;

/// <summary>
/// What the VIEWED tab shows (App-only, UI-free). Selection-scoped display
/// rules with no fallback across identities: an unconnected worker shows
/// unknown engine values (never legacy's), the working indicator reflects
/// only the viewed run, and tab-list repair after a removal never disturbs
/// a surviving view.
/// </summary>
public static class WorkerDisplayPolicy
{
    /// <summary>
    /// Engine facts for a worker tab: the last confirmed state, or all
    /// unknown when never confirmed. Never legacy values.
    /// </summary>
    public static (string? Provider, string? Model, string? Thinking, int? Messages) EngineForWorker(PiEngineState? confirmed)
        => confirmed is null
            ? (null, null, null, null)
            : (confirmed.Provider, confirmed.ModelName ?? confirmed.ModelId, confirmed.ThinkingLevel, confirmed.MessageCount);

    /// <summary>Working indicator: visible exactly when the viewed run is busy.</summary>
    public static bool ShowWorking(bool viewedIsBusy) => viewedIsBusy;

    /// <summary>
    /// Card-strip order from the two authorities: catalog entries first
    /// (legacy + registered workspaces, in catalog order), then workers
    /// (in manager order), each key once. A catalog rebuild must never
    /// drop a worker: both populations always merge here, so registering
    /// a workspace cannot hide an existing worker. Ordinal throughout.
    /// </summary>
    public static IReadOnlyList<string> StripOrder(
        IReadOnlyList<string> catalogKeys, IReadOnlyList<string> workerIds)
    {
        var order = new List<string>(catalogKeys.Count + workerIds.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in catalogKeys)
        {
            if (seen.Add(key))
            {
                order.Add(key);
            }
        }

        foreach (var id in workerIds)
        {
            if (seen.Add(id))
            {
                order.Add(id);
            }
        }

        return order;
    }

    /// <summary>
    /// Selection reveal without scroll-jacking: scroll the strip only
    /// when the SELECTED key actually changed (registration, removal,
    /// user switch). Busy/connection/state refreshes keep the key and
    /// must never fight the user's scroll position.
    /// </summary>
    public static bool ShouldReveal(string? lastRevealedKey, string? selectedKey)
        => selectedKey is not null
            && !string.Equals(lastRevealedKey, selectedKey, StringComparison.Ordinal);

    /// <summary>
    /// Tab-list repair after a worker removal: which worker id to show, or
    /// null to fall back to the legacy workspace view. A null viewed id
    /// (workspace showing) repairs nothing; a surviving viewed worker is
    /// reselected untouched; a vanished one falls to the manager selection.
    /// </summary>
    public static string? RepairTarget(string? viewedWorkerId, IReadOnlyList<string> remainingIds, string? managerSelectedId)
    {
        if (viewedWorkerId is null)
        {
            return null;
        }

        if (remainingIds.Contains(viewedWorkerId, StringComparer.Ordinal))
        {
            return viewedWorkerId;
        }

        return managerSelectedId;
    }
}
