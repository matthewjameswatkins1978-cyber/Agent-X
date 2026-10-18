using AgentX.App;
using Avalonia.Input;

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
    public void ShouldClearComposer_ClearsOnlyUntouchedBox()
    {
        Assert.True(ComposerActionPolicy.ShouldClearComposer("hello pi", "hello pi"));
        Assert.False(ComposerActionPolicy.ShouldClearComposer("hello pi + more", "hello pi"));
        Assert.False(ComposerActionPolicy.ShouldClearComposer(string.Empty, "hello pi"));
        Assert.False(ComposerActionPolicy.ShouldClearComposer(null, "hello pi"));
    }

    [Fact]
    public void Policy_NeverSendsAndStopsTogether()
    {
        // A run is either active (stoppable) or idle (sendable): the menu
        // never offers to cancel another worker's run by accident.
        Assert.False(ComposerActionPolicy.CanSend(true, true, "x") && ComposerActionPolicy.CanStop(true));
        Assert.False(ComposerActionPolicy.CanSend(false, false, "x") && ComposerActionPolicy.CanStop(false));
    }

    [Theory]
    // Plain chords: Enter sends, Alt/Shift+Enter newline, Ctrl+Enter sends.
    [InlineData(Key.Enter, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.Send)]
    [InlineData(Key.Enter, KeyModifiers.Alt, false, false, false, ComposerActionPolicy.ComposerKeyAction.InsertNewline)]
    [InlineData(Key.Enter, KeyModifiers.Shift, false, false, false, ComposerActionPolicy.ComposerKeyAction.InsertNewline)]
    [InlineData(Key.Enter, KeyModifiers.Control, false, false, false, ComposerActionPolicy.ComposerKeyAction.Send)]
    [InlineData(Key.Enter, KeyModifiers.Control, true, true, false, ComposerActionPolicy.ComposerKeyAction.Send)]
    // Menu open: plain Enter accepts a highlight, Tab completes, arrows move.
    [InlineData(Key.Enter, KeyModifiers.None, true, true, false, ComposerActionPolicy.ComposerKeyAction.AcceptSuggestion)]
    [InlineData(Key.Enter, KeyModifiers.None, true, false, false, ComposerActionPolicy.ComposerKeyAction.Send)]
    [InlineData(Key.Tab, KeyModifiers.None, true, true, false, ComposerActionPolicy.ComposerKeyAction.CompleteSuggestion)]
    [InlineData(Key.Tab, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.None)]
    [InlineData(Key.Up, KeyModifiers.None, true, false, false, ComposerActionPolicy.ComposerKeyAction.SuggestPrevious)]
    [InlineData(Key.Down, KeyModifiers.None, true, false, false, ComposerActionPolicy.ComposerKeyAction.SuggestNext)]
    [InlineData(Key.Up, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.None)]
    // Escape: busy cancels, idle with menu dismisses, else nothing.
    [InlineData(Key.Escape, KeyModifiers.None, false, false, true, ComposerActionPolicy.ComposerKeyAction.CancelRun)]
    [InlineData(Key.Escape, KeyModifiers.None, true, false, false, ComposerActionPolicy.ComposerKeyAction.DismissSuggestions)]
    [InlineData(Key.Escape, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.None)]
    [InlineData(Key.Escape, KeyModifiers.None, true, true, true, ComposerActionPolicy.ComposerKeyAction.CancelRun)]
    // Anything else never acts.
    [InlineData(Key.A, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.None)]
    [InlineData(Key.F5, KeyModifiers.None, false, false, false, ComposerActionPolicy.ComposerKeyAction.None)]
    public void DecideComposerKey_MapsEachChordOnce(
        Key key, KeyModifiers mods, bool slashOpen, bool highlight, bool busy,
        ComposerActionPolicy.ComposerKeyAction expected)
    {
        Assert.Equal(expected, ComposerActionPolicy.DecideComposerKey(key, mods, slashOpen, highlight, busy));
    }

    [Fact]
    public void SlashCommands_TableHasDocumentedCommands()
    {
        Assert.Equal(3, ComposerActionPolicy.SlashCommands.Count);
        Assert.Contains(ComposerActionPolicy.SlashCommands, c => c.Name == "model");
        Assert.Contains(ComposerActionPolicy.SlashCommands, c => c.Name == "thinking");
        Assert.Contains(ComposerActionPolicy.SlashCommands, c => c.Name == "help");
    }

    [Theory]
    [InlineData("/model", "model", "")]
    [InlineData("/model qwen", "model", "qwen")]
    [InlineData("/thinking high", "thinking", "high")]
    [InlineData("/HELP", "HELP", "")]
    [InlineData("/", "", "")]
    public void ParseSlashCommand_SplitsNameAndArgs(string text, string name, string args)
    {
        Assert.Equal((name, args), ComposerActionPolicy.ParseSlashCommand(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("//literal")]
    [InlineData(" //not-first")]
    public void ParseSlashCommand_RejectsNonCommands(string? text)
    {
        Assert.Null(ComposerActionPolicy.ParseSlashCommand(text));
    }

    [Fact]
    public void FindSlashCommand_MatchesCaseInsensitively()
    {
        Assert.NotNull(ComposerActionPolicy.FindSlashCommand("MODEL"));
        Assert.Null(ComposerActionPolicy.FindSlashCommand("compact"));
        Assert.Null(ComposerActionPolicy.FindSlashCommand(string.Empty));
    }

    [Fact]
    public void FilterSlashCommands_MatchesByPrefix()
    {
        Assert.Equal(3, ComposerActionPolicy.FilterSlashCommands(string.Empty).Count);
        var one = ComposerActionPolicy.FilterSlashCommands("mo");
        Assert.Equal("model", Assert.Single(one).Name);
        Assert.Empty(ComposerActionPolicy.FilterSlashCommands("zzz"));
    }

    [Theory]
    [InlineData("//literal", "/literal")]
    [InlineData("///x", "//x")]
    [InlineData("/model", "/model")]
    [InlineData("plain", "plain")]
    public void UnescapeSlashLiteral_StripsOneEscapeSlash(string text, string expected)
    {
        Assert.Equal(expected, ComposerActionPolicy.UnescapeSlashLiteral(text));
    }
}
