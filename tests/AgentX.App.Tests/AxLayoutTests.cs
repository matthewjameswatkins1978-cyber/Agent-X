using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Shared-geometry invariants: the shell's datum lines and spacing
/// tokens agree with each other, so alignment is structural, not luck.</summary>
public sealed class AxLayoutTests
{
    [Fact]
    public void Margins_AreSymmetricHorizontally()
    {
        Assert.Equal(AxLayout.MarginLeft, AxLayout.MarginRight);
    }

    [Fact]
    public void Rail_CentresIconsExactly()
    {
        // 40px button centred in the 48px rail leaves equal 4px flanks;
        // the 18px icon sits centred in the button with equal insets.
        Assert.Equal(4, AxLayout.RailWidth - AxLayout.NavButton - 4);
        Assert.Equal(11, AxLayout.NavIconInset);
        Assert.Equal(24, AxLayout.RailCentreX);
    }

    [Fact]
    public void ContentLeft_SumsMarginRailAndGutter()
    {
        Assert.Equal(68, AxLayout.ContentLeft);
    }

    [Fact]
    public void Spacing_FollowsTheGrid()
    {
        Assert.Equal(0, AxLayout.CardGap % AxLayout.Grid);
        Assert.Equal(0, AxLayout.ContentGutter % AxLayout.Grid);
        Assert.Equal(0, AxLayout.ComposerPadding % AxLayout.Grid);
    }
}
