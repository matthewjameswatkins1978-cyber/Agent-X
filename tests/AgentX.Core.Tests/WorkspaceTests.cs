using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class WorkspaceTests
{
    private static string TempRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-ws-test-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Slug_DerivesDeterministically_FromProjectPaths()
    {
        Assert.Equal("d-projects-my-app", AgentXWorkspace.DeriveSlug(@"d:\Projects\My App"));
        Assert.Equal("d-projects-my-app", AgentXWorkspace.DeriveSlug(@"D:\Projects\My App\"));
        Assert.Equal("d-x-a-b", AgentXWorkspace.DeriveSlug(@"D:\x\A_B"));
        Assert.Throws<ArgumentException>(() => AgentXWorkspace.DeriveSlug(""));
        Assert.Throws<ArgumentException>(() => AgentXWorkspace.DeriveSlug("///"));
    }

    [Fact]
    public void ForProject_BuildsValidNamespacedIdentity()
    {
        var root = TempRoot("managed");
        try
        {
            var project = ProjectDir(root, "Demo Project");
            var workspace = AgentXWorkspace.ForProject(root, project);
            Assert.StartsWith("ax-", workspace.SessionId, StringComparison.Ordinal);
            Assert.NotEqual("agent-x-main", workspace.SessionId);
            PiSessionOptions.ValidateSessionId(workspace.SessionId.Replace("ax-", ""));
            Assert.False(workspace.IsLegacy);
            Assert.Equal(project, workspace.ProjectRoot, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ForProject_RejectsMissingDirectory_Loudly()
    {
        var root = TempRoot("managed-missing");
        try
        {
            Assert.Throws<ArgumentException>(
                () => AgentXWorkspace.ForProject(root, Path.Combine(root, "does-not-exist")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Legacy_PreservesExactHistoricalPaths()
    {
        var root = TempRoot("legacy");
        try
        {
            var legacy = AgentXWorkspace.Legacy(root);
            var classic = PiSessionOptions.Default();
            Assert.True(legacy.IsLegacy);
            Assert.Equal("agent-x-main", legacy.SessionId);
            // Same managed root: identical directories to the pre-workspace GUI.
            var expected = new PiSessionOptions(root, "agent-x-main");
            Assert.Equal(expected.SessionDirectory, legacy.SessionDirectory);
            Assert.Equal(expected.WorkingDirectory, legacy.WorkingDirectory);
            Assert.Equal(Path.Combine(root, "agent-cwd"), legacy.WorkingDirectory);
            _ = classic; // Default() points at the real LocalAppData; never touched here.
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Workspace_PathsAreDisjoint_FromLegacy()
    {
        var root = TempRoot("disjoint");
        try
        {
            var legacy = AgentXWorkspace.Legacy(root);
            var workspace = AgentXWorkspace.ForProject(root, ProjectDir(root, "Proj"));
            Assert.NotEqual(legacy.SessionId, workspace.SessionId);
            Assert.NotEqual(legacy.WorkingDirectory, workspace.WorkingDirectory);
            Assert.Contains(
                Path.Combine("workspaces", workspace.Slug, "cwd"),
                workspace.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("agent-cwd", workspace.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Arguments_OpenExactSession_WithoutNoSession()
    {
        var root = TempRoot("args");
        try
        {
            var workspace = AgentXWorkspace.ForProject(root, ProjectDir(root, "Proj"));
            var args = workspace.ToArguments();
            Assert.Contains("--session-dir", args, StringComparison.Ordinal);
            Assert.Contains($"--session-id {workspace.SessionId}", args, StringComparison.Ordinal);
            Assert.DoesNotContain("--no-session", args, StringComparison.Ordinal);

            var options = workspace.ToPiRpcOptions();
            Assert.Equal(workspace.WorkingDirectory, options.WorkingDirectory);
            Assert.Contains(workspace.SessionId, options.Arguments, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureDirectories_CreatesManagedDirs_LeavesProjectUntouched()
    {
        var root = TempRoot("ensure");
        try
        {
            var project = ProjectDir(root, "Proj");
            var workspace = AgentXWorkspace.ForProject(root, project);
            workspace.EnsureDirectories();
            Assert.True(Directory.Exists(workspace.SessionDirectory));
            Assert.True(Directory.Exists(workspace.WorkingDirectory));
            Assert.Empty(Directory.GetFileSystemEntries(project)); // Nothing written into the project.
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_GetOrRegister_IsIdempotent_PerRoot()
    {
        var root = TempRoot("registry");
        try
        {
            var registry = new WorkspaceRegistry(root);
            var project = ProjectDir(root, "Proj");
            var first = registry.GetOrRegister(project);
            var second = registry.GetOrRegister(project + Path.DirectorySeparatorChar); // Trailing sep: same root.
            Assert.Equal(first.Slug, second.Slug);
            Assert.Equal(first.SessionId, second.SessionId);
            Assert.Single(registry.List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SanitizeIdenticalRoots_GetDistinctSessionIds()
    {
        // "A B" and "A-B" sanitize identically, but the stable full-path
        // hash in ForProject still separates them — no loud failure needed.
        var root = TempRoot("hashdisambig");
        try
        {
            var parent = ProjectDir(root, "parent");
            var a = ProjectDir(parent, "A B");
            var b = ProjectDir(parent, "A-B");
            Assert.Equal(AgentXWorkspace.DeriveSlug(a), AgentXWorkspace.DeriveSlug(b));
            var wsA = AgentXWorkspace.ForProject(root, a);
            var wsB = AgentXWorkspace.ForProject(root, b);
            Assert.NotEqual(wsA.SessionId, wsB.SessionId);
            PiSessionOptions.ValidateSessionId(wsA.SessionId);
            PiSessionOptions.ValidateSessionId(wsB.SessionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_RejectsSlugSquatter_BetweenDifferentRoots()
    {
        // Guard backstop: a registry entry already holding the slug a new
        // root would derive is a loud refusal, never a shared session.
        var root = TempRoot("squatter");
        try
        {
            var project = ProjectDir(root, "Proj");
            var derived = AgentXWorkspace.ForProject(root, project);
            var squatter = new WorkspaceRegistration(
                derived.Slug, Path.Combine(root, "Elsewhere"), derived.SessionId, DateTime.UtcNow);
            Directory.CreateDirectory(root);
            File.WriteAllText(
                WorkspaceRegistry.RegistryPath(root),
                System.Text.Json.JsonSerializer.Serialize(new[] { squatter }));
            var registry = WorkspaceRegistry.Load(root);
            var clash = Assert.Throws<InvalidOperationException>(() => registry.GetOrRegister(project));
            Assert.Contains("refusing to share", clash.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_RoundTrips_ThroughDisk()
    {
        var root = TempRoot("roundtrip");
        try
        {
            var registry = new WorkspaceRegistry(root);
            var workspace = registry.GetOrRegister(ProjectDir(root, "Proj"));
            registry.Save();
            Assert.True(File.Exists(WorkspaceRegistry.RegistryPath(root)));

            var reloaded = WorkspaceRegistry.Load(root);
            Assert.True(reloaded.TryGetBySlug(workspace.Slug, out var entry));
            Assert.NotNull(entry);
            Assert.Equal(workspace.SessionId, entry.SessionId);
            Assert.Equal(workspace.ProjectRoot, entry.ProjectRoot, ignoreCase: true);
            // Idempotent across the save/load boundary: no duplicate.
            reloaded.GetOrRegister(ProjectDir(root, "Proj"));
            Assert.Single(reloaded.List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_MissingFile_LoadsEmpty()
    {
        var root = TempRoot("missing");
        try
        {
            Assert.Empty(WorkspaceRegistry.Load(root).List());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_CorruptFile_Throws_AndPreservesFile()
    {
        var root = TempRoot("corrupt");
        try
        {
            Directory.CreateDirectory(root);
            var path = WorkspaceRegistry.RegistryPath(root);
            File.WriteAllText(path, "{ not valid json {{{");
            Assert.Throws<InvalidDataException>(() => WorkspaceRegistry.Load(root));
            Assert.True(File.Exists(path)); // Left in place for inspection.
            Assert.Contains("not valid", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InterruptedRun_NeverBecomesHealthyReceipt()
    {
        // Priority 5 at Core level: a run that never settled (transport died,
        // user cancelled before settle) gets EndAssistantTurn(seq, null) — no
        // receipt entry, LastReceipt stays null in the map.
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "partial streamed text");
        feed.EndAssistantTurn(seq, null);
        var map = feed.BuildMap();
        Assert.Null(map.LastReceipt);
        Assert.DoesNotContain(map.Milestones, m => m.Kind == "receipt");
        Assert.Equal("partial streamed text", feed.GetEntryText(seq));
    }

    [Fact]
    public void SingleClientConversation_Throws_OnExplicitClient()
    {
        // Documents the reconnect requirement: a fixed client cannot be
        // replaced, so the obsolete ctor refuses one loudly.
#pragma warning disable CS0618 // Intentionally exercising the obsolete path.
        Assert.Throws<ArgumentException>(
            () => new PiConversation(new PiRpcClient(PiRpcOptions.Default())));
#pragma warning restore CS0618
    }
}
