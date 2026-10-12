namespace AgentX.Core;

/// <summary>
/// Where Agent X keeps Pi-native session files. Pi groups project sessions by
/// working directory, so Agent X pins a stable, Agent X-managed working
/// directory: the same session id then resolves to the same file after the
/// GUI closes and reopens, on any install location on this machine.
/// </summary>
public sealed record PiSessionOptions(
    string ManagedRoot,
    string SessionId)
{
    public const string DefaultSessionId = "agent-x-main";

    public static PiSessionOptions Default() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AgentX"),
        DefaultSessionId);

    /// <summary>Pi session storage override (passed as --session-dir).</summary>
    public string SessionDirectory => Path.Combine(ManagedRoot, "sessions");

    /// <summary>Stable working directory for the Pi process (session grouping key).</summary>
    public string WorkingDirectory => Path.Combine(ManagedRoot, "agent-cwd");

    /// <summary>Create the managed directories. Safe to call on every connect.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(WorkingDirectory);
    }

    /// <summary>
    /// CLI fragment for persistent sessions. Pi rule: --session-id opens the
    /// exact project session or creates it when absent; it cannot be combined
    /// with --session, --continue, --resume or --no-session.
    /// </summary>
    public string ToArguments()
    {
        ValidateSessionId(SessionId);
        return $"--session-dir \"{SessionDirectory}\" --session-id {SessionId}";
    }

    public static void ValidateSessionId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)
            || !char.IsLetterOrDigit(sessionId[0])
            || !char.IsLetterOrDigit(sessionId[^1])
            || sessionId.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "Pi session ids accept letters, numbers, '.', '_' and '-', and must start and end with a letter or number.",
                nameof(sessionId));
        }
    }
}
