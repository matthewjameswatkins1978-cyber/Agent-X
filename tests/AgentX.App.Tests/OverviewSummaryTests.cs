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
    public void Attention_Errors_OutrankDisconnect()
    {
        var state = OverviewSummary.AttentionFor(false, false, [], 3);
        Assert.Equal(AttentionLevel.Warning, state.Level);
        Assert.Contains("3 error events", state.Text, StringComparison.Ordinal);
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
    public void Attention_HealthyReceipt_StaysQuiet()
    {
        var entries = new[] { Row(1, "receipt", "Healthy", "healthy") };
        var state = OverviewSummary.AttentionFor(true, false, entries, 0);
        Assert.Equal(AttentionLevel.Ok, state.Level);
    }

    [Fact]
    public void RecentActivity_KeepsMeaningfulSkipsRaw()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "do the thing"),
            Row(2, "tool", "started", "started: read"),
            Row(3, "system", "[detail call_x] Run read: args"),
            Row(4, "system", "[result call_x] Tool read: ok."),
            Row(5, "tool", "output", "finished: read"),
            Row(6, "tool", "boom", "failed: exec", true),
            Row(7, "receipt", "Healthy", "healthy"),
            Row(8, "system", "Connection to Pi lost", isError: true),
            Row(9, "assistant", "streaming…"),
        };
        var activity = OverviewSummary.RecentActivity(entries);
        Assert.Equal([8, 7, 6, 5, 1], activity.Select(e => e.Seq).ToArray());
    }

    [Fact]
    public void Summarize_LabelsRowsPlainly()
    {
        Assert.StartsWith("Asked:", OverviewSummary.Summarize(Row(1, "user", "hello there")).Label, StringComparison.Ordinal);
        Assert.Equal("read finished", OverviewSummary.Summarize(Row(2, "tool", "x", "finished: read")).Label);
        Assert.Equal("exec failed", OverviewSummary.Summarize(Row(3, "tool", "x", "failed: exec", true)).Label);
    }
}
