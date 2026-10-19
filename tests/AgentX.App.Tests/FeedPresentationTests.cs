using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>Pass 2 presentation contract: transport details fold away from
/// ordinary conversation (Full Trace reveals them), the reader keeps their
/// scroll position, streaming rows carry a view-only caret, and only the
/// settled closing word gets the outline. All pure and UI-free.</summary>
public sealed class FeedPresentationTests
{
    private static FeedEntry Row(long seq, string role, string text, string? note = null, bool isError = false)
        => new(seq, role, text, Note: note, IsError: isError);

    [Fact]
    public void ShouldShow_DetailRowsFoldedUnlessFullTrace()
    {
        var detail = Row(1, "system", "[detail ab12] read args: path");
        var result = Row(2, "system", "[result ab12] Tool read: finished.");
        var progress = Row(3, "system", "[progress ab12] read: partial");
        foreach (var row in new[] { detail, result, progress })
        {
            Assert.False(ConversationViewItem.ShouldShow(row, showDiagnostics: false));
            Assert.True(ConversationViewItem.ShouldShow(row, showDiagnostics: true));
        }
    }

    [Fact]
    public void ShouldShow_OrdinaryRowsAlwaysVisible()
    {
        var rows = new[]
        {
            Row(1, "user", "hello"),
            Row(2, "assistant", "hi"),
            Row(3, "tool", "out", "finished: read"),
            Row(4, "receipt", "Healthy", "healthy"),
            Row(5, "system", "Run cancelled — kept."),
            Row(6, "system", "[context summary] older"),
        };
        foreach (var row in rows)
        {
            Assert.True(ConversationViewItem.ShouldShow(row, showDiagnostics: false));
            Assert.True(ConversationViewItem.ShouldShow(row, showDiagnostics: true));
        }
    }

    [Fact]
    public void ClosesReport_AssistantClosedByReceiptInSameCluster()
    {
        // Live turns stream into the row opened BEFORE the tools run, so
        // adjacency is not the test — cluster membership is.
        var run = new[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "working"),
            Row(3, "tool", "out", "finished: read"),
            Row(4, "system", "[detail ab12] read args: x"),
            Row(5, "receipt", "Healthy", "healthy"),
        };
        Assert.True(ConversationViewItem.ClosesReport(run, 1, showDiagnostics: false));
        Assert.False(ConversationViewItem.ClosesReport(run, 0, showDiagnostics: false));
        Assert.False(ConversationViewItem.ClosesReport(run, 2, showDiagnostics: false));
    }

    [Fact]
    public void ClosesReport_NextPromptOrCancelEndsCluster()
    {
        var two = new[]
        {
            Row(1, "user", "first"),
            Row(2, "assistant", "done"),
            Row(3, "receipt", "Healthy", "healthy"),
            Row(4, "user", "second"),
            Row(5, "assistant", "partial"),
        };
        Assert.True(ConversationViewItem.ClosesReport(two, 1, showDiagnostics: false));
        Assert.False(ConversationViewItem.ClosesReport(two, 4, showDiagnostics: false));
        var cancelled = new[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "partial"),
            Row(3, "system", "Run cancelled — kept."),
        };
        Assert.False(ConversationViewItem.ClosesReport(cancelled, 1, showDiagnostics: false));
    }

    [Fact]
    public void StreamingDisplay_CaretOnlyWhileStreaming()
    {
        Assert.Equal("hi ▍", ConversationViewItem.StreamingDisplay("hi", true));
        Assert.Equal("hi", ConversationViewItem.StreamingDisplay("hi", false));
        Assert.Equal(" ▍", ConversationViewItem.StreamingDisplay(string.Empty, true));
    }

    [Fact]
    public void FinalReport_FoldedDetailDoesNotBreakAdjacency()
    {
        // A detail row between the answer and its receipt is folded, so the
        // answer still closes the run: the check runs over visible rows.
        var visible = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "done"),
            Row(3, "receipt", "Healthy", "healthy"),
        };
        Assert.True(ConversationViewItem.ClosesReport(visible, 1, showDiagnostics: false));
    }

    [Fact]
    public void TimelineAndMap_StayFreeOfTransportRows()
    {
        // Transport detail lives in the feed as evidence but must not leak
        // into the timeline or the session map as ordinary activity.
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var turn = feed.BeginAssistantTurn();
        feed.AppendDelta(turn, "working");
        feed.AddToolStart("read", "src/Foo.cs", "src/Foo.cs", isMutating: false, callId: "c1");
        feed.AddToolEnd("read", isError: false, callId: "c1");
        feed.AddSystemNotice("[detail ab12] read args: path=src/Foo.cs");
        feed.AddSystemNotice("[result ab12] Tool read: finished in 12 ms.");
        feed.EndAssistantTurn(turn, new PiRunReceipt(true, false, 0, 7, 2));
        var entries = feed.Snapshot();
        var map = feed.BuildMap();
        Assert.Equal(0, map.ErrorCount);
        Assert.DoesNotContain(map.Milestones, m => m.Title.Contains("[detail", StringComparison.Ordinal));
        Assert.DoesNotContain(map.Milestones, m => m.Title.Contains("[result", StringComparison.Ordinal));
        var group = Assert.Single(OverviewSummary.GroupActivity(entries, false, 10));
        Assert.Equal("Tools: read", group.Summary);
        Assert.Equal(ActivityOutcome.Healthy, group.Outcome);
    }

    [Fact]
    public void StudioOutcome_ReadsAuthoritativeMarkersOnly()
    {
        Assert.Null(ConversationViewItem.StudioOutcome("read", false, "content"));
        Assert.Null(ConversationViewItem.StudioOutcome("studio", false, "content"));
        Assert.Null(ConversationViewItem.StudioOutcome(string.Empty, false, "content"));
        var ok = ConversationViewItem.StudioOutcome("studio_checkpoint", false, "RESULT: WORKING\nObjective met so far.");
        Assert.NotNull(ok);
        Assert.Equal(StudioState.Working, ok.State);
        var done = ConversationViewItem.StudioOutcome("STUDIO_SHIP", false, "RESULT: COMPLETE\nAll green.");
        Assert.NotNull(done);
        Assert.Equal(StudioState.Complete, done.State);
        var partial = ConversationViewItem.StudioOutcome("studio_complete", false, "RESULT: PARTIAL\nTwo of three checks.");
        Assert.NotNull(partial);
        Assert.Equal(StudioState.NeedsHuman, partial.State);
        var failed = ConversationViewItem.StudioOutcome("studio_complete", false, "RESULT: FAILED\nBroken.");
        Assert.NotNull(failed);
        Assert.Equal(StudioState.Failed, failed.State);
        var blocked = ConversationViewItem.StudioOutcome("studio_wait_for_review", false, "STUDIO MISSION job-1 [WAITING_REVIEW]\nHold.");
        Assert.NotNull(blocked);
        Assert.Equal(StudioState.Blocked, blocked.State);
        var human = ConversationViewItem.StudioOutcome("studio_blocked", false, "STUDIO MISSION job-2 [NEEDS_HUMAN]\nNeed a decision.");
        Assert.NotNull(human);
        Assert.Equal(StudioState.NeedsHuman, human.State);
        // Prose mentions are not evidence: an unmarked success is Working.
        var prose = ConversationViewItem.StudioOutcome("studio_checkpoint", false, "A NEEDS_HUMAN packet stays WORKING until reviewed.");
        Assert.NotNull(prose);
        Assert.Equal(StudioState.Working, prose.State);
        var json = ConversationViewItem.StudioOutcome("studio_status", false, "{\"status\": \"FAILED\", \"n\": 1}");
        Assert.NotNull(json);
        Assert.Equal(StudioState.Failed, json.State);
        // Tool failure beats result text.
        var err = ConversationViewItem.StudioOutcome("studio_checkpoint", true, "RESULT: COMPLETE");
        Assert.NotNull(err);
        Assert.Equal(StudioState.Failed, err.State);
    }

    [Fact]
    public void StudioOutcome_SummarizesFirstLine()
    {
        Assert.Equal("no output recorded", ConversationViewItem.StudioSummaryOf("  \n "));
        Assert.Equal("Hello.", ConversationViewItem.StudioSummaryOf("\nHello.\nWorld."));
        var long_ = new string('x', 200);
        Assert.EndsWith("…", ConversationViewItem.StudioSummaryOf(long_));
    }

    [Fact]
    public void StudioPresentation_MapsStatesToBrushes()
    {
        Assert.Same(AxTheme.CyanBrush, ConversationViewItem.StudioEdgeFor(StudioState.Working));
        Assert.Same(AxTheme.GreenBrush, ConversationViewItem.StudioEdgeFor(StudioState.Complete));
        Assert.Same(AxTheme.YellowBrush, ConversationViewItem.StudioEdgeFor(StudioState.NeedsHuman));
        Assert.Same(AxTheme.BlueBrush, ConversationViewItem.StudioEdgeFor(StudioState.Blocked));
        Assert.Same(AxTheme.RedBrush, ConversationViewItem.StudioEdgeFor(StudioState.Failed));
        Assert.Equal("Needs human", ConversationViewItem.DescribeStudioState(StudioState.NeedsHuman));
    }

    [Fact]
    public void StudioTool_SuppressesCardAndFlowLines()
    {
        var item = ToolItem("finished: studio_checkpoint", false);
        item.IsStudioTool = true;
        Assert.False(item.IsCardTool);
        Assert.False(item.IsFlowTool);
        Assert.False(item.IsStudioSummary && item.IsStudioExpanded);
        item.IsExpanded = true;
        Assert.True(item.IsStudioExpanded);
        Assert.False(item.IsStudioSummary);
    }

    [Fact]
    public void FindNextMatch_StepsWrapsAndReachesFoldedText()
    {
        var entries = new FeedEntry[]
        {
            Row(1, "user", "go"),
            Row(2, "assistant", "working"),
            Row(3, "system", "[detail ab12] read args: path=src/Foo.cs"),
            Row(4, "receipt", "Healthy", "healthy"),
        };
        // Full-text match inside a folded detail row, from the top.
        Assert.Equal(2, MainWindow.FindNextMatch(entries, anchorSeq: 1, query: "src/Foo", direction: 1));
        // Wraps past the end back to the detail row.
        Assert.Equal(2, MainWindow.FindNextMatch(entries, anchorSeq: 4, query: "src/Foo", direction: 1));
        // Backward from the receipt lands on the detail row.
        Assert.Equal(2, MainWindow.FindNextMatch(entries, anchorSeq: 4, query: "src/Foo", direction: -1));
        // No anchor: forward starts at the first row, backward at the last.
        Assert.Equal(0, MainWindow.FindNextMatch(entries, anchorSeq: null, query: "go", direction: 1));
        Assert.Equal(3, MainWindow.FindNextMatch(entries, anchorSeq: null, query: "Healthy", direction: -1));
        // Unknown anchor behaves like no anchor; empty query and no match fail closed.
        Assert.Equal(0, MainWindow.FindNextMatch(entries, anchorSeq: 999, query: "go", direction: 1));
        Assert.Equal(-1, MainWindow.FindNextMatch(entries, anchorSeq: 1, query: "   ", direction: 1));
        Assert.Equal(-1, MainWindow.FindNextMatch(entries, anchorSeq: 1, query: "absent", direction: 1));
        Assert.Equal(-1, MainWindow.FindNextMatch(Array.Empty<FeedEntry>(), anchorSeq: null, query: "go", direction: 1));
    }

    [Fact]
    public void LongFeed_MapsHundredsOfToolRows()
    {
        // Hundreds of tool calls plus Studio rows and transport detail:
        // the view mapping stays exact and transport stays out of sight.
        var feed = new ConversationFeed();
        for (var i = 0; i < 100; i++)
        {
            feed.AddUser($"task {i}");
            var turn = feed.BeginAssistantTurn();
            feed.AppendDelta(turn, $"answer {i}");
            foreach (var tool in new[] { "read", "edit", "powershell" })
            {
                feed.AddToolStart(tool, $"op {i}", null, isMutating: false, callId: $"c{i}-{tool}");
                feed.AddToolEnd(tool, isError: false, callId: $"c{i}-{tool}");
            }

            feed.AddToolStart("studio_checkpoint", $"checkpoint {i}", null, isMutating: false, callId: $"sc-{i}");
            feed.AddToolEnd("studio_checkpoint", isError: false, callId: $"sc-{i}");
            feed.AddSystemNotice($"[detail ab{i:x}] read args: path=src/Foo.cs");
            feed.AddSystemNotice($"[result ab{i:x}] Tool read: finished in 12 ms.");
            feed.EndAssistantTurn(turn, new PiRunReceipt(true, false, 0, 8, 2));
        }

        var entries = feed.Snapshot();
        Assert.True(entries.Count > 700);
        var visible = entries.Where(e => ConversationViewItem.ShouldShow(e, showDiagnostics: false)).ToList();
        Assert.Equal(entries.Count - 200, visible.Count);
        Assert.DoesNotContain(visible, e => e.Text.StartsWith("[detail ", StringComparison.Ordinal));
        Assert.DoesNotContain(visible, e => e.Text.StartsWith("[result ", StringComparison.Ordinal));
        // Studio rows flow through the same tool mapping without errors.
        Assert.Equal(100, visible.Count(e => e.Role == "tool" && (e.Note ?? string.Empty).Contains("studio_checkpoint", StringComparison.Ordinal)));
        var map = feed.BuildMap();
        Assert.Equal(0, map.ErrorCount);
    }

    private static ConversationViewItem ToolItem(string note, bool isError, string text = "out")
        => new(1, "tool", "[Tool #1] " + note, text, text, FeedBlockKind.Tool, false, AxTheme.GreenBrush);

    [Fact]
    public void FlowMode_SlimLinesReplaceCards()
    {
        var item = ToolItem("finished: read", false);
        Assert.True(item.IsCardTool);
        Assert.False(item.IsFlowTool);
        item.FlowMode = true;
        Assert.True(item.IsFlowTool);
        Assert.False(item.IsCardTool);
        Assert.False(item.IsFlowExpanded);
        item.IsExpanded = true;
        Assert.True(item.IsFlowExpanded);
    }

    [Fact]
    public void FlowDot_MatchesLifecycle()
    {
        var failed = new FeedEntry(1, "tool", "boom", Note: "failed: exec", IsError: true);
        Assert.Same(AxTheme.RedBrush, MainWindow.FlowDotFor(failed));
        var pending = new FeedEntry(2, "tool", string.Empty, Note: "started: exec");
        Assert.Same(AxTheme.CyanBrush, MainWindow.FlowDotFor(pending));
        var done = new FeedEntry(3, "tool", "ok", Note: "finished: exec");
        Assert.Same(AxTheme.GreenBrush, MainWindow.FlowDotFor(done));
        var restoredFail = new FeedEntry(4, "tool", "[tool exec failed]\nboom");
        Assert.Same(AxTheme.RedBrush, MainWindow.FlowDotFor(restoredFail));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void FollowEngaged_PrefAndLatch(bool pref, bool latched, bool expected)
    {
        // The contract: preference is the master switch, a mouse click in
        // the feed is the only unlatch, Go/Enter and Jump re-latch.
        Assert.Equal(expected, MainWindow.FollowEngaged(pref, latched));
    }
}
