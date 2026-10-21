using AgentX.Core;

namespace AgentX.App;

/// <summary>Attention verdict for the Overview title row. Guidance is
/// neutral direction (connect, wait); Warning is a genuine observed
/// problem. Nothing is invented: every state derives from feed rows.</summary>
public enum AttentionLevel
{
    Ok,
    Guidance,
    Warning,
    Failed,
}

public sealed record AttentionState(string Text, AttentionLevel Level);

/// <summary>Run-cluster outcome for the activity timeline dots.</summary>
public enum ActivityOutcome
{
    Healthy,
    NeedsReview,
    Failed,
    InProgress,
    Unknown,
}

/// <summary>
/// Overview summarization (Checkpoint B2): attention verdicts and activity
/// rows derived from feed entries only. The view renders these; it never
/// invents missions, percentages or alerts. Pure and tested.
/// </summary>
public static class OverviewSummary
{
    /// <summary>
    /// Title-row attention: the latest receipt is the latest word about
    /// its own run — but never about a LATER one. Errors after the latest
    /// settle belong to the current unsettled run and warn on their own;
    /// a trailing cancellation replaces a stale receipt instead of letting
    /// it speak. A settled run with only recovered tool errors stays quiet
    /// (the failures remain under Verification and in the timeline); an
    /// empty settle is neutral, never a success claim. Only runs with no
    /// settle at all bill bare session errors to attention.
    /// </summary>
    public static AttentionState AttentionFor(
        bool connected, bool busy, IReadOnlyList<FeedEntry> entries, int errorCount)
    {
        if (busy)
        {
            return new AttentionState("Working — run in progress.", AttentionLevel.Ok);
        }

        FeedEntry? receipt = null;
        var receiptIndex = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Role == "receipt")
            {
                receipt = entries[i];
                receiptIndex = i;
            }
        }

        var errorsAfter = 0;
        var cancelledAfter = false;
        for (var i = receiptIndex + 1; i < entries.Count; i++)
        {
            if (entries[i].Role != "receipt" && entries[i].IsError)
            {
                errorsAfter++;
            }

            if (entries[i].Note == "cancelled")
            {
                cancelledAfter = true;
            }
        }

        if (cancelledAfter && errorsAfter == 0)
        {
            return new AttentionState(
                "Last run cancelled — run did not complete and earned no health signal.",
                AttentionLevel.Guidance);
        }

        if (errorsAfter > 0)
        {
            var afterPlural = errorsAfter == 1 ? "event" : "events";
            return new AttentionState(
                $"{errorsAfter} error {afterPlural} since the last settled run — review before trusting.",
                AttentionLevel.Warning);
        }

        if (receipt is not null)
        {
            if (receipt.IsError)
            {
                return new AttentionState($"Failed: {Short(receipt.Text, 90)}", AttentionLevel.Failed);
            }

            if (receipt.Note == "needs review")
            {
                return new AttentionState($"Needs review: {Short(receipt.Text, 90)}", AttentionLevel.Warning);
            }

            if (receipt.Note == "no reply")
            {
                return new AttentionState("Turn complete — no reply text from Pi.", AttentionLevel.Ok);
            }

            return new AttentionState("No attention requested.", AttentionLevel.Ok);
        }

        if (errorCount > 0)
        {
            var plural = errorCount == 1 ? "event" : "events";
            return new AttentionState($"{errorCount} error {plural} observed — review before trusting.", AttentionLevel.Warning);
        }

        if (!connected)
        {
            return new AttentionState("Not connected — press Connect on the Feed page.", AttentionLevel.Guidance);
        }

        return new AttentionState("No attention requested.", AttentionLevel.Ok);
    }

    /// One run cluster: the user prompt plus every following entry up to
    /// the next prompt. Grouping follows the feed's own association — one
    /// run appends its rows between AddUser and its receipt, and only one
    /// run is ever active per feed — never invented clusters.
    public sealed record ActivityGroup(
        FeedEntry Prompt, IReadOnlyList<FeedEntry> Items, ActivityOutcome Outcome, string Summary);

    /// Group newest-first into run clusters, capped. Detail/progress/result
    /// system rows fold into their cluster (represented by the tool block);
    /// streaming rows are skipped. An entry before the first prompt (history
    /// edge) forms no group and is dropped from the timeline.
    public static IReadOnlyList<ActivityGroup> GroupActivity(
        IReadOnlyList<FeedEntry> entries, bool latestRunActive, int maxGroups)
    {
        var clusters = new List<List<FeedEntry>>();
        foreach (var entry in entries)
        {
            if (entry.IsStreaming)
            {
                continue;
            }

            if (entry.Role == "user")
            {
                clusters.Add([entry]);
            }
            else if (clusters.Count > 0)
            {
                clusters[^1].Add(entry);
            }
        }

        var groups = new List<ActivityGroup>();
        for (var i = clusters.Count - 1; i >= 0 && groups.Count < maxGroups; i--)
        {
            var items = clusters[i];
            var isLatest = i == clusters.Count - 1;
            var active = isLatest && latestRunActive;
            var settled = items.Any(e => e.Role == "receipt");
            groups.Add(new ActivityGroup(items[0], items, OutcomeFor(items, active), SummaryFor(items, settled, active)));
        }

        return groups;
    }

    /// Total run clusters (for the "earlier runs" affordance).
    public static int GroupCount(IReadOnlyList<FeedEntry> entries)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            if (!entry.IsStreaming && entry.Role == "user")
            {
                count++;
            }
        }

        return count;
    }

    /// Cluster outcome from its own rows: the active latest run is in
    /// progress; otherwise the receipt rules, then any failed tool, else
    /// the quiet unknown (ended without a recorded outcome).
    internal static ActivityOutcome OutcomeFor(IReadOnlyList<FeedEntry> items, bool active)
    {
        FeedEntry? receipt = null;
        var failedTool = false;
        foreach (var item in items)
        {
            if (item.Role == "receipt")
            {
                receipt = item;
            }
            else if (item.Role == "tool" && (item.IsError
                || (item.Note?.StartsWith("failed:", StringComparison.Ordinal) == true)))
            {
                failedTool = true;
            }
        }

        // Restored rows carry no lifecycle note: the same header parser
        // the session map uses proves a persisted failure here too.
        if (!failedTool)
        {
            foreach (var item in items)
            {
                if (item.Role == "tool"
                    && ConversationFeed.ParseHistoryToolIdentity(item.Text)?.IsError == true)
                {
                    failedTool = true;
                    break;
                }
            }
        }

        if (receipt is null && active)
        {
            return ActivityOutcome.InProgress;
        }

        if (receipt is not null)
        {
            // A neutral settle ("no reply" or any future non-review note)
            // records no outcome: Unknown, honestly — never Healthy.
            return receipt.IsError
                ? ActivityOutcome.Failed
                : receipt.Note == "needs review"
                    ? ActivityOutcome.NeedsReview
                    : receipt.Note == "healthy"
                        ? ActivityOutcome.Healthy
                        : ActivityOutcome.Unknown;
        }

        return failedTool ? ActivityOutcome.Failed : ActivityOutcome.Unknown;
    }

    /// One row's observed tool evidence. Live lifecycle notes first, then
    /// the same header parser the session map uses for restored rows, then
    /// the honest remainder: a tool-role row IS observed tool activity even
    /// when its name is unprovable (bash "Ran …" bodies, unverified ends).
    /// Names are never invented — unidentified rows stay "tool"/"shell".
    /// Failed is null while the outcome is unobserved (pending or unknown).
    internal sealed record ToolFact(string Name, bool? Failed, bool IsLiveStart);

    internal static ToolFact? ToolOutcome(FeedEntry item)
    {
        if (item.Role != "tool")
        {
            return null;
        }

        var note = item.Note ?? string.Empty;
        const string finished = "finished: ";
        const string failed = "failed: ";
        const string started = "started: ";
        if (note.StartsWith(failed, StringComparison.Ordinal))
        {
            return new ToolFact(CleanName(note[failed.Length..]), true, false);
        }

        if (note.StartsWith(finished, StringComparison.Ordinal))
        {
            return new ToolFact(CleanName(note[finished.Length..]), false, false);
        }

        if (note.StartsWith(started, StringComparison.Ordinal))
        {
            return new ToolFact(CleanName(note[started.Length..]), null, true);
        }

        if (!string.IsNullOrEmpty(note))
        {
            return null;
        }

        // Restored row (no lifecycle note): the session map's own parser.
        if (ConversationFeed.ParseHistoryToolIdentity(item.Text) is { } identity)
        {
            return new ToolFact(identity.Name, identity.IsError, false);
        }

        if (item.Text.StartsWith("Ran ", StringComparison.Ordinal))
        {
            return new ToolFact("shell", item.IsError ? true : null, false);
        }

        return new ToolFact("tool", item.IsError ? true : null, false);
    }

    private static string CleanName(string raw)
    {
        var name = raw.Trim();
        return name.Length == 0 ? "tool" : name;
    }

    /// Concise tool/result line for a cluster: distinct tool names with
    /// counts (never one label repeated five times), then the honest
    /// absence states. Three cases, never conflated: none occurred (a
    /// settled or active run with no tool evidence at all), none observed
    /// (an unrestored cluster — no receipt proves the history complete),
    /// and history incomplete (call markers or pending starts without
    /// their recovered rows).
    internal static string SummaryFor(IReadOnlyList<FeedEntry> items, bool settled, bool active)
    {
        var facts = new List<ToolFact>();
        var markers = 0;
        var pendingStale = 0;
        foreach (var item in items)
        {
            if (item.Role == "assistant" && !string.IsNullOrEmpty(item.Text))
            {
                markers += CountMarkers(item.Text);
            }

            if (ToolOutcome(item) is { } fact)
            {
                if (fact.IsLiveStart && !active)
                {
                    // A live start in a cluster that is over never settled:
                    // evidence of incompleteness, not a running tool.
                    // (Restored rows with unobserved outcomes are complete
                    // recovered operations — command and output present —
                    // so they stay facts, shown without an outcome word.)
                    pendingStale++;
                }
                else
                {
                    facts.Add(fact);
                }
            }
        }

        if (facts.Count > 0)
        {
            return "Tools: " + string.Join(" · ", GroupLabels(facts, active));
        }

        if (markers > 0 || pendingStale > 0)
        {
            var noted = markers > 0 ? $"{markers} call{(markers == 1 ? string.Empty : "s")} noted" : "starts without ends";
            return $"Tool history incomplete — {noted}, none recovered.";
        }

        return settled || active ? "No tool calls." : "No tool calls observed.";
    }

    /// Group settled facts by name, preserving first-seen order: each
    /// distinct tool appears once with its count and outcome words.
    private static IEnumerable<string> GroupLabels(List<ToolFact> facts, bool active)
    {
        var order = new List<string>();
        var groups = new Dictionary<string, List<bool?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in facts)
        {
            if (!groups.TryGetValue(fact.Name, out var list))
            {
                list = [];
                groups[fact.Name] = list;
                order.Add(fact.Name);
            }

            list.Add(fact.Failed);
        }

        foreach (var name in order)
        {
            var outcomes = groups[name];
            var label = outcomes.Count > 1 ? $"{name} ×{outcomes.Count}" : name;
            var failed = outcomes.Count(o => o == true);
            if (outcomes.Any(o => o is null))
            {
                // Nulls here are live starts on the active run (stale ones
                // were diverted above) or restored rows whose outcome Pi
                // never persisted: only a live start means running.
                var running = active && facts.Where(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Any(f => f.IsLiveStart);
                yield return running ? $"{label} running" : label;
            }
            else if (failed == outcomes.Count)
            {
                yield return $"{label} failed";
            }
            else if (failed > 0)
            {
                yield return $"{label} ({failed} failed)";
            }
            else
            {
                yield return label;
            }
        }
    }

    private static int CountMarkers(string text)
    {
        const string marker = "[tool call: ";
        var count = 0;
        var at = 0;
        while ((at = text.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += marker.Length;
        }

        return count;
    }

    /// Prompt headline: first line, bounded, unwrapped.
    public static string Headline(FeedEntry prompt) => Short(prompt.Text, 100);



    private static string Short(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }
}
