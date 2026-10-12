namespace AgentX.Core;

/// <summary>
/// One bindable workspace row: the preserved legacy session or one validated
/// registry entry. <see cref="ToWorkspace"/> rebuilds the workspace identity
/// from stored data (the constructor revalidates the session id).
/// </summary>
public sealed record CatalogEntry(
    string Key,
    string Label,
    string Detail,
    bool IsLegacy,
    WorkspaceRegistration? Registration)
{
    public override string ToString() => Label;

    public AgentXWorkspace ToWorkspace(string managedRoot)
        => IsLegacy || Registration is null
            ? AgentXWorkspace.Legacy(managedRoot)
            : new AgentXWorkspace(managedRoot, Registration.Slug, Registration.ProjectRoot, Registration.SessionId);
}

/// <summary>
/// Startup view over the workspace registry: the bindable entry list plus an
/// optional recovery state. When the registry file is unreadable the catalog
/// still yields exactly the legacy entry with <see cref="RecoveryError"/> set
/// — the host disables registration and keeps the legacy session working
/// instead of crashing or overwriting the corrupt original. Pure and
/// UI-free; the host marshals nothing here.
/// </summary>
public sealed class WorkspaceCatalog
{
    public const string LegacyKey = "legacy";

    public WorkspaceCatalog(IReadOnlyList<CatalogEntry> entries, string? recoveryError)
    {
        Entries = entries;
        RecoveryError = recoveryError;
    }

    public IReadOnlyList<CatalogEntry> Entries { get; }

    /// <summary>Null when healthy; the human-readable reason when in recovery.</summary>
    public string? RecoveryError { get; }

    public bool IsRecovery => RecoveryError is not null;

    public static WorkspaceCatalog Load(string managedRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managedRoot);
        var legacy = AgentXWorkspace.Legacy(managedRoot);
        var legacyEntry = new CatalogEntry(
            LegacyKey,
            "Legacy session (agent-x-main)",
            $"Pre-workspace global session — preserved as-is. Session {legacy.SessionId}. " +
            $"Pi runs in {legacy.WorkingDirectory} with your user authority.",
            IsLegacy: true,
            Registration: null);
        try
        {
            var registry = WorkspaceRegistry.Load(managedRoot);
            var entries = new List<CatalogEntry> { legacyEntry };
            foreach (var registration in registry.List())
            {
                var name = Path.GetFileName(registration.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(name))
                {
                    name = registration.Slug;
                }

                entries.Add(new CatalogEntry(
                    registration.Slug,
                    $"{name} ({registration.Slug})",
                    $"{registration.ProjectRoot} — session {registration.SessionId}. " +
                    "Pi runs HERE with your user authority (isolated from other projects, not sandboxed).",
                    IsLegacy: false,
                    Registration: registration));
            }

            return new WorkspaceCatalog(entries, recoveryError: null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new WorkspaceCatalog(
                [legacyEntry],
                $"Workspace registry unreadable — registration disabled (file preserved, legacy session works). " +
                $"Repair or back up {WorkspaceRegistry.RegistryPath(managedRoot)}, then restart. {ex.Message}");
        }
    }
}
