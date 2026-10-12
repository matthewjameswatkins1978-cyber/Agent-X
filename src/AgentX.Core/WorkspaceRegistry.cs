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

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Slug) || string.IsNullOrWhiteSpace(entry.ProjectRoot))
            {
                throw new InvalidDataException(
                    $"Workspace registry '{path}' contains an incomplete entry; left in place for inspection.");
            }

            registry._bySlug[entry.Slug] = entry;
        }

        return registry;
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
