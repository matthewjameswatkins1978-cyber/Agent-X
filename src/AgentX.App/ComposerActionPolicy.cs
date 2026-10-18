using AgentX.Core;
using Avalonia.Input;

namespace AgentX.App;

/// <summary>
/// Enablement policy for the composer's Send/Stop surface (App-only,
/// UI-free). The same contract drives the context-menu items and documents
/// the keyboard behaviour: Ctrl+Enter sends only a valid prompt to a
/// connected, idle worker; Escape stops only an active run. Single execution
/// path — the menu and the chords both call the existing handlers.
/// </summary>
public static class ComposerActionPolicy
{
    public static bool CanSend(bool connected, bool busy, string? text)
        => connected && !busy && PromptInput.Prepare(text) is not null;

    public static bool CanStop(bool busy) => busy;

    /// <summary>
    /// B1 rendering pass: after an accepted send, the visible composer
    /// clears ONLY when it still holds exactly the sent message — text the
    /// reader typed mid-run is a new draft and survives. Failed sends keep
    /// the text (the host only calls this on success).
    /// </summary>
    public static bool ShouldClearComposer(string? currentText, string sentMessage)
        => currentText == sentMessage;

    /// One action per keypress: Enter sends, Alt/Shift+Enter inserts a
    /// newline, Ctrl+Enter always sends, Escape cancels a busy run (or
    /// dismisses the slash menu), Up/Down/Tab drive an open slash menu.
    /// A highlighted slash suggestion wins over plain Enter so choosing
    /// never accidentally sends; Ctrl+Enter always sends.
    public enum ComposerKeyAction
    {
        None,
        Send,
        InsertNewline,
        CancelRun,
        AcceptSuggestion,
        CompleteSuggestion,
        DismissSuggestions,
        SuggestPrevious,
        SuggestNext,
    }

    /// <summary>
    /// The single keyboard contract shared by every workspace: each chord
    /// maps to exactly one action, so Enter can never both newline and
    /// send. Pure and fully tested; the host only applies the verdict.
    /// </summary>
    public static ComposerKeyAction DecideComposerKey(
        Key key, KeyModifiers mods, bool slashOpen, bool hasHighlight, bool busy)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        return key switch
        {
            Key.Enter when ctrl => ComposerKeyAction.Send,
            Key.Enter when alt || shift => ComposerKeyAction.InsertNewline,
            Key.Enter when slashOpen && hasHighlight => ComposerKeyAction.AcceptSuggestion,
            Key.Enter => ComposerKeyAction.Send,
            Key.Tab when slashOpen && hasHighlight => ComposerKeyAction.CompleteSuggestion,
            Key.Escape when busy => ComposerKeyAction.CancelRun,
            Key.Escape when slashOpen => ComposerKeyAction.DismissSuggestions,
            Key.Up when slashOpen => ComposerKeyAction.SuggestPrevious,
            Key.Down when slashOpen => ComposerKeyAction.SuggestNext,
            _ => ComposerKeyAction.None,
        };
    }
}
