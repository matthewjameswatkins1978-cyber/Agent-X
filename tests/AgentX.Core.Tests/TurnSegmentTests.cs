using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class TurnSegmentTests
{
    [Fact]
    public void SplitAfterToolInterleave_NewRowCarriesOn()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        Assert.Equal(s1, feed.AppendDelta(s1, "aaa"));
        feed.AddToolStart("read", "read p", "p", false, "c1");
        feed.AddToolEnd("read", false, "c1");
        var s2 = feed.AppendDelta(s1, "bbb");
        Assert.NotEqual(s1, s2);
        var rows = feed.Snapshot();
        var first = Assert.Single(rows, r => r.Seq == s1);
        Assert.Equal("aaa", first.Text);
        Assert.False(first.IsStreaming);
        var second = Assert.Single(rows, r => r.Seq == s2);
        Assert.Equal("bbb", second.Text);
        Assert.True(second.IsStreaming);
        var list = rows.ToList();
        Assert.True(list.FindIndex(r => r.Seq == s2) > list.FindIndex(r => r.Role == "tool"));
    }

    [Fact]
    public void NoSplitWithoutTools_SameRow()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        Assert.Equal(s1, feed.AppendDelta(s1, "aaa"));
        Assert.Equal(s1, feed.AppendDelta(s1, "bbb"));
        var row = Assert.Single(feed.Snapshot(), r => r.Seq == s1);
        Assert.Equal("aaabbb", row.Text);
    }

    [Fact]
    public void NoticeDoesNotSplit()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aaa");
        feed.AddSystemNotice("reconnected");
        Assert.Equal(s1, feed.AppendDelta(s1, "bbb"));
        var row = Assert.Single(feed.Snapshot(), r => r.Seq == s1);
        Assert.Equal("aaabbb", row.Text);
    }

    [Fact]
    public void AppendToFrozenRow_NoOp()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aaa");
        feed.AddToolStart("read", "read p", "p", false, "c1");
        feed.AddToolEnd("read", false, "c1");
        var s2 = feed.AppendDelta(s1, "bbb");
        var count = feed.Snapshot().Count;
        Assert.Equal(s1, feed.AppendDelta(s1, "ccc"));
        Assert.Equal(count, feed.Snapshot().Count);
        Assert.Equal("bbb", Assert.Single(feed.Snapshot(), r => r.Seq == s2).Text);
    }

    [Fact]
    public void ReconcileDistributesRemainder_StaleSeqWorks()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aaa");
        feed.AddToolStart("read", "read p", "p", false, "c1");
        feed.AddToolEnd("read", false, "c1");
        feed.AppendDelta(s1, "b");
        feed.ReconcileStreamingText(s1, "aaabbb");
        Assert.Equal("aaa", Assert.Single(feed.Snapshot(), r => r.Seq == s1).Text);
        var live = feed.Snapshot().Last(r => r.Role == "assistant");
        Assert.Equal("bbb", live.Text);
    }

    [Fact]
    public void ReconcileNonPrefix_NoOp()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aaa");
        feed.AddToolStart("read", "read p", "p", false, "c1");
        feed.AddToolEnd("read", false, "c1");
        var s2 = feed.AppendDelta(s1, "b");
        feed.ReconcileStreamingText(s2, "zzz");
        Assert.Equal("aaa", Assert.Single(feed.Snapshot(), r => r.Seq == s1).Text);
        Assert.Equal("b", Assert.Single(feed.Snapshot(), r => r.Seq == s2).Text);
    }

    [Fact]
    public void ReconcileSingleRow_ReplacesWhole()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aa");
        feed.ReconcileStreamingText(s1, "aaabbb");
        Assert.Equal("aaabbb", Assert.Single(feed.Snapshot(), r => r.Seq == s1).Text);
    }

    [Fact]
    public void EndLatestSettlesTurn()
    {
        var feed = new ConversationFeed();
        var s1 = feed.BeginAssistantTurn();
        feed.AppendDelta(s1, "aaa");
        feed.AddToolStart("read", "read p", "p", false, "c1");
        feed.AddToolEnd("read", false, "c1");
        var s2 = feed.AppendDelta(s1, "bbb");
        feed.EndAssistantTurn(s2, null);
        Assert.All(feed.Snapshot().Where(r => r.Role == "assistant"), r => Assert.False(r.IsStreaming));
    }
}
