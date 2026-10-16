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
    /// Title-row attention: the latest receipt is the latest word. An
    /// unhealthy settle warns; a healthy one quiets earlier errors (they
    /// stay visible under Verification and in the timeline — never
    /// discarded, just not mistaken for a new failure). Only when no run
    /// has settled do bare session errors raise attention on their own.
    /// </summary>
    public static AttentionState AttentionFor(
        bool connected, bool busy, IReadOnlyList<FeedEntry> entries, int errorCount)
    {
        FeedEntry? receipt = null;
        foreach (var entry in entries)
        {
            if (entry.Role == "receipt")
            {
                receipt = entry;
            }
        }

        if (receipt is not null)
        {
            if (receipt.IsError)
            {
                return new AttentionState($"Failed: {Short(receipt.Text, 90)}", AttentionLevel.Failed);
            }

            return receipt.Note == "needs review"
                ? new AttentionState($"Needs review: {Short(receipt.Text, 90)}", AttentionLevel.Warning)
                : new AttentionState("No attention requested.", AttentionLevel.Ok);
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

        if (busy)
        {
            return new AttentionState("Working — run in progress.", AttentionLevel.Ok);
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
            groups.Add(new ActivityGroup(items[0], items, OutcomeFor(items, isLatest && latestRunActive), SummaryFor(items)));
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

        if (receipt is null && active)
        {
            return ActivityOutcome.InProgress;
        }

        if (receipt is not null)
        {
            return receipt.IsError
                ? ActivityOutcome.Failed
                : receipt.Note == "needs review"
                    ? ActivityOutcome.NeedsReview
                    : ActivityOutcome.Healthy;
        }

        return failedTool ? ActivityOutcome.Failed : ActivityOutcome.Unknown;
    }

    /// Concise tool/result line for a cluster: settled tools with their
    /// outcomes, or the honest absence of tool calls.
    internal static string SummaryFor(IReadOnlyList<FeedEntry> items)
    {
        var tools = new List<string>();
        foreach (var item in items)
        {
            if (item.Role == "tool" && item.Note is not null
                && (item.Note.StartsWith("finished:", StringComparison.Ordinal)
                    || item.Note.StartsWith("failed:", StringComparison.Ordinal)))
            {
                tools.Add(ToolLabel(item));
            }
        }

        return tools.Count == 0 ? "No tool calls." : $"Tools: {string.Join(" · ", tools)}";
    }

    /// Prompt headline: first line, bounded, unwrapped.
    public static string Headline(FeedEntry prompt) => Short(prompt.Text, 100);

    private static string ToolLabel(FeedEntry entry)
    {
        var note = entry.Note ?? string.Empty;
        const string finished = "finished: ";
        const string failed = "failed: ";
        if (note.StartsWith(failed, StringComparison.Ordinal))
        {
            return $"{note[failed.Length..]} failed";
        }

        if (note.StartsWith(finished, StringComparison.Ordinal))
        {
            return $"{note[finished.Length..]} finished";
        }

        return note.Length > 0 ? note : "tool";
    }

    private static string Short(string text, int max)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= max ? flat : flat[..max].TrimEnd() + "…";
    }
}
