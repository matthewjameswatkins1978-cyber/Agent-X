using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>
/// Fixing Pass 1 acceptance: Overview and Feed agree about the same run.
/// Recovered tool errors stay quiet, genuine problems warn, cancellations
/// and empty settles are neutral, and post-settle failures are never
/// hidden behind a stale receipt.
/// </summary>
public sealed class TrustworthyRunStatusTests
{
    private static FeedEntry Row(long seq, string role, string text, string? note = null, bool isError = false)
        => new(seq, role, text, Note: note, IsError: isError);

    [Fact]
    public void CleanRun_FeedAndOverviewAgreeHealthy()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "reply please"),
            Row(2, "assistant", "done"),
            Row(3, "receipt", "Healthy — settled cleanly with no observed errors.", "healthy"),
        };
        Assert.Equal(AttentionLevel.Ok, OverviewSummary.AttentionFor(true, false, entries, 0).Level);
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal(ActivityOutcome.Healthy, only.Outcome);
    }

    [Fact]
    public void RecoveredRun_FeedAndOverviewStayQuiet()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "fix it"),
            Row(2, "tool", "Tool edit failed.", "failed: edit", true),
            Row(3, "tool", "Tool edit finished.", "finished: edit"),
            Row(4, "assistant", "corrected"),
            Row(5, "receipt", "Settled with 1 recovered tool error — failures preserved in the feed.", "healthy"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Ok, attention.Level);
        Assert.Equal("No attention requested.", attention.Text);
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal(ActivityOutcome.Healthy, only.Outcome);
        // The failure is still described, just not alarmed on.
        Assert.Contains("1 failed", only.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnresolvedRun_FeedAndOverviewDemandReview()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "do it"),
            Row(2, "tool", "Tool exec failed.", "failed: exec", true),
            Row(3, "assistant", "tried"),
            Row(4, "receipt", "Settled with 1 problem (2 observed) — review before trusting this answer.", "needs review"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Warning, attention.Level);
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal(ActivityOutcome.NeedsReview, only.Outcome);
    }

    [Fact]
    public void GenuineFailure_FeedAndOverviewAgreeFailed()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "do it"),
            Row(2, "receipt", "Aborted — task did not complete.", "needs review", true),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 0);
        Assert.Equal(AttentionLevel.Failed, attention.Level);
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal(ActivityOutcome.Failed, only.Outcome);
    }

    [Fact]
    public void TransportErrorAfterHealthyReceipt_Warns()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "hello"),
            Row(2, "assistant", "hi"),
            Row(3, "receipt", "Healthy — settled cleanly with no observed errors.", "healthy"),
            Row(4, "system", "Connection to Pi lost: pipe broke", "error", true),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Warning, attention.Level);
        Assert.Contains("since the last settled run", attention.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledRun_ReplacesStaleReceipt()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "first"),
            Row(2, "receipt", "Settled with 1 unresolved problem — review before trusting.", "needs review", true),
            Row(3, "user", "second"),
            Row(4, "assistant", "partial"),
            Row(5, "system", "Run cancelled — partial streamed text above is kept as-is.", "cancelled"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Guidance, attention.Level);
        Assert.Contains("cancelled", attention.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyReply_IsNeutralEverywhere()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "hello?"),
            Row(2, "assistant", string.Empty),
            Row(3, "receipt", "Turn complete — Pi provided no reply text; no task outcome claimed.", "no reply"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 0);
        Assert.Equal(AttentionLevel.Ok, attention.Level);
        Assert.Contains("no reply text", attention.Text, StringComparison.OrdinalIgnoreCase);
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal(ActivityOutcome.Unknown, only.Outcome);
    }

    [Fact]
    public void BusyRun_ShowsWorking_NotStaleVerdict()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "first"),
            Row(2, "receipt", "Settled with 1 unresolved problem.", "needs review", true),
            Row(3, "user", "second"),
        };
        var attention = OverviewSummary.AttentionFor(true, true, entries, 1);
        Assert.Equal(AttentionLevel.Ok, attention.Level);
        Assert.Contains("Working", attention.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoredHistory_WithoutSettle_WarnsHonestly()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "tool", "[tool exec failed]\nboom"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Warning, attention.Level);
    }

    [Fact]
    public void RestoredHistory_ThenHealthySettle_QuietsButPreserves()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "tool", "[tool exec failed]\nboom"),
            Row(3, "user", "retry"),
            Row(4, "assistant", "recovered answer"),
            Row(5, "receipt", "Healthy — settled cleanly with no observed errors.", "healthy"),
        };
        var attention = OverviewSummary.AttentionFor(true, false, entries, 1);
        Assert.Equal(AttentionLevel.Ok, attention.Level);
    }
}
