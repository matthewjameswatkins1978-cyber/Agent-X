namespace AgentX.Core;

/// <summary>
/// Project-aware workspace identity for Agent X sessions.
///
/// The legacy GUI used one global session (<c>agent-x-main</c> in
/// <c>%LocalAppData%/AgentX/agent-cwd</c>) for everything. A workspace binds
/// one project root to one Pi session id and one Agent X-managed working
/// directory, so switching projects cannot mix histories, working
/// directories or tool execution paths.
///
/// Safety rules, all tested:
/// - Pi session files keep living in the shared managed <c>sessions/</c>
///   directory; Pi groups project sessions by working directory, and every
///   workspace gets its own working directory, so session files stay
///   distinct without moving legacy files.
/// - Agent X never writes into the project root itself; the Pi process runs
///   in the managed working directory, exactly like the legacy
///   <c>agent-cwd</c> arrangement.
/// - The legacy session (<c>agent-x-main</c> / <c>agent-cwd</c>) is
///   addressable as <see cref="Legacy"/> and its paths are disjoint from
///   every workspace subtree by construction.
/// </summary>
public sealed class AgentXWorkspace
{
    public const string SessionIdPrefix = "ax-";
    public const string LegacySessionId = "agent-x-main";
    public const string LegacyWorkingDirectoryName = "agent-cwd";
    public const string WorkspaceDirectoryName = "workspaces";
    public const int MaxSlugLength = 48;
    public const int MaxSlugTailLength = 32;

    public AgentXWorkspace(string managedRoot, string slug, string projectRoot, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        PiSessionOptions.ValidateSessionId(sessionId);
        ManagedRoot = managedRoot;
        Slug = slug;
        ProjectRoot = projectRoot;
        SessionId = sessionId;
    }

    public string ManagedRoot { get; }

    public string Slug { get; }

    /// <summary>Canonical project root. Identity anchor only; never written to.</summary>
    public string ProjectRoot { get; }

    public string SessionId { get; }

    /// <summary>True for the preserved global session, never for a project workspace.</summary>
    public bool IsLegacy => SessionId == LegacySessionId;

    /// <summary>Pi session storage override (passed as --session-dir). Shared with legacy.</summary>
    public string SessionDirectory => Path.Combine(ManagedRoot, "sessions");

    /// <summary>
    /// Stable Pi working directory (session grouping key and tool execution
    /// root). Legacy uses <c>agent-cwd</c>; workspaces use
    /// <c>workspaces/&lt;slug&gt;/cwd</c> — disjoint by construction.
    /// </summary>
    public string WorkingDirectory => IsLegacy
        ? Path.Combine(ManagedRoot, LegacyWorkingDirectoryName)
        : Path.Combine(ManagedRoot, WorkspaceDirectoryName, Slug, "cwd");

    /// <summary>The exact legacy session. Paths equal PiSessionOptions.Default().</summary>
    public static AgentXWorkspace Legacy(string managedRoot)
        => new(managedRoot, LegacySessionId, managedRoot, LegacySessionId);

    /// <summary>
    /// Derive a workspace for an existing project directory. The slug keeps a
    /// readable tail of the sanitized path plus a stable 8-hex hash of the
    /// full canonical path, so deep paths that sanitize identically still get
    /// distinct session ids (the registry collision guard stays as backstop).
    /// Throws loudly (never guesses) when the directory does not exist or
    /// yields no usable identity.
    /// </summary>
    public static AgentXWorkspace ForProject(string managedRoot, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var canonical = CanonicalizeProjectRoot(projectRoot);
        if (!Directory.Exists(canonical))
        {
            throw new ArgumentException($"Project directory does not exist: {projectRoot}", nameof(projectRoot));
        }

        var tail = DeriveSlug(canonical);
        if (tail.Length > MaxSlugTailLength)
        {
            tail = tail[^MaxSlugTailLength..].Trim('-');
        }

        var hash = StableHash8(canonical);
        var slug = string.IsNullOrEmpty(tail) ? hash : $"{tail}-{hash}";
        return new AgentXWorkspace(managedRoot, slug, canonical, SessionIdPrefix + slug);
    }

    /// <summary>Create the managed directories. Safe on every connect. Never touches the project root.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(WorkingDirectory);
    }

    /// <summary>
    /// CLI fragment for persistent sessions. Same shape as
    /// PiSessionOptions.ToArguments: --session-id opens the exact project
    /// session or creates it when absent.
    /// </summary>
    public string ToArguments()
    {
        PiSessionOptions.ValidateSessionId(SessionId);
        return $"--session-dir \"{SessionDirectory}\" --session-id {SessionId}";
    }

    /// <summary>Launch options for the Pi subprocess, rooted in this workspace.</summary>
    public PiRpcOptions ToPiRpcOptions(string? executablePath = null)
    {
        EnsureDirectories();
        return PiRpcOptions.ForSession(SessionDirectory, SessionId, WorkingDirectory, executablePath);
    }

    public static string CanonicalizeProjectRoot(string projectRoot)
    {
        var full = Path.GetFullPath(projectRoot.Trim());
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Deterministic sanitize step for <see cref="ForProject"/>: lowercase, runs of
    /// non-alphanumerics become one dash, capped at <see cref="MaxSlugLength"/>.
    /// Distinct roots that sanitize identically are disambiguated by hash in
    /// ForProject, with the registry guard (see WorkspaceRegistry) as backstop —
    /// never a silent share.
    /// </summary>
    public static string DeriveSlug(string canonicalProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalProjectRoot);
        var lowered = canonicalProjectRoot.ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        var lastWasDash = true; // Suppress a leading dash.
        foreach (var c in lowered)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].Trim('-');
        }

        if (slug.Length == 0
            || !char.IsLetterOrDigit(slug[0])
            || !char.IsLetterOrDigit(slug[^1]))
        {
            throw new ArgumentException(
                $"Project path yields no usable workspace identity: {canonicalProjectRoot}", nameof(canonicalProjectRoot));
        }

        return slug;
    }

    /// <summary>Stable 8-hex fingerprint (SHA-256, first 4 bytes). Runtime-hash free.</summary>
    public static string StableHash8(string value)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest, 0, 4).ToLowerInvariant();
    }
}
