using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>Context gauge contract: real percentages fill bottom-up and
/// clamped; unknown renders hollow, never a measured zero.</summary>
public sealed class ContextGaugeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.8, 0.008)]
    [InlineData(50, 0.5)]
    [InlineData(100, 1)]
    [InlineData(-4, 0)]
    [InlineData(140, 1)]
    public void FillFraction_ClampsToDisc(double percent, double expected)
    {
        Assert.Equal(expected, ContextGauge.FillFraction(percent), precision: 9);
    }

    [Fact]
    public void FillFraction_NaN_IsEmpty()
    {
        Assert.Equal(0, ContextGauge.FillFraction(double.NaN));
    }

    [Fact]
    public void GroupMargins_TurnAnchorsKeepAir_IntraRunJoins()
    {
        var assistant = new FeedEntry(1, "assistant", "text");
        var tool = new FeedEntry(2, "tool", "[tool result: read]\nout");
        // Inside one run: tight both sides.
        Assert.Equal(new Avalonia.Thickness(0, 1, 0, 1), ConversationViewItem.GroupMargins("assistant", tool, "assistant"));
        Assert.Equal(new Avalonia.Thickness(0, 1, 0, 1), ConversationViewItem.GroupMargins("tool", assistant, "tool"));
        // Turn anchors: full rhythm whatever the neighbours.
        var user = new FeedEntry(3, "user", "go");
        Assert.Equal(new Avalonia.Thickness(0, 4, 0, 4), ConversationViewItem.GroupMargins("assistant", user, "assistant"));
        var receipt = new FeedEntry(4, "receipt", "Healthy");
        Assert.Equal(new Avalonia.Thickness(0, 4, 0, 4), ConversationViewItem.GroupMargins("tool", receipt, null));
        // Edges: first row after a prompt keeps top air, last row before
        // a receipt keeps bottom air.
        Assert.Equal(new Avalonia.Thickness(0, 4, 0, 1), ConversationViewItem.GroupMargins("user", assistant, "tool"));
        Assert.Equal(new Avalonia.Thickness(0, 1, 0, 4), ConversationViewItem.GroupMargins("tool", tool, "receipt"));
        // Detail rows always tight, even at an edge.
        var detail = new FeedEntry(5, "system", "[detail abc] read args");
        Assert.Equal(new Avalonia.Thickness(0, 1, 0, 1), ConversationViewItem.GroupMargins(null, detail, null));
    }

    [Fact]
    public void DetailRows_ReadTight_OrdinaryRowsKeepRhythm()
    {
        var detail = new ConversationViewItem(1, "system", string.Empty, "[detail abc] read args: {}", "[detail abc] read args: {}", AgentX.App.FeedBlockKind.System, false, AgentX.App.AxTheme.MutedBrush);
        Assert.True(detail.IsDetailRow);
        Assert.Equal(new Avalonia.Thickness(0, 1, 0, 1), detail.RowMargin);
        var receipt = new ConversationViewItem(2, "receipt", string.Empty, "[result abc] ok", "[result abc] ok", AgentX.App.FeedBlockKind.Receipt, false, AgentX.App.AxTheme.MutedBrush);
        Assert.False(receipt.IsDetailRow);
        Assert.Equal(new Avalonia.Thickness(0, 4, 0, 4), receipt.RowMargin);
        var plain = new ConversationViewItem(3, "system", string.Empty, "note", "note", AgentX.App.FeedBlockKind.System, false, AgentX.App.AxTheme.MutedBrush);
        Assert.False(plain.IsDetailRow);
    }
}
