namespace AgentX.Core;

/// <summary>
/// Project-aware workspace identity for Agent X sessions.
///
/// The legacy GUI used one global session (<c>agent-x-main</c> in
/// <c>%LocalAppData%/AgentX/agent-cwd</c>) for everything. A workspace binds
/// one project root to one Pi session id and runs Pi IN that project root,
/// so switching projects swaps histories, working directories and tool
/// execution paths together and they cannot mix.
///
/// Trust boundary, stated plainly: Pi executes with the user's OS authority
/// inside the selected project. Workspaces isolate projects from EACH OTHER
/// (separate session, history and cwd); they do NOT sandbox Pi from the
/// project itself. The GUI names the execution root on every connect and
/// only opens folders the user explicitly picks.
///
/// Safety rules, all tested:
/// - Pi session files keep living in the shared managed <c>sessions/</c>
///   directory; Pi groups project sessions by working directory, and every
///   workspace gets its own working directory, so session files stay
///   distinct without moving legacy files.
/// - Agent X's own managed files (registry, Pi session files) never go into
///   the project root; only the Pi process runs there, by explicit user
///   choice of folder, with the user's OS authority.
/// - The legacy session (<c>agent-x-main</c> / <c>agent-cwd</c>) is
///   addressable as <see cref="Legacy"/> and can never collide with a
///   workspace: workspace session ids carry the <c>ax-</c> prefix and Pi
///   groups sessions by working directory.
/// </summary>
public sealed class AgentXWorkspace
{
    public const string SessionIdPrefix = "ax-";
    public const string LegacySessionId = "agent-x-main";
    public const string LegacyWorkingDirectoryName = "agent-cwd";
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
    /// Pi working directory: the observed project root itself (legacy uses
    /// the managed <c>agent-cwd</c>). Pi therefore executes relative
    /// read/edit/write/bash operations and project-local discovery INSIDE
    /// the selected project with the user's OS authority — this is project
    /// isolation (separate session, history and cwd per workspace), NOT a
    /// sandbox. Switching workspaces swaps the entire execution root.
    /// </summary>
    public string WorkingDirectory => IsLegacy
        ? Path.Combine(ManagedRoot, LegacyWorkingDirectoryName)
        : ProjectRoot;

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

        var hash = StableHash8(canonical.ToLowerInvariant());
        var slug = string.IsNullOrEmpty(tail) ? hash : $"{tail}-{hash}";
        return new AgentXWorkspace(managedRoot, slug, canonical, SessionIdPrefix + slug);
    }

    /// <summary>
    /// Runtime session check: after connect/reconnect, the host passes Pi's
    /// reported <c>get_state.sessionId</c> here before showing a successful
    /// restore. A non-null mismatch throws instead of displaying a foreign
    /// history; null (Pi did not report one) is allowed and the host still
    /// shows exactly what was restored.
    /// </summary>
    public void ValidateSessionMatch(string? actualSessionId)
    {
        if (actualSessionId is not null
            && !string.Equals(actualSessionId, SessionId, StringComparison.Ordinal))
        {
            throw new PiRpcTransportException(
                $"Connected to unexpected Pi session '{actualSessionId}' (expected '{SessionId}') — refusing to show a foreign history.");
        }
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
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return ResolveLinks(full);
    }

    /// <summary>
    /// Resolve directory/file links (symlinks, Windows junctions) to their
    /// final target so aliased paths share one workspace identity. Cycles
    /// and unresolvable links keep the lexical path; nothing throws here —
    /// existence and usability are checked by the caller.
    /// </summary>
    public static string ResolveLinks(string path)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
        var current = path;
        for (var i = 0; i < 32; i++)
        {
            string? target = null;
            try
            {
                if (Directory.Exists(current))
                {
                    target = new DirectoryInfo(current).LinkTarget;
                }
                else if (File.Exists(current))
                {
                    target = new FileInfo(current).LinkTarget;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }

            if (string.IsNullOrEmpty(target))
            {
                break;
            }

            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? current, target));
            resolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seen.Add(resolved))
            {
                break;
            }

            current = resolved;
        }

        return current;
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
