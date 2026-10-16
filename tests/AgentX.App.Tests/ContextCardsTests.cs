using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Checkpoint A shell builders: card status lines and footer honesty.</summary>
public sealed class ContextCardsTests
{
    [Theory]
    [InlineData(true, false, false, "Working")]
    [InlineData(true, true, false, "Working")]
    [InlineData(false, true, false, "Connecting")]
    [InlineData(false, false, true, "Idle")]
    [InlineData(false, false, false, "Not connected")]
    [InlineData(true, false, true, "Working")]
    public void StateWord_DistinguishesConnectionStates(bool busy, bool connecting, bool connected, string expected)
    {
        Assert.Equal(expected, ContextCards.StateWordFor(busy, connecting, connected));
    }

    [Fact]
    public void DisconnectedWorker_IsNeverShownAsIdle()
    {
        var card = ContextCards.WorkerCard("w1", "Agent ONE", "proj", false, false, false, true);

        Assert.Equal("Not connected", card.StateWord);
        Assert.Equal("Not connected · proj", card.StatusLine);
    }

    [Theory]
    [InlineData(696, 1, 696)]
    [InlineData(696, 2, 344)]
    [InlineData(696, 6, 200)]
    [InlineData(0, 2, 200)]
    [InlineData(696, 0, 200)]
    public void CardWidth_BalancesAcrossViewport_FloorsAt200(double viewport, int count, double expected)
    {
        Assert.Equal(expected, ContextCards.CardWidth(viewport, count));
    }

    [Fact]
    public void NoticeState_WarningThenRoutine_Collapses()
    {
        var closed = new ContextCards.NoticeState(false, false, null);
        var alerted = ContextCards.NextNotice(closed, "Connection failed: timeout.");
        Assert.True(alerted.Open);

        var routine = ContextCards.NextNotice(alerted, "Main selected — press Connect.");
        Assert.False(routine.Open);
        Assert.Equal("Main selected — press Connect.", routine.LastText);
    }

    [Fact]
    public void NoticeState_ManualExpansion_SurvivesRoutine()
    {
        var closed = new ContextCards.NoticeState(false, false, null);
        var manual = ContextCards.ToggleNotice(closed);
        Assert.True(manual is { Open: true, Manual: true });

        var routine = ContextCards.NextNotice(manual, "Copied message #5.");
        Assert.True(routine.Open);
        Assert.True(routine.Manual);
    }

    [Fact]
    public void NoticeState_NewError_ReopensAfterManualClose()
    {
        var closed = new ContextCards.NoticeState(false, false, "old routine");
        var alerted = ContextCards.NextNotice(closed, "Connection failed: timeout.");
        var dismissed = ContextCards.ToggleNotice(alerted);
        Assert.False(dismissed.Open);
        Assert.False(dismissed.Manual);

        var again = ContextCards.NextNotice(dismissed, "Connection failed: refused.");
        Assert.True(again.Open);
    }

    [Fact]
    public void NoticeState_Empty_CollapsesUnchangedText_Keeps()
    {
        var open = new ContextCards.NoticeState(true, true, "Copied.");
        var cleared = ContextCards.NextNotice(open, string.Empty);
        Assert.False(cleared.Open);
        Assert.True(cleared.Manual);

        var steady = ContextCards.NextNotice(open, "Copied.");
        Assert.True(steady.Open);
    }

    [Fact]
    public void CardsEqual_IgnoresNothing_ComparesOrderAndState()
    {
        var a = ContextCards.WorkerCard("w1", "Agent ONE", "p", true, false, false, true);
        var b = ContextCards.WorkerCard("w1", "Agent ONE", "p", true, false, false, true);
        var moved = ContextCards.WorkerCard("w1", "Agent ONE", "p", true, false, false, false);

        var other = ContextCards.WorkerCard("w2", "Agent TWO", "q", false, false, false, false);

        Assert.True(ContextCards.CardsEqual([a], [b]));
        Assert.False(ContextCards.CardsEqual([a], [moved]));
        Assert.False(ContextCards.CardsEqual([a], [a, b]));
        Assert.False(ContextCards.CardsEqual([a, other], [other, a]));
    }

    [Theory]
    [InlineData("Connection failed: timeout — nothing connected.", true)]
    [InlineData("Workspace registry unreadable — registration disabled.", true)]
    [InlineData("Cannot create worker: access denied.", true)]
    [InlineData("Folder picker failed: cancelled.", true)]
    [InlineData("Main selected — press Connect.", false)]
    [InlineData("Copied message #5 (120 chars) byte-for-byte.", false)]
    [InlineData("Pi replied with 258 characters — healthy run.", false)]
    [InlineData("Streaming… 120 chars in 3 UI updates.", false)]
    [InlineData("Worker 'x' never connected — press Connect first.", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsAlert_FlagsWarningsNotRoutine(string? text, bool expected)
    {
        Assert.Equal(expected, ContextCards.IsAlert(text));
    }

    [Fact]
    public void BranchNeedsRefresh_NullOrStale_True_Fresh_False()
    {
        var now = DateTime.UtcNow;
        Assert.True(ContextCards.BranchNeedsRefresh(null, now));
        Assert.True(ContextCards.BranchNeedsRefresh(now - TimeSpan.FromMinutes(6), now));
        Assert.False(ContextCards.BranchNeedsRefresh(now - TimeSpan.FromMinutes(4), now));
        Assert.False(ContextCards.BranchNeedsRefresh(now, now));
    }

    [Fact]
    public void WorkerCard_StatusLine_ComposesStateAndDetail()
    {
        var card = ContextCards.WorkerCard("w1", "Agent ONE", "Workspace UI", true, true, false, true);

        Assert.True(card.IsWorker);
        Assert.True(card.IsSelected);
        Assert.Equal("Working · Workspace UI", card.StatusLine);
    }

    [Fact]
    public void WorkspaceCard_EmptyDetail_BareStateWord()
    {
        var card = ContextCards.WorkspaceCard("legacy", "Main", string.Empty, false, false, false, true);

        Assert.False(card.IsWorker);
        Assert.Equal("Not connected", card.StatusLine);
    }

    [Fact]
    public void FooterText_FullValues_MatchesApprovedShape()
    {
        var (left, right) = ContextCards.FooterText("Agent-X", "foundation", "MiMo 2.6", "High", 18.4);

        Assert.Equal("Agent-X · foundation", left);
        Assert.Equal("MiMo 2.6 · High · Context 18.4%", right);
    }

    [Fact]
    public void FooterText_Unknowns_ShowPlaceholders_NotInventions()
    {
        var (left, right) = ContextCards.FooterText("Main", null, null, null, null);

        Assert.Equal("Main", left);
        Assert.Equal("— · — · Context —", right);
        Assert.DoesNotContain("*", right, StringComparison.Ordinal);
    }

    [Fact]
    public void FooterText_BlankBranch_Omitted()
    {
        var (left, _) = ContextCards.FooterText("Main", "  ", "M", "High", 3.2);

        Assert.Equal("Main", left);
    }

    [Theory]
    [InlineData("foundation\n", "foundation")]
    [InlineData("  feature/x  ", "feature/x")]
    [InlineData("HEAD\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseGitBranch_TrimsAndRejectsDetached(string? output, string? expected)
    {
        Assert.Equal(expected, ContextCards.ParseGitBranch(output));
    }

    [Fact]
    public void IconData_NewKinds_ParseStructurally()
    {
        foreach (var kind in new[] { AxIconKind.Rabbit, AxIconKind.Search, AxIconKind.Sliders, AxIconKind.Grid, AxIconKind.Chat, AxIconKind.Branch })
        {
            var data = AxIcons.DataFor(kind);
            Assert.NotEmpty(data);
            Assert.StartsWith("M", data, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"\A[MmLlHhVvCcSsQqTtAaZz0-9eE.\+\-\s,]*\z", data);
        }
    }
}
