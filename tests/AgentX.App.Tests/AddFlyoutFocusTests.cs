using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Slice 2 final: flyout focus follows the visible mode. Headless routing only.</summary>
public sealed class AddFlyoutFocusTests
{
    [Fact]
    public void ChoiceMode_FocusesExisting()
        => Assert.Equal(AddFlyoutFocus.Target.Existing,
            AddFlyoutFocus.Pick(choiceVisible: true, openVisible: false, quickVisible: false));

    [Fact]
    public void ConfirmMode_FocusesOpen()
        => Assert.Equal(AddFlyoutFocus.Target.Open,
            AddFlyoutFocus.Pick(choiceVisible: false, openVisible: true, quickVisible: false));

    [Fact]
    public void QuickMode_FocusesName_EvenWithCancelRow()
        => Assert.Equal(AddFlyoutFocus.Target.Name,
            AddFlyoutFocus.Pick(choiceVisible: false, openVisible: false, quickVisible: true));

    [Fact]
    public void QuickBeatsStaleOpen_NeverInvisible()
        => Assert.Equal(AddFlyoutFocus.Target.Name,
            AddFlyoutFocus.Pick(choiceVisible: false, openVisible: true, quickVisible: true));

    [Fact]
    public void NothingVisible_FallsBackToExisting()
        => Assert.Equal(AddFlyoutFocus.Target.Existing,
            AddFlyoutFocus.Pick(choiceVisible: false, openVisible: false, quickVisible: false));
}
