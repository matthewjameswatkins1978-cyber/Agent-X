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
    public static string StateWordFor(bool busy, bool connecting)
        => busy ? "Working" : connecting ? "Connecting" : "Idle";

    public static ContextCardData WorkerCard(
        string id, string name, string detail, bool connected, bool busy, bool connecting, bool selected)
        => new(id, true, name, StateWordFor(busy, connecting), detail, connected, busy, selected);

    public static ContextCardData WorkspaceCard(
        string key, string name, string detail, bool connected, bool busy, bool connecting, bool selected)
        => new(key, false, name, StateWordFor(busy, connecting), detail, connected, busy, selected);

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
                    $"{(contextPercent is double p ? $"Context {p:0}%": "Context —")}";
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
