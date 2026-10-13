using System.Diagnostics;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Regression for review comment 6145828034: DisposeAsync must not tear
/// anything down or release the session claim when it fails to acquire the
/// lifecycle gate held by an in-progress connect (bounded, retryable failure
/// with ownership retained), and a connect queued behind a completed
/// disposal must never publish a process or re-claim the session.
/// Both races are barrier-controlled — deterministic overlap, not timing.
/// They self-adapt when Pi is unavailable: the connect then fails at
/// launch, and the claim/disposal invariants asserted here still hold.
/// </summary>
public sealed class WorkerDisposalRaceTests : IDisposable
{
    private readonly string _root;
    private readonly string _proj;

    public WorkerDisposalRaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ax-disprace-{Guid.NewGuid():N}");
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
    public async Task DisposeDuringBlockedConnect_TimesOutWithOwnershipRetained_ThenRetryRetiresPublishedProcess()
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
            // The connect now holds the lifecycle gate, blocked after claiming.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Dispose contends for that gate and must FAIL bounded without
            // touching anything at all.
            worker.DisposeGateTimeout = TimeSpan.FromMilliseconds(250);
            var failure = await Record.ExceptionAsync(async () => await worker.DisposeAsync());
            Assert.IsType<TimeoutException>(failure);

            // Nothing was torn down: ownership retained, no half-done state.
            Assert.True(PiSessionClaims.IsClaimedBy(worker.SessionId, worker),
                "A gate-starved DisposeAsync must never release the session claim.");
            Assert.Null(worker.Snapshot().ProcessId);

            // Release the barrier: the connect finishes normally (publishes a
            // process when Pi is available, fails at launch when not) — the
            // worker is still NOT disposed either way.
            release.TrySetResult();
            var connectFailure = await Record.ExceptionAsync(async () => await connectTask);
            if (connectFailure is not null)
            {
                Assert.IsNotType<ObjectDisposedException>(connectFailure);
            }

            Assert.True(PiSessionClaims.IsClaimedBy(worker.SessionId, worker),
                "The claim survives until an ownership-completing disposal.");
            var publishedPid = worker.Snapshot().ProcessId;

            // Retry with the gate now free: disposal completes for real.
            await worker.DisposeAsync();

            Assert.False(PiSessionClaims.IsClaimedBy(worker.SessionId, worker));
            Assert.Null(worker.Snapshot().ProcessId);
            if (publishedPid is not null)
            {
                AssertExited(publishedPid.Value); // no orphan from the overlapping connect
            }

            // After a completed disposal, connecting must refuse without publishing.
            await Assert.ThrowsAsync<ObjectDisposedException>(() => worker.ConnectAsync());
            Assert.Null(worker.Snapshot().ProcessId);
        }
        finally
        {
            release.TrySetResult();
            worker.LifecycleGateHookForTests = null;
            _ = connectTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    [Fact]
    public async Task ConnectQueuedBehindCompletedDisposal_NeverPublishesOrReclaims()
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

        // 1) Connect #1 holds the gate, parked after its session claim.
        var connect1 = worker.ConnectAsync();
        Task<PiEngineState>? connect2 = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // 2) Queue a patient DisposeAsync behind the blocked connect.
            worker.DisposeGateTimeout = TimeSpan.FromSeconds(60);
            var disposeTask = worker.DisposeAsync().AsTask();
            await Task.Delay(200);
            Assert.False(disposeTask.IsCompleted, "Dispose must wait for the gate.");

            // 3) Queue connect #2 while disposal is still pending: it passes
            //    the entry disposal check (flag not set yet) and parks.
            connect2 = worker.ConnectAsync();
            await Task.Delay(200);
            Assert.False(connect2.IsCompleted, "Connect #2 must be parked on the gate.");

            // 4) Release the barrier: connect #1 finishes, then the gate
            //    frees. Whatever order the waiters resolve in, the end state
            //    after disposal must hold: no re-claim, no published process.
            release.TrySetResult();
            var connect1Failure = await Record.ExceptionAsync(async () => await connect1);
            if (connect1Failure is not null)
            {
                Assert.IsNotType<ObjectDisposedException>(connect1Failure);
            }

            var pid1 = worker.Snapshot().ProcessId;

            await disposeTask;
            var connect2Failure = await Record.ExceptionAsync(async () => await connect2!);
            if (connect2Failure is not null)
            {
                Assert.IsType<ObjectDisposedException>(connect2Failure);
            }

            Assert.False(PiSessionClaims.IsClaimedBy(worker.SessionId, worker),
                "A connect overlapping a completed disposal must not re-claim the session.");
            Assert.Null(worker.Snapshot().ProcessId);
            if (pid1 is not null)
            {
                AssertExited(pid1.Value);
            }
        }
        finally
        {
            release.TrySetResult();
            worker.LifecycleGateHookForTests = null;
            _ = connect1.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (connect2 is not null)
            {
                _ = connect2.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
    }

    private static void AssertExited(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // Already gone from the process table.
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"Process {pid} survived disposal — orphan process.");
    }
}
