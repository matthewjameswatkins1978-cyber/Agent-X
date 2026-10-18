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

    /// One slash command: Pi-canonical name plus what Agent X does with
    /// it. The set is deliberately small — model/thinking route to the
    /// existing pickers, help answers locally. Anything else gets an
    /// honest unknown-command notice instead of a Pi prompt.
    public sealed record SlashCommand(string Name, string Description);

    public static readonly IReadOnlyList<SlashCommand> SlashCommands = new[]
    {
        new SlashCommand("model", "Select a model (opens the model picker)"),
        new SlashCommand("thinking", "Set the thinking level (opens the thinking picker)"),
        new SlashCommand("help", "Show Agent X commands and composer keys"),
    };

    /// <summary>
    /// Split "/name args..." into its command and argument text. Null
    /// unless slash-first. A leading "//" escapes to a literal slash
    /// (ordinary prompt, never a command); a bare "/" parses to an
    /// empty name (menu with every command, nothing to execute).
    /// </summary>
    public static (string Name, string Args)? ParseSlashCommand(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/')
        {
            return null;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        var body = text[1..].TrimStart();
        if (body.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var space = body.IndexOfAny([' ', '\t', '\n', '\r']);
        if (space < 0)
        {
            return (body, string.Empty);
        }

        return (body[..space], body[(space + 1)..].Trim());
    }

    /// Exact command match (case-insensitive); null when unknown.
    public static SlashCommand? FindSlashCommand(string name)
        => SlashCommands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// Menu suggestions: known names starting with the typed fragment.
    public static IReadOnlyList<SlashCommand> FilterSlashCommands(string fragment)
        => SlashCommands
            .Where(c => c.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// "//" escapes to a literal leading slash: strip one and send the
    /// rest as an ordinary prompt. Anything else passes through untouched.
    public static string UnescapeSlashLiteral(string text)
        => text.StartsWith("//", StringComparison.Ordinal) ? text[1..] : text;
}
