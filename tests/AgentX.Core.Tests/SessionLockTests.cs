using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// C4 in-process session-lock units and ownership semantics (deterministic;
/// no Pi required): exclusive acquire/refuse/release, equivalent-path
/// convergence on one lock, ownership retained across a failed connect and
/// released by disposal, the C3 gate-starved disposal keeping the lock until
/// a successful retry, and measured acquire/release overhead.
/// </summary>
public sealed class SessionLockTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly string _proj;

    public SessionLockTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"ax-lock-{Guid.NewGuid():N}");
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

    [Fact]
    public void Acquire_IsExclusive_RefusesSecondHolder_FileIsNotTheLock()
    {
        var sid = "axw-lock01";
        using (var first = CrossProcessSessionLock.Acquire(_root, sid))
        {
            var ex = Assert.Throws<SessionLockedException>(() => CrossProcessSessionLock.Acquire(_root, sid));
            Assert.Equal(sid, ex.SessionId);
            Assert.Contains(sid, ex.Message);
            Assert.IsAssignableFrom<InvalidOperationException>(ex); // existing ownership-catch sites cover it
        }

        // Disposing the handle releases ownership; the leftover file does not.
        using var again = CrossProcessSessionLock.Acquire(_root, sid);
        Assert.NotNull(again);
    }

    [Fact]
    public void EquivalentSpellingsOfSameManagedRoot_ShareOneLock()
    {
        var sid = "axw-lockeq";
        using var held = CrossProcessSessionLock.Acquire(_root, sid);

        // Case variant (NTFS is case-insensitive) and trailing separator
        // both resolve to the same file — both refused while held.
        Assert.Throws<SessionLockedException>(() =>
            CrossProcessSessionLock.Acquire(_root.ToUpperInvariant(), sid));
        Assert.Throws<SessionLockedException>(() =>
            CrossProcessSessionLock.Acquire(_root + Path.DirectorySeparatorChar, sid));
    }

    [Fact]
    public async Task FailedConnect_RetainsOwnership_DisposeReleasesIt()
    {
        await using var manager = new PiWorkerManager(_root);
        var worker = manager.CreateWorker(_proj, "W");

        // Deterministic launch-path failure with no Pi involved: the project
        // directory vanishes AFTER claim+lock are taken, and
        // EnsureConversation refuses to recreate it.
        Directory.Delete(_proj, recursive: true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => worker.ConnectAsync());
        Assert.True(worker.SessionLockHeld, "a failed connect retains cross-process ownership");

        // Another manager on the same root — refused (the process-wide claim
        // fires first, the cross-process lock would fire next) with no
        // process started and no lock taken on the loser.
        await using var manager2 = new PiWorkerManager(_root);
        var worker2 = manager2.Workers.Single();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => worker2.ConnectAsync());
        Assert.False(worker2.SessionLockHeld, "the refused worker never acquired the lock");

        // Graceful disposal releases ownership; a fresh acquire succeeds.
        await worker.DisposeAsync();
        Assert.False(worker.SessionLockHeld);
        using var probe = CrossProcessSessionLock.Acquire(_root, worker.SessionId);
        Assert.NotNull(probe);
    }

    [Fact]
    public async Task GateStarvedDisposal_KeepsLock_UntilSuccessfulRetry()
    {
        await using var manager = new PiWorkerManager(_root);
        var worker = manager.CreateWorker(_proj, "W");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookRuns = 0;
        worker.LifecycleGateHookForTests = async ct =>
        {
            if (Interlocked.Exchange(ref hookRuns, 1) == 0)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        };

        var connectTask = worker.ConnectAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(worker.SessionLockHeld, "the lock is taken before the gate hook (before any Pi launch)");

            worker.DisposeGateTimeout = TimeSpan.FromMilliseconds(250);
            var failure = await Record.ExceptionAsync(async () => await worker.DisposeAsync());
            Assert.IsType<TimeoutException>(failure);
            Assert.True(worker.SessionLockHeld, "a gate-starved disposal keeps cross-process ownership");

            release.TrySetResult();
            _ = await Record.ExceptionAsync(async () => await connectTask); // success or launch failure — lock kept either way
            Assert.True(worker.SessionLockHeld);

            await worker.DisposeAsync(); // retry with the gate free
            Assert.False(worker.SessionLockHeld);
            using var probe = CrossProcessSessionLock.Acquire(_root, worker.SessionId);
            Assert.NotNull(probe);
        }
        finally
        {
            release.TrySetResult();
            worker.LifecycleGateHookForTests = null;
            _ = connectTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    [Fact]
    public void LockOverhead_AcquireReleaseCycles_MeasureMicroseconds()
    {
        var sid = "axw-lockbench";
        const int cycles = 100;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < cycles; i++)
        {
            using (CrossProcessSessionLock.Acquire(_root, sid))
            {
            }
        }

        sw.Stop();
        var avgUs = sw.Elapsed.TotalMilliseconds * 1000.0 / cycles;
        _output.WriteLine($"cross-process lock acquire+release: {avgUs:F1} µs average over {cycles} cycles");
        // Generous sanity bound (no flakiness): a file open/close is well
        // under a millisecond on any healthy disk. Reported as measured
        // evidence, not as a hard performance contract.
        Assert.True(avgUs < 5_000, $"lock cycle averaged {avgUs:F1} µs (expected < 5000 µs)");
    }
}
