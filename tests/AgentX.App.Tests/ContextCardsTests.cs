using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Checkpoint A shell builders: card status lines and footer honesty.</summary>
public sealed class ContextCardsTests
{
    [Theory]
    [InlineData(true, false, "Working")]
    [InlineData(false, true, "Connecting")]
    [InlineData(false, false, "Idle")]
    [InlineData(true, true, "Working")]
    public void StateWord_BusyBeatsConnecting_BeatsIdle(bool busy, bool connecting, string expected)
    {
        Assert.Equal(expected, ContextCards.StateWordFor(busy, connecting));
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
        Assert.Equal("Idle", card.StatusLine);
    }

    [Fact]
    public void FooterText_FullValues_MatchesApprovedShape()
    {
        var (left, right) = ContextCards.FooterText("Agent-X", "foundation", "MiMo 2.6", "High", 18);

        Assert.Equal("Agent-X · foundation", left);
        Assert.Equal("MiMo 2.6 · High · Context 18%", right);
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
