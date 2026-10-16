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
}

public sealed record AttentionState(string Text, AttentionLevel Level);

/// <summary>
/// Overview summarization (Checkpoint B2): attention verdicts and activity
/// rows derived from feed entries only. The view renders these; it never
/// invents missions, percentages or alerts. Pure and tested.
/// </summary>
public static class OverviewSummary
{
    /// <summary>
    /// Title-row attention: unhealthy last receipt first, then session
    /// errors, then connection guidance, then the working state. A clean
    /// idle agent asks for nothing.
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

        if (receipt is not null && (receipt.IsError || receipt.Note == "needs review"))
        {
            return new AttentionState($"Needs review: {Short(receipt.Text, 90)}", AttentionLevel.Warning);
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

    /// <summary>
    /// Newest-first meaningful activity: user prompts, settled tool rows,
    /// receipts and errors. Detail/progress/result system rows are skipped
    /// (represented by their tool block), as are streaming rows.
    /// </summary>
    public static IReadOnlyList<FeedEntry> RecentActivity(IReadOnlyList<FeedEntry> entries, int max = 8)
    {
        var kept = new List<FeedEntry>();
        for (var i = entries.Count - 1; i >= 0 && kept.Count < max; i--)
        {
            var entry = entries[i];
            if (entry.IsStreaming)
            {
                continue;
            }

            var useful = entry.Role switch
            {
                "user" => true,
                "receipt" => true,
                "tool" => entry.Note is not null
                    && (entry.Note.StartsWith("finished:", StringComparison.Ordinal)
                        || entry.Note.StartsWith("failed:", StringComparison.Ordinal)),
                "system" => entry.IsError,
                _ => false,
            };

            if (useful)
            {
                kept.Add(entry);
            }
        }

        return kept;
    }

    /// <summary>One-line activity label plus muted detail for a timeline row.</summary>
    public static (string Label, string Detail) Summarize(FeedEntry entry) => entry.Role switch
    {
        "user" => ($"Asked: {Short(entry.Text, 80)}", $"message #{entry.Seq}"),
        "receipt" => ($"Settled: {Short(entry.Text, 80)}", $"receipt #{entry.Seq}"),
        "tool" => (ToolLabel(entry), $"tool #{entry.Seq}"),
        _ => (Short(entry.Text, 80), $"#{entry.Seq}"),
    };

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
