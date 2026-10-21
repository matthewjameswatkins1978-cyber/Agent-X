using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Fixing Pass 1 acceptance: trustworthy run settlement. A failed tool
/// call the agent corrects must not imply a failed task; an empty settle
/// is neutral; genuine unresolved errors still demand review; history is
/// never rewritten.
/// </summary>
public sealed class RunSettlementTests
{
    private static void Track(RunOutcomeTracker tracker, string line)
    {
        using var record = JsonDocument.Parse(line);
        using var copy = JsonDocument.Parse(record.RootElement.GetRawText());
        tracker.Add(new PiRpcEventArgs(copy, line));
    }

    [Fact]
    public void Tracker_FailedThenSuccess_Recovered()
    {
        var tracker = new RunOutcomeTracker();
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c1","toolName":"edit","isError":true}""");
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c2","toolName":"edit","isError":false}""");
        Assert.Equal(1, tracker.ObservedErrors);
        Assert.Equal(0, tracker.UnresolvedErrors);
    }

    [Fact]
    public void Tracker_FailureWithNoSuccess_Unresolved()
    {
        var tracker = new RunOutcomeTracker();
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c1","toolName":"bash","isError":true}""");
        Assert.Equal(1, tracker.ObservedErrors);
        Assert.Equal(1, tracker.UnresolvedErrors);
    }

    [Fact]
    public void Tracker_LateFailureAfterSuccess_Unresolved()
    {
        var tracker = new RunOutcomeTracker();
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c1","toolName":"read","isError":true}""");
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c2","toolName":"read","isError":false}""");
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c3","toolName":"exec","isError":true}""");
        Assert.Equal(2, tracker.ObservedErrors);
        Assert.Equal(1, tracker.UnresolvedErrors);
    }

    [Fact]
    public void Tracker_HardError_NeverRecoversViaToolSuccess()
    {
        var tracker = new RunOutcomeTracker();
        Track(tracker, """{"type":"extension_error","extensionPath":"x","event":"y","error":"z"}""");
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c1","toolName":"read","isError":false}""");
        Assert.Equal(1, tracker.ObservedErrors);
        Assert.Equal(1, tracker.UnresolvedErrors);
    }

    [Fact]
    public void Tracker_RetryExhaustion_StaysUnresolved()
    {
        var tracker = new RunOutcomeTracker();
        Track(tracker, """{"type":"tool_execution_end","toolCallId":"c1","toolName":"bash","isError":true}""");
        Track(tracker, """{"type":"auto_retry_end","success":false}""");
        Assert.Equal(2, tracker.ObservedErrors);
        Assert.Equal(2, tracker.UnresolvedErrors);
    }

    [Fact]
    public void Receipt_RecoveredRun_NoReviewDemanded()
    {
        var receipt = new PiRunReceipt(Settled: true, Aborted: false, ErrorCount: 2, AssistantChars: 16, MessageCount: 4)
        {
            UnresolvedErrorCount = 0,
        };
        Assert.True(receipt.RunHealthy);
        Assert.Contains("recovered", receipt.Display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("review before trusting", receipt.Display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verif", receipt.Display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Receipt_UnresolvedRun_DemandsReview()
    {
        var receipt = new PiRunReceipt(Settled: true, Aborted: false, ErrorCount: 2, AssistantChars: 16, MessageCount: 4)
        {
            UnresolvedErrorCount = 1,
        };
        Assert.False(receipt.RunHealthy);
        Assert.Contains("1 problem (2 observed)", receipt.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_DirectConstruction_KeepsAllUnresolvedMeaning()
    {
        // Callers that count failures without a tracker (and all existing
        // tests) keep the historical semantics: observed == unresolved.
        var receipt = new PiRunReceipt(true, false, 2, 5, 2);
        Assert.Equal(2, receipt.UnresolvedErrorCount);
        Assert.False(receipt.RunHealthy);
        Assert.Contains("2 problems", receipt.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Receipt_EmptyReply_IsNeutralNotHealthy()
    {
        var receipt = new PiRunReceipt(Settled: true, Aborted: false, ErrorCount: 0, AssistantChars: 0, MessageCount: 3);
        Assert.False(receipt.HasReply);
        Assert.False(receipt.RunHealthy);
        Assert.Contains("no reply text", receipt.Display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Healthy", receipt.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Feed_RecoveredReceipt_StaysHealthyRow()
    {
        var feed = new ConversationFeed();
        feed.AddUser("fix the type error");
        var turn = feed.BeginAssistantTurn();
        feed.AppendDelta(turn, "corrected answer");
        feed.AddToolStart("edit", "src/Type.cs", "src/Type.cs", isMutating: true, callId: "c1");
        feed.AddToolEnd("edit", isError: true, callId: "c1");
        feed.AddToolStart("edit", "src/Type.cs", "src/Type.cs", isMutating: true, callId: "c2");
        feed.AddToolEnd("edit", isError: false, callId: "c2");
        var receipt = new PiRunReceipt(true, false, 1, 16, 4) { UnresolvedErrorCount = 0 };
        feed.EndAssistantTurn(turn, receipt);

        var row = feed.Snapshot().Last(e => e.Role == "receipt");
        Assert.Equal("healthy", row.Note);
        Assert.False(row.IsError);
        // Evidence preserved: the failure is still in the feed and map.
        Assert.Equal(1, feed.BuildMap().ErrorCount);
    }

    [Fact]
    public void Feed_EmptyReceipt_IsNeutralRow()
    {
        var feed = new ConversationFeed();
        feed.AddUser("hello?");
        var turn = feed.BeginAssistantTurn();
        feed.EndAssistantTurn(turn, new PiRunReceipt(true, false, 0, 0, 2));

        var row = feed.Snapshot().Last(e => e.Role == "receipt");
        Assert.Equal("no reply", row.Note);
        Assert.False(row.IsError);
    }

    [Fact]
    public void Feed_UnresolvedReceipt_NeedsReviewRow()
    {
        var feed = new ConversationFeed();
        feed.AddUser("do it");
        var turn = feed.BeginAssistantTurn();
        feed.AppendDelta(turn, "tried");
        feed.EndAssistantTurn(turn, new PiRunReceipt(true, false, 1, 5, 2) { UnresolvedErrorCount = 1 });

        var row = feed.Snapshot().Last(e => e.Role == "receipt");
        Assert.Equal("needs review", row.Note);
        Assert.False(row.IsError);
    }

    [Fact]
    public void Feed_AbortedReceipt_IsFailedRow()
    {
        var feed = new ConversationFeed();
        feed.AddUser("do it");
        var turn = feed.BeginAssistantTurn();
        feed.EndAssistantTurn(turn, new PiRunReceipt(true, true, 0, 0, 2));

        var failed = feed.Snapshot().Last(e => e.Role == "receipt");
        Assert.Equal("needs review", failed.Note);
        Assert.True(failed.IsError);
    }

    [Fact]
    public void Feed_CancelNotice_CarriesCancelledNote()
    {
        var feed = new ConversationFeed();
        feed.AddUser("second");
        var partial = feed.BeginAssistantTurn();
        feed.AppendDelta(partial, "partial");
        feed.EndAssistantTurn(partial, null);
        feed.AddCancelNotice("Run cancelled — partial streamed text above is kept as-is.");

        var notice = feed.Snapshot().Last();
        Assert.Equal("cancelled", notice.Note);
        Assert.False(notice.IsError);
    }

    [Fact]
    public void Feed_RestoredHistory_PreservesFailuresWithoutReceipt()
    {
        var feed = new ConversationFeed();
        feed.LoadHistory(new[]
        {
            new ConversationMessage("user", "go"),
            new ConversationMessage("assistant", "working"),
            new ConversationMessage("tool", "[tool exec failed]\nboom", CallId: "c9", IsError: true),
        });
        var map = feed.BuildMap();
        Assert.Equal(1, map.ErrorCount);
        Assert.Null(map.LastReceipt);
    }
}
