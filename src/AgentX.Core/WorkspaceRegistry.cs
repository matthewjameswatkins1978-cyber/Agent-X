using System.Text.Json;

namespace AgentX.Core;

/// <summary>One registered workspace: JSON-serializable, no behavior.</summary>
public sealed record WorkspaceRegistration(
    string Slug,
    string ProjectRoot,
    string SessionId,
    DateTime CreatedUtc);

/// <summary>
/// Maps project roots to workspace identities, persisted as JSON in the
/// managed root (<c>workspaces.json</c>). Rules, all tested:
/// - Same canonical root always resolves to the same registration
///   (registering twice is idempotent, never a duplicate).
/// - Two different roots sanitizing to one slug is a loud
///   InvalidOperationException — histories must never silently share a
///   session id.
/// - The legacy <c>agent-x-main</c> session is not a registration; nothing
///   here can claim, move or delete it.
/// - A corrupt registry file throws InvalidDataException and the file is
///   left in place for inspection — never deleted or overwritten silently.
/// - Saves are atomic (temp file + move) so a crash cannot leave a half
///   registry behind.
/// </summary>
public sealed class WorkspaceRegistry
{
    public const string FileName = "workspaces.json";

    private readonly string _managedRoot;
    private readonly Dictionary<string, WorkspaceRegistration> _bySlug = new(StringComparer.Ordinal);

    public WorkspaceRegistry(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        _managedRoot = managedRoot;
    }

    public static string RegistryPath(string managedRoot) => Path.Combine(managedRoot, FileName);

    public IReadOnlyList<WorkspaceRegistration> List()
        => _bySlug.Values.OrderBy(r => r.Slug, StringComparer.Ordinal).ToArray();

    public bool TryGetBySlug(string slug, out WorkspaceRegistration? registration)
        => _bySlug.TryGetValue(slug, out registration);

    /// <summary>
    /// Resolve the workspace for a project root, registering it on first
    /// use. Idempotent for the same root; loud on slug collision.
    /// </summary>
    public AgentXWorkspace GetOrRegister(string projectRoot)
    {
        var canonical = AgentXWorkspace.CanonicalizeProjectRoot(projectRoot);
        foreach (var existing in _bySlug.Values)
        {
            if (string.Equals(existing.ProjectRoot, canonical, StringComparison.OrdinalIgnoreCase))
            {
                return new AgentXWorkspace(_managedRoot, existing.Slug, existing.ProjectRoot, existing.SessionId);
            }
        }

        var workspace = AgentXWorkspace.ForProject(_managedRoot, canonical);
        if (_bySlug.TryGetValue(workspace.Slug, out var occupant)
            && !string.Equals(occupant.ProjectRoot, workspace.ProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Workspace slug '{workspace.Slug}' is already registered for '{occupant.ProjectRoot}'; " +
                $"refusing to share one Pi session between two projects ('{workspace.ProjectRoot}'). " +
                "Pick a distinct project directory.");
        }

        _bySlug[workspace.Slug] = new WorkspaceRegistration(
            workspace.Slug, workspace.ProjectRoot, workspace.SessionId, DateTime.UtcNow);
        return workspace;
    }

    public static WorkspaceRegistry Load(string managedRoot)
    {
        var registry = new WorkspaceRegistry(managedRoot);
        var path = RegistryPath(managedRoot);
        if (!File.Exists(path))
        {
            return registry;
        }

        WorkspaceRegistration[] entries;
        try
        {
            var json = File.ReadAllText(path);
            entries = JsonSerializer.Deserialize<WorkspaceRegistration[]>(json)
                ?? throw new InvalidDataException($"Workspace registry '{path}' deserialized to null.");
        }
        catch (Exception ex) when (ex is not InvalidDataException and (JsonException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException(
                $"Workspace registry '{path}' is unreadable; left in place for inspection, nothing was deleted or reset.", ex);
        }

        var seenSessionIds = new HashSet<string>(StringComparer.Ordinal);
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var validated = ValidateStoredEntry(path, entry);
            if (!registry._bySlug.TryAdd(validated.Slug, validated))
            {
                throw new InvalidDataException(
                    $"Workspace registry '{path}' lists slug '{validated.Slug}' twice; left in place for inspection.");
            }

            if (!seenSessionIds.Add(validated.SessionId))
            {
                throw new InvalidDataException(
                    $"Workspace registry '{path}' reuses session id '{validated.SessionId}'; left in place for inspection.");
            }

            if (!seenRoots.Add(validated.ProjectRoot))
            {
                throw new InvalidDataException(
                    $"Workspace registry '{path}' lists project '{validated.ProjectRoot}' twice; left in place for inspection.");
            }
        }

        return registry;
    }

    /// <summary>
    /// Fail-closed validation for one stored entry. Rejects blank fields,
    /// malformed slugs, session ids that disagree with their slug, reserved
    /// legacy names, and non-rooted project paths — so a hand-edited or
    /// planted registry file cannot redirect managed paths or collide with
    /// the legacy session. Returns the entry with a canonical project root.
    /// Throws InvalidDataException; the file is never modified here.
    /// </summary>
    public static WorkspaceRegistration ValidateStoredEntry(string registryPath, WorkspaceRegistration? entry)
    {
        void Fail(string reason) => throw new InvalidDataException(
            $"Workspace registry '{registryPath}' {reason}; left in place for inspection.");

        // Null entries ([null] is valid JSON) fail closed like every other
        // malformed case — never an unhandled ArgumentNullException.

        if (entry is null)
        {
            throw new InvalidDataException(
                $"Workspace registry '{registryPath}' contains a null entry; left in place for inspection.");
        }

        if (string.IsNullOrWhiteSpace(entry.Slug)
            || entry.Slug.Length > 64
            || !System.Text.RegularExpressions.Regex.IsMatch(entry.Slug, "^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$"))
        {
            Fail($"rejects slug '{entry.Slug ?? ""}' (lowercase letters, digits and dashes only)");
        }

        if (entry.Slug == "legacy" || entry.Slug == AgentXWorkspace.LegacySessionId)
        {
            Fail($"rejects reserved slug '{entry.Slug}'");
        }

        if (entry.SessionId != AgentXWorkspace.SessionIdPrefix + entry.Slug)
        {
            Fail($"rejects session id '{entry.SessionId ?? ""}' disagreeing with slug '{entry.Slug}'");
        }

        if (string.IsNullOrWhiteSpace(entry.ProjectRoot))
        {
            Fail("contains an entry with a blank project root");
        }

        // Raw rootedness first: a stored relative path would resolve against
        // whatever the process working directory happens to be.
        if (!Path.IsPathRooted(entry.ProjectRoot))
        {
            Fail($"rejects non-rooted project path '{entry.ProjectRoot}'");
        }

        string canonical;
        try
        {
            canonical = AgentXWorkspace.CanonicalizeProjectRoot(entry.ProjectRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException(
                $"Workspace registry '{registryPath}' contains an unusable project path '{entry.ProjectRoot}'; left in place for inspection.", ex);
        }

        if (!Path.IsPathRooted(canonical))
        {
            Fail($"rejects non-rooted project path '{entry.ProjectRoot}'");
        }

        // Exact identity agreement: the slug/session must be precisely what
        // this canonical root derives — a syntactically valid pair minted
        // for an unrelated root is rejected, not trusted.
        var (expectedSlug, expectedSession) = AgentXWorkspace.DeriveIdentity(canonical);
        if (!string.Equals(entry.Slug, expectedSlug, StringComparison.Ordinal)
            || !string.Equals(entry.SessionId, expectedSession, StringComparison.Ordinal))
        {
            Fail($"rejects identity mismatch for '{entry.ProjectRoot}' (expected slug '{expectedSlug}')");
        }

        return entry with { ProjectRoot = canonical };
    }

    public void Save()
    {
        Directory.CreateDirectory(_managedRoot);
        var path = RegistryPath(_managedRoot);
        var json = JsonSerializer.Serialize(List(), new JsonSerializerOptions { WriteIndented = true });
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
