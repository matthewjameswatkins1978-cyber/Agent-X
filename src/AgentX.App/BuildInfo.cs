namespace AgentX.App;

/// <summary>
/// Canonical build-identity presentation: pure formatting over the
/// compile-time <c>BuildIdentity</c> constants plus live process values
/// (executable path, PID). All formatting takes explicit arguments — never
/// ambient state — so every result is deterministic and unit-tested; the
/// GUI passes the constants in via <c>LiveShortLabel</c>/<c>LiveDetailText</c>.
/// </summary>
internal static class BuildInfo
{
    public static string ShortLabel(string displayVersion, string shortSha, bool dirty)
        => $"Agent X v{displayVersion} · {shortSha}{(dirty ? "*" : string.Empty)}";

    public static string ChannelOf(string suffix, string configuration)
        => suffix.Length > 0 ? "Preview"
            : configuration == "Release" ? "Production" : "Development";

    public static string DetailText(
        string displayVersion,
        string channel,
        string fullSha,
        string branch,
        string builtAtUtc,
        string configuration,
        bool dirty,
        string exePath,
        int pid)
        => string.Join("\n", new[]
        {
            $"Agent X v{displayVersion} ({channel}{(dirty ? ", dirty working copy" : string.Empty)})",
            $"Commit: {fullSha}",
            $"Branch: {branch}",
            $"Built: {builtAtUtc} ({configuration})",
            $"Executable: {exePath}",
            $"PID: {pid}",
        });

    public static string LiveShortLabel()
        => ShortLabel(BuildIdentity.DisplayVersion, BuildIdentity.ShortSha, BuildIdentity.SourceDirty);

    public static string LiveDetailText()
        => DetailText(
            BuildIdentity.DisplayVersion,
            BuildIdentity.Channel,
            BuildIdentity.CommitSha,
            BuildIdentity.Branch,
            BuildIdentity.BuiltAtUtc,
            BuildIdentity.Configuration,
            BuildIdentity.SourceDirty,
            Environment.ProcessPath ?? "unknown",
            Environment.ProcessId);
}
