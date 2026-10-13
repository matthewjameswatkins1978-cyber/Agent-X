using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// B4 composer enablement contract: the context menu and the keyboard chords
/// share one policy — Send only for a valid prompt to a connected idle
/// worker, Stop only while a run is active. Deterministic, headless.
/// </summary>
public sealed class ComposerActionPolicyTests
{
    [Fact]
    public void CanSend_ConnectedIdleWithText()
    {
        Assert.True(ComposerActionPolicy.CanSend(true, false, "hello pi"));
    }

    [Fact]
    public void CanSend_RefusesBusy()
    {
        Assert.False(ComposerActionPolicy.CanSend(true, true, "hello pi"));
    }

    [Fact]
    public void CanSend_RefusesDisconnected()
    {
        Assert.False(ComposerActionPolicy.CanSend(false, false, "hello pi"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void CanSend_RefusesBlankPrompts(string? text)
    {
        Assert.False(ComposerActionPolicy.CanSend(true, false, text));
    }

    [Fact]
    public void CanSend_PreservesPromptBytes()
    {
        Assert.True(ComposerActionPolicy.CanSend(true, false, "  padded  "));
    }

    [Fact]
    public void CanStop_MirrorsBusy()
    {
        Assert.True(ComposerActionPolicy.CanStop(true));
        Assert.False(ComposerActionPolicy.CanStop(false));
    }

    [Fact]
    public void Policy_NeverSendsAndStopsTogether()
    {
        // A run is either active (stoppable) or idle (sendable): the menu
        // never offers to cancel another worker's run by accident.
        Assert.False(ComposerActionPolicy.CanSend(true, true, "x") && ComposerActionPolicy.CanStop(true));
        Assert.False(ComposerActionPolicy.CanSend(false, false, "x") && ComposerActionPolicy.CanStop(false));
    }
}
