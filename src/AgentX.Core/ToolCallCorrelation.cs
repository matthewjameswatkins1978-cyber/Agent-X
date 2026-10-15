namespace AgentX.Core;

/// <summary>
/// One tool call's observed lifecycle across the streamed RPC events:
/// announcement (<c>toolcall_start</c>), execution start, progress updates,
/// and the terminal end. A pure derived view — no storage, no subscription,
/// no second log. Events with an empty call id (absent, or withheld by
/// redaction) cannot correlate and are excluded.
/// </summary>
public sealed record ToolCallLifecycle(
    string CallId,
    string ToolName,
    bool Announced,
    bool Started,
    bool Ended,
    int ProgressUpdates,
    int StartCount,
    int EndCount)
{
    /// <summary>An end arrived with no observed start for this id.</summary>
    public bool EndWithoutStart => Ended && !Started;

    /// <summary>
    /// A start never reached an observed end: still running, lost, retried
    /// under a new id, or outside the retained window.
    /// </summary>
    public bool StartWithoutEnd => Started && !Ended;

    /// <summary>More than one execution start claimed this id (retry or duplicate delivery).</summary>
    public bool DuplicateStart => StartCount > 1;

    /// <summary>More than one end claimed this id (duplicate delivery).</summary>
    public bool DuplicateEnd => EndCount > 1;
}

/// <summary>
/// Correlates tool invocation and result events by observed call id.
/// Stateless: summarizes whatever event window it is handed (typically the
/// bounded tap store), so eviction shows up honestly as
/// <see cref="ToolCallLifecycle.StartWithoutEnd"/> or
/// <see cref="ToolCallLifecycle.EndWithoutStart"/> rather than as a claim.
/// Lifecycles report in first-appearance order; the tool name is the first
/// non-empty name observed for the id.
/// </summary>
public static class ToolCallCorrelation
{
    public static IReadOnlyList<ToolCallLifecycle> Summarize(IEnumerable<AgentXEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var order = new List<string>();
        var states = new Dictionary<string, Mutable>(StringComparer.Ordinal);
        Mutable Ref(string callId, string toolName)
        {
            if (!states.TryGetValue(callId, out var mutable))
            {
                mutable = new Mutable();
                states.Add(callId, mutable);
                order.Add(callId);
            }

            if (mutable.ToolName.Length == 0 && toolName.Length > 0)
            {
                mutable.ToolName = toolName;
            }

            return mutable;
        }

        foreach (var e in events)
        {
            switch (e)
            {
                case ToolCallAnnounced a when a.CallId.Length > 0:
                    Ref(a.CallId, a.ToolName).Announced = true;
                    break;
                case ToolStarted s when s.CallId.Length > 0:
                    var started = Ref(s.CallId, s.ToolName);
                    started.Started = true;
                    started.Starts++;
                    break;
                case ToolProgress p when p.CallId.Length > 0:
                    Ref(p.CallId, p.ToolName).Progress++;
                    break;
                case ToolEnded x when x.CallId.Length > 0:
                    var ended = Ref(x.CallId, x.ToolName);
                    ended.Ended = true;
                    ended.Ends++;
                    break;
                default:
                    break;
            }
        }

        var lifecycles = new List<ToolCallLifecycle>(order.Count);
        foreach (var callId in order)
        {
            var mutable = states[callId];
            lifecycles.Add(new ToolCallLifecycle(
                callId, mutable.ToolName, mutable.Announced, mutable.Started, mutable.Ended,
                mutable.Progress, mutable.Starts, mutable.Ends));
        }

        return lifecycles;
    }

    private sealed class Mutable
    {
        public string ToolName = string.Empty;
        public bool Announced;
        public bool Started;
        public bool Ended;
        public int Progress;
        public int Starts;
        public int Ends;
    }
}
