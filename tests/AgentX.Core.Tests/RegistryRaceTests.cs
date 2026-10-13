using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// C5 registry concurrency (deterministic, in-process): stale-save conflict
/// with no silent loss, busy-save conflict that is retryable without a
/// reopen, stale removal that neither resurrects nor deletes unrelated
/// registrations, corrupt bytes preserved, failed save keeps prior bytes
/// with coherent memory, and measured save overhead.
/// </summary>
public sealed class RegistryRaceTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly string _proj;

    public RegistryRaceTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"ax-reg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _proj = Path.Combine(_root, "proj");
        Directory.CreateDirectory(_proj);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static string DiskLabels(string root) =>
        string.Join(",", WorkerStore.Load(root).List().Select(r => r.Label));

    [Fact]
    public async Task StaleSave_ConflictsWithoutSilentLoss_ReopenRecovers()
    {
        // Two managers load the same registry (same revision baseline).
        await using var managerA = new PiWorkerManager(_root);
        await using var managerB = new PiWorkerManager(_root);

        var a = managerA.CreateWorker(_proj, "A"); // persists rev1
        Assert.Equal("A", DiskLabels(_root));

        // B's save is stale: A's registration must survive and B must get a
        // clear typed conflict (nothing written, memory rolled back).
        var conflict = Assert.Throws<WorkerRegistryConflictException>(
            () => { managerB.CreateWorker(_proj, "B"); });
        Assert.Equal(WorkerRegistryConflictKind.StaleRevision, conflict.Kind);
        Assert.Equal("A", DiskLabels(_root)); // A's registration survives
        Assert.Empty(managerB.Workers); // B rolled back coherently

        // Documented recovery (reopen): a fresh manager loads A's state,
        // saves cleanly, and nothing was lost.
        await using var managerB2 = new PiWorkerManager(_root);
        managerB2.CreateWorker(_proj, "B");
        Assert.Equal("A,B", DiskLabels(_root));
        Assert.NotNull(a);
    }

    [Fact]
    public async Task BusySave_ConflictsClearly_AndIsRetryableWithoutReopen()
    {
        await using var manager = new PiWorkerManager(_root);
        var lockPath = WorkerRegistryLock.PathFor(_root);
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var conflict = Assert.Throws<WorkerRegistryConflictException>(
                () => { manager.CreateWorker(_proj, "W"); });
            Assert.Equal(WorkerRegistryConflictKind.Busy, conflict.Kind);
            Assert.Empty(manager.Workers); // nothing constructed or indexed
            Assert.False(File.Exists(WorkerStore.StorePath(_root))); // nothing written
        }

        // Transient: the SAME manager retries successfully once the lock is
        // free (its revision never moved).
        var w = manager.CreateWorker(_proj, "W");
        Assert.Equal("W", DiskLabels(_root));
        Assert.Single(manager.Workers);
        Assert.Equal(w.Label, manager.Workers[0].Label);
    }

    [Fact]
    public async Task StaleRemove_NeverResurrectsNorDeletesUnrelated()
    {
        await using var managerA = new PiWorkerManager(_root);
        managerA.CreateWorker(_proj, "W1");
        managerA.CreateWorker(_proj, "W2");
        await using var managerB = new PiWorkerManager(_root); // loads {W1, W2} at the current revision
        var w1 = managerA.Workers.Single(r => r.Label == "W1");
        var w2 = managerB.Workers.Single(r => r.Label == "W2");
        Assert.Equal("W1,W2", DiskLabels(_root));

        // A removes W1 (rev bumps). B — still holding {W1, W2} — now tries
        // to remove W2; a stale write would resurrect W1 AND drop W2.
        await managerA.RemoveWorkerAsync(w1.Id);
        Assert.Equal("W2", DiskLabels(_root));

        var conflict = await Assert.ThrowsAsync<WorkerRegistryConflictException>(
            () => managerB.RemoveWorkerAsync(w2.Id));
        Assert.Equal(WorkerRegistryConflictKind.StaleRevision, conflict.Kind);

        // Disk is exactly as A left it: W1 stays removed, W2 stays present.
        Assert.Equal("W2", DiskLabels(_root));
        // B's memory restored coherently (registration + index back).
        Assert.Equal(2, managerB.Workers.Count);

        // Recovery: reopen, then the intended removal completes.
        await using var managerB2 = new PiWorkerManager(_root);
        var w2again = managerB2.Workers.Single(r => r.Label == "W2");
        await managerB2.RemoveWorkerAsync(w2again.Id);
        Assert.Equal(string.Empty, DiskLabels(_root));
    }

    [Fact]
    public void CorruptRegistry_ThrowsAndBytesArePreserved()
    {
        var path = WorkerStore.StorePath(_root);
        var corrupt = "{ this is not json [";
        File.WriteAllText(path, corrupt);

        Assert.Throws<InvalidDataException>(() => new PiWorkerManager(_root));
        Assert.Equal(corrupt, File.ReadAllText(path)); // never reset, replaced or repaired
        Assert.Equal(corrupt, File.ReadAllText(path));
    }

    [Fact]
    public async Task FailedSave_PreservesPriorBytes_CoherentMemory_Retriable()
    {
        await using var manager = new PiWorkerManager(_root);
        manager.CreateWorker(_proj, "keep");
        var before = File.ReadAllBytes(WorkerStore.StorePath(_root));

        // Fail the atomic move itself (registry file held by a reader that
        // shares no writes/deletes) — not the lock or the revision check.
        using (new FileStream(WorkerStore.StorePath(_root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => { manager.CreateWorker(_proj, "failed"); });
            Assert.True(failure is IOException or UnauthorizedAccessException,
                $"Expected an IO-family save failure, got: {failure?.GetType().Name}");
        }

        Assert.Equal(before, File.ReadAllBytes(WorkerStore.StorePath(_root))); // prior bytes
        Assert.Single(manager.Workers); // "keep" only — coherent rollback
        Assert.Equal("keep", DiskLabels(_root));

        // Retriable: same manager succeeds once the obstruction clears
        // (its revision never moved).
        manager.CreateWorker(_proj, "retried");
        Assert.Equal("keep,retried", DiskLabels(_root));
    }

    [Fact]
    public void SaveOverhead_MeasuredWithLockAndRevisionCheck()
    {
        var store = new WorkerStore(_root);
        store.Add(_proj, "seed");
        store.Save();

        const int cycles = 50;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < cycles; i++)
        {
            store.Save(); // lock + re-read + revision check + atomic write
        }

        sw.Stop();
        var avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / cycles;
        _output.WriteLine($"registry save (lock+revision+atomic write): {avgUs:F1} µs average over {cycles} cycles");
        // Generous sanity bound (a file lock, one hash and a temp move are
        // well under a millisecond each); reported as measured evidence.
        Assert.True(avgUs < 50_000, $"registry save averaged {avgUs:F1} µs (expected < 50000 µs)");
    }
}
