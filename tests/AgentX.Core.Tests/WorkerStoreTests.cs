using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// C3 worker runtime — deterministic contract (no Pi, no network): store
/// persistence and hostile payloads, session-namespace disjointness,
/// duplicate-session claims, confirmed-state helpers, version parsing, and
/// manager lifecycle. Real-Pi concurrency lives in WorkerRuntimeLiveTests;
/// the cross-version RPC contract lives in PiCompatibilityContractTests.
/// </summary>
public sealed class WorkerStoreTests
{
    private static string TempRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-workers-{tag}-" + Guid.NewGuid().ToString("N"));
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
    public void Store_RoundTrips_ByteStable_AndDerivedSession()
    {
        var root = TempRoot("roundtrip");
        try
        {
            var store = new WorkerStore(root);
            var reg = store.Add(ProjectDir(root, "Proj"), "Agent ONE");
            Assert.Equal(WorkerStore.SessionPrefix + reg.Id, reg.SessionId);
            Assert.Equal(8, reg.Id.Length);
            store.Save();

            var reloaded = WorkerStore.Load(root);
            var entry = Assert.Single(reloaded.List());
            Assert.Equal(reg.Id, entry.Id);
            Assert.Equal("Agent ONE", entry.Label);
            Assert.Equal(reg.SessionId, entry.SessionId);
            Assert.Equal(reg.ProjectRoot, entry.ProjectRoot, ignoreCase: true);

            var first = File.ReadAllText(WorkerStore.StorePath(root));
            reloaded.Save();
            Assert.Equal(first, File.ReadAllText(WorkerStore.StorePath(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Store_MissingProject_FailsLoud_CreatesNothing()
    {
        var root = TempRoot("missing");
        try
        {
            var store = new WorkerStore(root);
            Assert.Throws<DirectoryNotFoundException>(
                () => store.Add(Path.Combine(root, "does-not-exist")));
            Assert.Empty(store.List());
            Assert.False(File.Exists(WorkerStore.StorePath(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"zzzzzzzz\",\"label\":\"x\",\"projectRoot\":\"C:\\\\x\",\"sessionId\":\"axw-zzzzzzzz\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("[{\"id\":\"aabbccdd\",\"label\":\"x\",\"projectRoot\":\"C:\\\\x\",\"sessionId\":\"ax-not-derived\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("[{\"id\":\"aabbccdd\",\"label\":\"x\",\"projectRoot\":\"relative\\\\path\",\"sessionId\":\"axw-aabbccdd\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("[{\"id\":\"aabbccdd\",\"label\":\"x\",\"projectRoot\":\"C:\\\\x\",\"sessionId\":\"axw-aabbccdd\",\"createdUtc\":\"2026-01-01T00:00:00Z\"},{\"id\":\"aabbccdd\",\"label\":\"y\",\"projectRoot\":\"C:\\\\y\",\"sessionId\":\"axw-aabbccdd\",\"createdUtc\":\"2026-01-01T00:00:00Z\"}")]
    public void Store_HostilePayloads_FailClosed_PreserveFile(string payload)
    {
        var root = TempRoot("hostile");
        try
        {
            var path = WorkerStore.StorePath(root);
            File.WriteAllText(path, payload);
            Assert.Throws<InvalidDataException>(() => WorkerStore.Load(root));
            Assert.Equal(payload, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionNamespace_DisjointFromWorkspacesAndLegacy()
    {
        // axw- can never collide with workspace sessions (ax- + slug) or the
        // preserved legacy session, by construction — assert the derivation.
        var (slug, workspaceSession) = AgentXWorkspace.DeriveIdentity(@"C:\projects\some-repo");
        Assert.StartsWith("ax-", workspaceSession, StringComparison.Ordinal);
        Assert.NotEqual("axw-" + slug, workspaceSession);
        Assert.NotEqual(AgentXWorkspace.LegacySessionId, WorkerStore.SessionPrefix + "deadbeef");
        Assert.NotEqual(WorkerStore.SessionPrefix + "deadbeef", workspaceSession);
    }

    [Fact]
    public void SessionClaims_SingleWriterPerSession()
    {
        const string session = "axw-deadbeef";
        var ownerA = new object();
        var ownerB = new object();
        Assert.True(PiSessionClaims.TryClaim(session, ownerA));
        Assert.False(PiSessionClaims.TryClaim(session, ownerB)); // Second writer refused.
        Assert.True(PiSessionClaims.IsClaimedBy(session, ownerA));
        Assert.False(PiSessionClaims.IsClaimedBy(session, ownerB));
        Assert.Contains("Object#", PiSessionClaims.DescribeOwner(session), StringComparison.Ordinal);
        PiSessionClaims.Release(session, ownerB); // Wrong owner: no-op.
        Assert.True(PiSessionClaims.IsClaimedBy(session, ownerA));
        PiSessionClaims.Release(session, ownerA);
        Assert.False(PiSessionClaims.IsClaimedBy(session, ownerA));
        Assert.True(PiSessionClaims.TryClaim(session, ownerB)); // Reclaim after release.
        PiSessionClaims.Release(session, ownerB);
    }

    [Fact]
    public void ConfirmationHelpers_ClampAndMismatch_Loud()
    {
        var levels = (IReadOnlyList<string>)new[] { "off", "low", "medium", "high" };
        Assert.Equal("high", PiWorker.RequireCanonicalLevel(levels, "HIGH")); // Case-insensitive canonicalization.
        var missing = Assert.Throws<ArgumentException>(() => PiWorker.RequireCanonicalLevel(levels, "extreme"));
        Assert.Contains("off, low, medium, high", missing.Message, StringComparison.Ordinal);

        var state = new PiEngineState("m1", "Model One", "prov", "medium", false, false, "axw-x", 0, 0);
        PiWorker.RequireModelConfirmed(state, "prov", "m1");
        var modelMismatch = Assert.Throws<InvalidOperationException>(
            () => PiWorker.RequireModelConfirmed(state, "prov", "m2"));
        Assert.Contains("engine state reports 'prov/m1'", modelMismatch.Message, StringComparison.Ordinal);
        PiWorker.RequireLevelConfirmed(state, "MEDIUM");
        var levelMismatch = Assert.Throws<InvalidOperationException>(
            () => PiWorker.RequireLevelConfirmed(state, "high"));
        Assert.Contains("reports 'medium'", levelMismatch.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.1.0", 1, 1, 0)]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("v2.3.4-beta.1", 2, 3, 4)]
    [InlineData("pi version 10.20.30", 10, 20, 30)]
    public void VersionParse_ExtractsNumbers(string raw, int major, int minor, int patch)
    {
        var info = PiVersionInfo.TryParse(raw);
        Assert.NotNull(info);
        Assert.Equal(major, info!.Major);
        Assert.Equal(minor, info.Minor);
        Assert.Equal(patch, info.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no digits here")]
    [InlineData("1")]
    public void VersionParse_RejectsGarbage(string raw)
        => Assert.Null(PiVersionInfo.TryParse(raw));

    [Fact]
    public async Task Manager_PersistsIdentities_RecreatesOnReload_RemovesExplicitly()
    {
        var root = TempRoot("manager");
        try
        {
            var proj = ProjectDir(root, "Proj");
            string firstId;
            await using (var manager = new PiWorkerManager(root))
            {
                var worker = manager.CreateWorker(proj);
                firstId = worker.Id;
                Assert.Equal($"Worker 1", worker.Label); // Default label assigned and persisted.
                Assert.Same(worker, manager.Selected); // First worker becomes selection.
                Assert.Equal(WorkerStore.SessionPrefix + firstId, worker.SessionId);
                Assert.True(File.Exists(WorkerStore.StorePath(root)));
            }

            await using (var reloaded = new PiWorkerManager(root))
            {
                var worker = Assert.Single(reloaded.Workers);
                Assert.Equal(firstId, worker.Id);
                Assert.Equal("Worker 1", worker.Label);
                Assert.Equal(AgentXWorkspace.CanonicalizeProjectRoot(proj), worker.ProjectRoot, ignoreCase: true);

                var events = new List<string>();
                reloaded.WorkerRemoved += (_, id) => events.Add("removed:" + id);
                reloaded.SelectionChanged += (_, w) => events.Add("sel:" + (w?.Id ?? "(null)"));
                await reloaded.RemoveWorkerAsync(firstId);
                Assert.Empty(reloaded.Workers);
                Assert.Null(reloaded.Selected);
                Assert.Equal(new[] { "removed:" + firstId, "sel:(null)" }, events);
                Assert.Empty(WorkerStore.Load(root).List()); // Persisted removal, file kept as [].
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Manager_UnknownIdsAndMissingProjects_Refuse()
    {
        var root = TempRoot("refuse");
        try
        {
            await using var manager = new PiWorkerManager(root);
            Assert.Throws<ArgumentException>(() => manager.SelectWorker("nope"));
            Assert.DoesNotContain(manager.Workers, w => w.Id == "nope");
            Assert.Throws<DirectoryNotFoundException>(() => manager.CreateWorker(Path.Combine(root, "missing-project")));
            Assert.Empty(manager.Workers);
            Assert.False(File.Exists(WorkerStore.StorePath(root))); // Refused create persists nothing.
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Manager_LoadsCorruptStore_Loud()
    {
        var root = TempRoot("corrupt");
        try
        {
            File.WriteAllText(WorkerStore.StorePath(root), "[null]");
            Assert.Throws<InvalidDataException>(() => new PiWorkerManager(root));
            Assert.Equal("[null]", File.ReadAllText(WorkerStore.StorePath(root)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Selection_ChangesWithoutTouchingProcesses()
    {
        var root = TempRoot("select");
        try
        {
            await using var manager = new PiWorkerManager(root);
            var a = manager.CreateWorker(ProjectDir(root, "A"), "A");
            var b = manager.CreateWorker(ProjectDir(root, "B"), "B");
            var fired = new List<string>();
            manager.SelectionChanged += (_, w) => fired.Add(w?.Id ?? "(null)");

            manager.SelectWorker(b.Id);
            Assert.Same(b, manager.Selected);
            Assert.Equal(new[] { b.Id }, fired);
            Assert.False(a.IsConnected); // Selection is a view pointer: nothing connected.
            Assert.False(b.IsConnected);

            manager.SelectWorker(a.Id);
            Assert.Same(a, manager.Selected);
            Assert.Equal(new[] { b.Id, a.Id }, fired);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
