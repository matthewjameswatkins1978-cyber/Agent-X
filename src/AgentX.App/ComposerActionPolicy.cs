using AgentX.Core;

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
}
