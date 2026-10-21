using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>Overview summarization contract: attention priority and activity
/// filtering derive strictly from feed rows — nothing invented.</summary>
public sealed class OverviewSummaryTests
{
    private static FeedEntry Row(long seq, string role, string text, string? note = null, bool isError = false)
        => new(seq, role, text, Note: note, IsError: isError);

    [Fact]
    public void Attention_CleanIdle_AsksNothing()
    {
        var state = OverviewSummary.AttentionFor(true, false, [], 0);
        Assert.Equal(AttentionLevel.Ok, state.Level);
        Assert.Equal("No attention requested.", state.Text);
    }

    [Fact]
    public void Attention_Disconnected_GuidesToConnect()
    {
        var state = OverviewSummary.AttentionFor(false, false, [], 0);
        Assert.Equal(AttentionLevel.Guidance, state.Level);
        Assert.Contains("Connect", state.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_Busy_ReportsWorking()
    {
        var state = OverviewSummary.AttentionFor(true, true, [], 0);
        Assert.Equal(AttentionLevel.Ok, state.Level);
    }

    [Fact]
    public void Attention_ErrorsWithoutReceipt_Warn()
    {
        var state = OverviewSummary.AttentionFor(false, false, [], 3);
        Assert.Equal(AttentionLevel.Warning, state.Level);
        Assert.Contains("3 error events", state.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_HealthyReceiptQuietsEarlierErrors()
    {
        // Latest word wins: a healthy settle is not a new failure, even
        // with historical errors (they stay under Verification).
        var entries = new[] { Row(1, "receipt", "Healthy", "healthy") };
        var state = OverviewSummary.AttentionFor(true, false, entries, 2);
        Assert.Equal(AttentionLevel.Ok, state.Level);
        Assert.Equal("No attention requested.", state.Text);
    }

    [Fact]
    public void Attention_BadReceipt_OutranksErrors()
    {
        var entries = new[] { Row(1, "receipt", "Settled with 1 problem", "needs review") };
        var state = OverviewSummary.AttentionFor(true, false, entries, 2);
        Assert.Equal(AttentionLevel.Warning, state.Level);
        Assert.StartsWith("Needs review:", state.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_ErrorReceipt_FailsRed()
    {
        var entries = new[] { Row(1, "receipt", "Aborted", "needs review") };
        entries[0] = entries[0] with { IsError = true };
        var state = OverviewSummary.AttentionFor(true, false, entries, 0);
        Assert.Equal(AttentionLevel.Failed, state.Level);
        Assert.StartsWith("Failed:", state.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Attention_HealthyReceipt_StaysQuiet()
    {
        var entries = new[] { Row(1, "receipt", "Healthy", "healthy") };
        var state = OverviewSummary.AttentionFor(true, false, entries, 0);
        Assert.Equal(AttentionLevel.Ok, state.Level);
    }

    [Fact]
    public void GroupActivity_SplitsOnPromptsNewestFirst()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "do the thing"),
            Row(2, "tool", "out", "finished: read"),
            Row(3, "receipt", "Healthy", "healthy"),
            Row(4, "user", "do another"),
            Row(5, "tool", "boom", "failed: exec", true),
        };
        var groups = OverviewSummary.GroupActivity(entries, false, 10);
        Assert.Equal(2, groups.Count);
        Assert.Equal(4, groups[0].Prompt.Seq);
        Assert.Equal(ActivityOutcome.Failed, groups[0].Outcome);
        Assert.Equal(1, groups[1].Prompt.Seq);
        Assert.Equal(ActivityOutcome.Healthy, groups[1].Outcome);
        Assert.Equal(2, OverviewSummary.GroupCount(entries));
    }

    [Fact]
    public void GroupActivity_DetailRowsFoldIntoCluster()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "do the thing"),
            Row(2, "system", "[detail call_x] Run read: args"),
            Row(3, "system", "[result call_x] Tool read: ok."),
            Row(4, "tool", "out", "finished: read"),
            Row(5, "receipt", "Healthy", "healthy"),
        };
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tools: read", only.Summary);
        Assert.Equal(ActivityOutcome.Healthy, only.Outcome);
    }

    [Fact]
    public void GroupActivity_OutcomePrecedence()
    {
        // Active latest run without a receipt is in progress, not failed.
        var active = new FeedEntry[] { Row(1, "user", "go"), Row(2, "tool", "x", "failed: exec", true) };
        Assert.Equal(ActivityOutcome.InProgress, OverviewSummary.GroupActivity(active, true, 10)[0].Outcome);
        // Same rows settled without a receipt: failed, honestly.
        Assert.Equal(ActivityOutcome.Failed, OverviewSummary.GroupActivity(active, false, 10)[0].Outcome);
        // Needs-review receipt outranks a quiet cluster; healthy quiets.
        var review = new FeedEntry[] { Row(1, "user", "go"), Row(2, "receipt", "Settled with 1 problem", "needs review") };
        Assert.Equal(ActivityOutcome.NeedsReview, OverviewSummary.GroupActivity(review, false, 10)[0].Outcome);
        // A prompt with nothing after it yet has no recorded outcome.
        var bare = new FeedEntry[] { Row(1, "user", "go") };
        Assert.Equal(ActivityOutcome.Unknown, OverviewSummary.GroupActivity(bare, false, 10)[0].Outcome);
    }

    [Fact]
    public void GroupActivity_CapsGroupsAndDropsPrePromptRows()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "system", "history edge"),
            Row(2, "user", "one"),
            Row(3, "user", "two"),
            Row(4, "user", "three"),
        };
        var groups = OverviewSummary.GroupActivity(entries, false, 2);
        Assert.Equal(2, groups.Count);
        Assert.Equal(4, groups[0].Prompt.Seq);
        Assert.Equal(3, groups[1].Prompt.Seq);
        Assert.Equal(3, OverviewSummary.GroupCount(entries));
        Assert.Equal("No tool calls observed.", groups[0].Summary);
    }

    [Fact]
    public void GroupActivity_GroupsRepeatedToolsWithCounts()
    {
        // Five identical calls read once, never five repeated labels.
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "tool", "o1", "finished: powershell"),
            Row(3, "tool", "o2", "finished: powershell"),
            Row(4, "tool", "o3", "finished: powershell"),
            Row(5, "tool", "o4", "finished: powershell"),
            Row(6, "tool", "o5", "finished: powershell"),
            Row(7, "receipt", "Healthy", "healthy"),
        };
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tools: powershell ×5", only.Summary);
    }

    [Fact]
    public void GroupActivity_MarksFailedGroups()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "tool", "o1", "finished: read"),
            Row(3, "tool", "o2", "finished: read"),
            Row(4, "tool", "boom", "failed: read", true),
            Row(5, "tool", "bang", "failed: exec", true),
        };
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tools: read ×3 (1 failed) · exec failed", only.Summary);
        Assert.Equal(ActivityOutcome.Failed, only.Outcome);
    }

    [Fact]
    public void GroupActivity_RestoredRowsUseHeaderIdentity()
    {
        // Restored history carries no lifecycle notes: the header parser
        // (shared with the session map) names the tools and proves failure.
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "working\n[tool call: read]"),
            Row(3, "tool", "[tool result: read]\ncontent"),
            Row(4, "tool", "[tool exec failed]"),
        };
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tools: read · exec failed", only.Summary);
        Assert.Equal(ActivityOutcome.Failed, only.Outcome);
    }

    [Fact]
    public void GroupActivity_MarkersWithoutRowsAreIncomplete()
    {
        // Call markers with no recovered rows: incomplete, never "none".
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "a\n[tool call: read]\n[tool call: exec]"),
        };
        var only = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tool history incomplete — 2 calls noted, none recovered.", only.Summary);
        Assert.Equal(ActivityOutcome.Unknown, only.Outcome);
    }

    [Fact]
    public void GroupActivity_AbsenceStates()
    {
        // Settled live run: none occurred. Unrestored cluster: none observed.
        var settled = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "receipt", "Healthy", "healthy"),
        };
        Assert.Equal("No tool calls.", OverviewSummary.GroupActivity(settled, false, 10)[0].Summary);
        var restored = new FeedEntry[] { Row(1, "user", "go"), Row(2, "assistant", "plain answer") };
        Assert.Equal("No tool calls observed.", OverviewSummary.GroupActivity(restored, false, 10)[0].Summary);
    }

    [Fact]
    public void GroupActivity_RunningToolOnActiveRun()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "tool", string.Empty, "started: exec"),
        };
        var active = Assert.Single(OverviewSummary.GroupActivity(entries, true, 10));
        Assert.Equal("Tools: exec running", active.Summary);
        Assert.Equal(ActivityOutcome.InProgress, active.Outcome);
        // Same rows after the run ended without settling: incomplete.
        var stale = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tool history incomplete — starts without ends, none recovered.", stale.Summary);
    }

    [Fact]
    public void GroupActivity_ShellRowsCountAsTools()
    {
        // Bash bodies carry no provable name: counted honestly as shell.
        var entries = new FeedEntry[] { Row(1, "user", "go"), Row(2, "tool", "Ran `Get-Date`\n…") };
        Assert.Equal("Tools: shell", OverviewSummary.GroupActivity(entries, false, 10)[0].Summary);
    }
}
