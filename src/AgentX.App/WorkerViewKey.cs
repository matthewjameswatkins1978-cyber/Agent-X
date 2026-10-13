namespace AgentX.App;

/// <summary>
/// View-state identity for worker tabs (App-only, UI-free). Worker drafts,
/// selection and scroll are namespaced by persistent worker id — never by
/// tab position — while workspace keys pass through byte-identical so
/// existing persisted drafts keep working with no migration.
/// </summary>
public static class WorkerViewKey
{
    public const string Prefix = "worker/";

    public static string ForWorker(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Prefix + id;
    }

    public static bool IsWorkerKey(string? key)
        => key is not null && key.StartsWith(Prefix, StringComparison.Ordinal);

    public static string? WorkerIdOf(string? key)
        => IsWorkerKey(key) ? key![Prefix.Length..] : null;

    /// Removal gate: a worker with a run or connect in flight refuses
    /// removal until it settles. Pure so the refusal contract is testable
    /// without a window.
    public static bool CanRemove(bool busy, bool connecting) => !busy && !connecting;

    /// <summary>Display label: stored label, or a stable short-id fallback (Core allows blank labels).</summary>
    public static string DisplayLabel(string id, string? label)
        => string.IsNullOrWhiteSpace(label)
            ? $"Worker {id[..Math.Min(4, id.Length)]}"
            : label.Trim();

    public static string Tooltip(string id, string? label, string sessionId, string projectRoot)
        => $"Worker '{DisplayLabel(id, label)}' — session {sessionId} · {projectRoot}";

    /// <summary>
    /// Draft-prune allowlist: catalog keys plus namespaced worker ids.
    /// Either side may be absent (degraded registry or unavailable worker
    /// store); absence prunes nothing by itself — callers pass prune=false
    /// when their source is unhealthy.
    /// </summary>
    public static string[] MergeKnownKeys(IEnumerable<string>? catalogKeys, IEnumerable<string>? workerIds)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (catalogKeys is not null)
        {
            foreach (var key in catalogKeys)
            {
                keep.Add(key);
            }
        }

        if (workerIds is not null)
        {
            foreach (var id in workerIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    keep.Add(ForWorker(id));
                }
            }
        }

        return keep.ToArray();
    }
}
