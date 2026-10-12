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
/// Effective tab list for one registry load: catalog entries plus, when the
/// connected workspace is missing from them, its retained tab.
/// </summary>
public sealed record ResolvedTabs(
    IReadOnlyList<CatalogEntry> Entries,
    string TargetKey,
    string? RetentionNote);

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

    /// <summary>
    /// Decide the visible tabs and the transition target for one registry
    /// load. When <paramref name="connected"/> is set but absent from the
    /// catalog (recovery wiped its entry mid-session), its tab is RETAINED —
    /// explicitly labeled from the live connection's own workspace object —
    /// so the loaded feed is never displayed under another tab's name. The
    /// target is the connected key whenever connected, else the preferred
    /// key when present, else legacy. Pure and fully unit-tested; the host
    /// only renders Entries, transitions to TargetKey and shows
    /// RetentionNote.
    /// </summary>
    public static ResolvedTabs ResolveTabs(
        WorkspaceCatalog catalog,
        AgentXWorkspace? connected,
        string? preferredKey)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var entries = catalog.Entries.ToList();
        string? note = null;
        if (connected is not null && entries.All(e => e.Key != connected.TabKey))
        {
            entries.Add(RetainedEntry(connected));
            note = $"Registry lost workspace '{connected.Slug}' — tab retained with its live session until disconnect.";
        }

        string target;
        if (connected is not null)
        {
            target = connected.TabKey;
        }
        else if (preferredKey is not null && entries.Any(e => e.Key == preferredKey))
        {
            target = preferredKey;
        }
        else
        {
            target = LegacyKey;
        }

        return new ResolvedTabs(entries, target, note);
    }

    /// <summary>
    /// A tab row rebuilt from the live connection's own workspace identity.
    /// Never fabricated from registry data: slug, session and root all come
    /// from the object Pi is actually running under.
    /// </summary>
    public static CatalogEntry RetainedEntry(AgentXWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var name = Path.GetFileName(workspace.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(name))
        {
            name = workspace.Slug;
        }

        return new CatalogEntry(
            workspace.TabKey,
            workspace.IsLegacy ? "Legacy session (agent-x-main)" : $"{name} (session active)",
            workspace.IsLegacy
                ? $"Pre-workspace global session — preserved as-is. Session {workspace.SessionId}."
                : $"{workspace.ProjectRoot} — session {workspace.SessionId}. " +
                  "Registry lost this workspace entry — tab retained until disconnect; reconnect stays on this live session.",
            workspace.IsLegacy,
            workspace.IsLegacy
                ? null
                : new WorkspaceRegistration(workspace.Slug, workspace.ProjectRoot, workspace.SessionId, DateTime.UtcNow));
    }

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
                    name,
                    $"{name} ({registration.Slug}) — {registration.ProjectRoot} — session {registration.SessionId}. " +
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
