using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Flow v2 chronological narration: assistant segments split at Pi message
/// boundaries, tool pairing survives splits, and the final authoritative
/// text never duplicates closed segments.
/// </summary>
public sealed class RunSegmentationTests
{
    private static JsonDocument Doc(string json) => JsonDocument.Parse(json);

    [Fact]
    public void SegmentBoundary_MatchesTurnAndAssistantMessagesOnly()
    {
        Assert.True(PiConversation.IsSegmentBoundary(Doc("""{"type":"turn_start"}""")));
        Assert.True(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_start","message":{"role":"assistant","content":[]}}""")));
        Assert.True(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_start","message":{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"read"}]}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_start","message":{"role":"user","content":[]}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_start","message":{"role":"toolResult","toolCallId":"c1"}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_start","message":{"role":"system","content":[]}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc("""{"type":"message_start"}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc("""{"type":"agent_start"}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc("""{"type":"turn_end"}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"hi"}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"tool_execution_start","toolName":"read"}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc(
            """{"type":"message_end","message":{"stopReason":"toolUse"}}""")));
        Assert.False(PiConversation.IsSegmentBoundary(Doc("""{}""")));
    }

    [Fact]
    public void EndAssistantSegment_ClosesRowWithoutSettling()
    {
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "partial");
        Assert.True(feed.EndAssistantSegment(seq));
        Assert.False(feed.EndAssistantSegment(seq));
        Assert.False(feed.EndAssistantSegment(999_999));
        var row = feed.Snapshot().Single();
        Assert.False(row.IsStreaming);
        Assert.Equal("partial", row.Text);
        Assert.DoesNotContain(feed.Snapshot(), e => e.Role == "receipt");
    }

    [Fact]
    public void Split_PreservesToolPairingAndOrder()
    {
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var first = feed.BeginAssistantTurn();
        feed.AppendDelta(first, "first. ");
        Assert.True(feed.EndAssistantSegment(first));
        var start = feed.AddToolStart("read", "src/F.cs", "src/F.cs", isMutating: false, callId: "c1");
        Assert.Equal(start, feed.AddToolEnd("read", isError: false, callId: "c1"));
        var second = feed.BeginAssistantTurn();
        feed.AppendDelta(second, "second.");
        feed.EndAssistantTurn(second, new PiRunReceipt(true, false, 0, 7, 2));

        var rows = feed.Snapshot();
        Assert.Equal(new[] { "user", "assistant", "tool", "assistant", "receipt" },
            rows.Select(e => e.Role));
        Assert.Equal("first. ", rows[1].Text);
        Assert.Equal("second.", rows[3].Text);
        Assert.False(rows[1].IsStreaming);
        Assert.Contains("finished", rows[2].Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileFinalText_StripsClosedSegmentsExactly()
    {
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var first = feed.BeginAssistantTurn();
        feed.AppendDelta(first, "AB");
        Assert.True(feed.EndAssistantSegment(first));
        var second = feed.BeginAssistantTurn();
        feed.AppendDelta(second, "C");
        feed.ReconcileFinalText(second, "ABC", ["AB"]);
        Assert.Equal("C", feed.GetEntryText(second));
    }

    [Fact]
    public void ReconcileFinalText_UnsegmentedReplacesWhole()
    {
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "chunk");
        feed.ReconcileFinalText(seq, "authoritative full", []);
        Assert.Equal("authoritative full", feed.GetEntryText(seq));
    }

    [Fact]
    public void ReconcileFinalText_MismatchKeepsFullText()
    {
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "C");
        feed.ReconcileFinalText(seq, "ABC", ["XX"]);
        Assert.Equal("ABC", feed.GetEntryText(seq));
    }

    [Fact]
    public void ReconcileFinalText_IgnoresUnknownOrSettledRows()
    {
        var feed = new ConversationFeed();
        feed.AddUser("go");
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "text");
        feed.ReconcileFinalText(999_999, "other", []);
        feed.EndAssistantTurn(seq, null);
        feed.ReconcileFinalText(seq, "other", []);
        Assert.Equal("text", feed.GetEntryText(seq));
    }
}
