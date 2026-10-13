namespace AgentX.App;

/// <summary>
/// Per-worker-tab view state, owned entirely by the App. The transcript
/// itself stays in Core's coordinator memory; this covers what the VIEW
/// owns: the composer draft, the list selection (by feed Seq, stable across
/// rebuilds), the scroll offset, and the status strings the tab showed.
/// Drafts persist across restarts via <see cref="ViewPrefs"/>; selection,
/// scroll and status are runtime-only. Switching tabs never touches a live
/// process — the coordinator still refuses view changes while connected,
/// so a background worker cannot be disturbed by looking at it.
/// </summary>
public sealed record WorkerViewState
{
    public string DraftText { get; set; } = string.Empty;

    public long? SelectedSeq { get; set; }

    public double ScrollOffset { get; set; }

    public string StatusText { get; set; } = string.Empty;

    public string RunStatusText { get; set; } = string.Empty;
}

/// <summary>
/// Stable identity tints for worker tabs, mirroring the terminal-tab idea:
/// crimson / amber / violet / slate (the reference tab colours). Tints are
/// applied as translucent tab fills; selection brightens via fill opacity.
/// Dots and footer text — never colour alone — carry connection state.
/// </summary>
public static class WorkerTabColors
{
    private static readonly string[] Tints =
    [
        "#B3404F", // crimson
        "#C77B3B", // amber
        "#7A5FC0", // violet
        "#4E7FA3", // slate blue
        "#3E8E6B", // teal
        "#8A6D5A", // umber
    ];

    public static string TintFor(int index)
        => Tints[((index % Tints.Length) + Tints.Length) % Tints.Length];
}
