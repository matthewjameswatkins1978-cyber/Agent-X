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
/// Stable identity tints for worker tabs, mirroring the terminal-tab idea.
/// B7: muted slate-leaning tints — a subtle per-tab accent, never a dominant
/// wash. Applied as translucent fills; selection brightens via fill opacity.
/// Dots and footer text — never colour alone — carry connection state.
/// </summary>
public static class WorkerTabColors
{
    private static readonly string[] Tints =
    [
        "#4A5A6A", // slate
        "#4E6B5E", // sage teal
        "#5A5A72", // muted violet grey
        "#6B5E4E", // umber grey
        "#54687A", // steel
        "#5E6B7A", // cool grey
    ];

    public static string TintFor(int index)
        => Tints[((index % Tints.Length) + Tints.Length) % Tints.Length];
}
