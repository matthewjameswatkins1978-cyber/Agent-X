namespace AgentX.App;

/// <summary>
/// One selectable agent context for the worker-card strip: a worker or a
/// legacy workspace entry. Pure data, UI-free: the window builds these from
/// worker/registry truth and renders them; clicks drive the existing
/// selection paths (never a heading-only switch). No invented tasks: the
/// detail line carries only observed state and activity or project names.
/// </summary>
public sealed record ContextCardData(
    string Key,
    bool IsWorker,
    string Name,
    string StateWord,
    string Detail,
    bool IsConnected,
    bool IsBusy,
    bool IsSelected)
{
    public string StatusLine => Detail.Length == 0 ? StateWord : $"{StateWord} · {Detail}";
}

/// <summary>Pure builders for the approved shell: worker cards and the compact footer.</summary>
public static class ContextCards
{
    /// Connection-honest state word: a disconnected context is never shown
    /// as a healthy idle connection. Busy beats connecting beats connected;
    /// anything else is explicitly not connected.
    public static string StateWordFor(bool busy, bool connecting, bool connected)
        => busy ? "Working" : connecting ? "Connecting" : connected ? "Idle" : "Not connected";

    public static ContextCardData WorkerCard(
        string id, string name, string detail, bool connected, bool busy, bool connecting, bool selected)
        => new(id, true, name, StateWordFor(busy, connecting, connected), detail, connected, busy, selected);

    public static ContextCardData WorkspaceCard(
        string key, string name, string detail, bool connected, bool busy, bool connecting, bool selected)
        => new(key, false, name, StateWordFor(busy, connecting, connected), detail, connected, busy, selected);

    /// <summary>
    /// Per-card width for the balanced strip: the Add control lives outside
    /// the scroller, so cards share exactly the card viewport minus the
    /// real inter-card gaps. Single card fills it (edges meet the page
    /// below); the 200px floor sends overflow to the scroller instead of
    /// crushing content. Pure so the layout rule is tested, not eyeballed.
    /// </summary>
    public static double CardWidth(double viewportWidth, int cardCount)
    {
        if (cardCount <= 0 || viewportWidth <= 0)
        {
            return 200;
        }

        return Math.Max(200, (viewportWidth - (8 * (cardCount - 1))) / cardCount);
    }

    /// <summary>
    /// Whether two card strips show the same thing (key, text, state and
    /// order): lets the window skip needless rebuilds that would drop
    /// focus and scroll.
    /// </summary>
    public static bool CardsEqual(IReadOnlyList<ContextCardData> current, IReadOnlyList<ContextCardData> fresh)
    {
        if (current.Count != fresh.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = fresh[i];
            if (a.Key != b.Key || a.Name != b.Name || a.StatusLine != b.StatusLine
                || a.IsSelected != b.IsSelected || a.IsConnected != b.IsConnected || a.IsBusy != b.IsBusy)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Attention heuristic for the notice dot ONLY: routine information
    /// (guidance, confirmations, progress) stays quiet; genuine warning or
    /// error language lights the control. Conservative keyword set, pinned
    /// by tests on both sides; a miss only dims the dot — the full text is
    /// always one click away and writers are untouched.
    /// </summary>
    public static bool IsAlert(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return AlertTokens.Any(token => text.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] AlertTokens =
    [
        "fail", "cannot", "could not", "unreadable", "disabled", "refus",
        "corrupt", "mismatch", "denied", "unavailable", "unable", "error",
        "invalid", "expired", "quarantine",
    ];

    /// <summary>
    /// Notice expansion lifecycle: call on every status change. A new alert
    /// opens automatically; a new routine message collapses unless the user
    /// deliberately opened the notice (manual toggle); unchanged or empty
    /// text keeps the current state. Writers are untouched — severity stays
    /// a hint, and typed severity remains a future improvement.
    /// </summary>
    public sealed record NoticeState(bool Open, bool Manual, string? LastText);

    public static NoticeState NextNotice(NoticeState state, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new NoticeState(false, state.Manual, null);
        }

        if (text == state.LastText)
        {
            return state;
        }

        return IsAlert(text)
            ? new NoticeState(true, state.Manual, text)
            : new NoticeState(state.Manual, state.Manual, text);
    }

    public static NoticeState ToggleNotice(NoticeState state)
        => state.Open
            ? new NoticeState(false, false, state.LastText)
            : new NoticeState(true, true, state.LastText);

    /// <summary>Bounded branch freshness: refresh when never checked or older than the TTL.</summary>
    public static readonly TimeSpan GitBranchTtl = TimeSpan.FromMinutes(5);

    public static bool BranchNeedsRefresh(DateTime? lastCheckedUtc, DateTime nowUtc)
        => lastCheckedUtc is null || (nowUtc - lastCheckedUtc.Value) >= GitBranchTtl;

    /// <summary>
    /// Compact footer text. Left: context name plus the git branch only when
    /// genuinely resolved. Right: model, thinking and context segments with
    /// em-dash placeholders for unknowns — never invented values.
    /// </summary>
    public static (string Left, string Right) FooterText(
        string contextName, string? gitBranch,
        string? model, string? thinking, double? contextPercent)
    {
        var left = string.IsNullOrWhiteSpace(gitBranch)
            ? contextName
            : $"{contextName} · {gitBranch.Trim()}";
        var right = $"{model switch { null or { Length: 0 } => "—", var m => m }} · " +
                    $"{thinking switch { null or { Length: 0 } => "—", var t => t }} · " +
                    $"{(contextPercent is double p ? $"Context {p:F1}%" : "Context —")}";
        return (left, right);
    }

    /// <summary>
    /// Parse `git rev-parse --abbrev-ref HEAD` output: a detached HEAD prints
    /// literally "HEAD", which is not a branch — report null (unknown) rather
    /// than showing it as one.
    /// </summary>
    public static string? ParseGitBranch(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var branch = output.Trim();
        return branch.Length == 0 || branch == "HEAD" ? null : branch;
    }
}
