using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Real-Pi regressions for the C3 failure-path corrections: failed save with
/// a CONNECTED worker (retired process, retained ownership, no orphan),
/// quarantine + revalidation after invalidated verification, stale-capability
/// clearing across a failed reconnect, cancel/completion/kill/dispose races
/// (never ObjectDisposedException, never a fabricated receipt), and turn
/// closure on transport death. Conventions match WorkerRuntimeLiveTests:
/// disposable temp roots, loud skip without the shim, exact-echo prompts.
/// </summary>
public sealed class WorkerFailureLiveTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper _output;

    public WorkerFailureLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; live failure-path test cannot run here.");
        return false;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-failpath-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception) { /* never fails the test */ }
    }

    private static void AssertProcessGone(int? pid)
    {
        try
        {
            var process = Process.GetProcessById(pid!.Value);
            process.Refresh();
            Assert.True(process.HasExited, $"Process {pid} should be gone.");
        }
        catch (ArgumentException)
        {
            // Process table no longer knows it: exited. Expected.
        }
    }

    private static bool AnyStreaming(ConversationFeed feed)
        => feed.Snapshot().Any(e => e.IsStreaming);

    [Fact]
    public async Task FailedRemoveSave_WithConnectedWorker_RetiredProcessRetainedOwnership_NoOrphan()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"), "Agent ONE");
            await worker.ConnectAsync(ct);
            var pid = worker.Snapshot().ProcessId;
            Assert.NotNull(pid);
            Assert.True(worker.Snapshot().Verified);

            var beforeBytes = File.ReadAllBytes(WorkerStore.StorePath(root));
            Exception? saveFailure;
            using (new FileStream(WorkerStore.StorePath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                saveFailure = await Record.ExceptionAsync(() => manager.RemoveWorkerAsync(worker.Id));
            }

            // The save failed, but the PROCESS was retired first and the
            // WORKER is still owned — the worst case is a dead, registered
            // worker, never an unreachable live Pi.
            Assert.NotNull(saveFailure);
            Assert.True(saveFailure is IOException or UnauthorizedAccessException,
                $"Expected a persistence failure, got: {saveFailure?.GetType().Name}");
            AssertProcessGone(pid);
            Assert.Contains(manager.Workers, w => w.Id == worker.Id);
            Assert.Same(worker, manager.Selected);
            Assert.Equal(beforeBytes, File.ReadAllBytes(WorkerStore.StorePath(root)));

            // Recovery: retry completes the persistence half (dispose idempotent).
            await manager.RemoveWorkerAsync(worker.Id);
            Assert.Empty(manager.Workers);
            Assert.Empty(WorkerStore.Load(root).List()); // sole worker removed → store legitimately empty
            await manager.DisposeAsync();
            AssertProcessGone(pid); // no orphan after every path
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task InvalidatedVerification_RefusesActions_ConnectQuarantinesAndReplaces()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"));
            await worker.ConnectAsync(ct);
            var pid1 = worker.Snapshot().ProcessId;
            Assert.NotNull(pid1);
            Assert.True(worker.IdentityVerified);
            Assert.NotNull(worker.Capabilities);

            // Simulate a connection whose validation/probe previously failed.
            worker.InvalidateVerificationForTests();
            Assert.False(worker.Snapshot().Verified);
            Assert.Null(worker.Snapshot().Capabilities);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SendAsync("Reply with exactly: SHOULD-NOT-RUN", null, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.GetModelsAsync(ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.GetStateAsync(ct));

            // ConnectAsync must NOT take a bypassing shortcut: it quarantines
            // the unverified process (pid1 dies) and re-verifies a new one.
            var state = await worker.ConnectAsync(ct);
            var pid2 = worker.Snapshot().ProcessId;
            Assert.NotNull(pid2);
            Assert.NotEqual(pid1, pid2);
            AssertProcessGone(pid1);
            Assert.True(worker.IdentityVerified);
            Assert.NotNull(worker.Capabilities);
            Assert.NotNull(worker.Version);
            Assert.Equal(worker.SessionId, state.SessionId);

            // Revalidated: normal operation resumes.
            var marker = $"ax-reverify-{Guid.NewGuid():N}";
            var (_, receipt) = await worker.SendAsync($"Reply with exactly: {marker}", null, ct);
            Assert.True(receipt.RunHealthy);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task FailedReconnect_ClearsCapabilitiesAndState_RepublishesAfterRecovery()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"));
            await worker.ConnectAsync(ct);
            Assert.NotNull(worker.Capabilities);
            Assert.NotNull(worker.LastConfirmedState);

            // Sabotage: retire the current process (its working-directory
            // handle blocks the rename), then make the worker's project —
            // Pi's working directory — vanish, so the replacement process
            // cannot even start.
            var pid = worker.Snapshot().ProcessId;
            Assert.NotNull(pid);
            var dying = Process.GetProcessById(pid!.Value);
            dying.Kill(entireProcessTree: true);
            Assert.True(dying.WaitForExit(5000), "Pi process should exit after kill.");
            var proj = Path.Combine(root, "proj");
            var projBak = Path.Combine(root, "proj.bak");
            Directory.Move(proj, projBak);

            await Assert.ThrowsAnyAsync<Exception>(() => worker.ReconnectAsync(ct));

            // Stale claims are gone and every action refuses until re-verified.
            Assert.Null(worker.Capabilities);
            Assert.Null(worker.LastConfirmedState);
            Assert.False(worker.IdentityVerified);
            Assert.False(worker.Snapshot().Verified);
            await Assert.ThrowsAsync<InvalidOperationException>(() => worker.SendAsync("Reply with exactly: STILL-NOT-RUN", null, ct));

            // Restore the project directory and reconnect: capabilities and
            // confirmed state are re-probed fresh from the new process.
            Directory.Move(projBak, proj);

            var state = await worker.ReconnectAsync(ct);
            Assert.Equal(worker.SessionId, state.SessionId);
            Assert.True(worker.IdentityVerified);
            Assert.NotNull(worker.Capabilities);
            Assert.NotNull(worker.Version);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task CancelVersusCompletionAndRepeatedCancel_NeverObjectDisposed_TurnCloses()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"));
            await worker.ConnectAsync(ct);

            // Race: a run completing at the same moment cancels fire.
            var send = worker.SendAsync("Reply with exactly: RACE-OK", null, ct);
            var cancelSpam = Task.Run(async () =>
            {
                for (var i = 0; i < 50; i++)
                {
                    await worker.CancelAsync(CancellationToken.None);
                    await Task.Delay(10);
                }
            }, ct);

            string? text = null;
            PiRunReceipt? receipt = null;
            OperationCanceledException? oce = null;
            try
            {
                (text, receipt) = await send;
            }
            catch (OperationCanceledException ex)
            {
                oce = ex;
            }

            await cancelSpam; // every CancelAsync must complete without ObjectDisposedException
            Assert.True(oce is not null || receipt is not null, "Run must end as cancelled or receipted.");
            if (receipt is not null)
            {
                Assert.Equal("RACE-OK", text); // completed first — never a fabricated result
            }

            // Repeated cancellation with nothing running: silent no-op.
            await worker.CancelAsync(ct);
            await worker.CancelAsync(ct);
            await worker.CancelAsync(ct);

            Assert.False(AnyStreaming(worker.Feed), "The visible run must be closed either way.");

            // Cancellation still leaves the worker usable.
            var marker = $"ax-cancelpost-{Guid.NewGuid():N}";
            var (_, receipt2) = await worker.SendAsync($"Reply with exactly: {marker}", null, ct);
            Assert.True(receipt2.RunHealthy);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task KillPlusCancel_DuringSend_TurnCloses_NoFabricatedReceipt()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"));
            await worker.ConnectAsync(ct);
            var pid = worker.Snapshot().ProcessId;
            Assert.NotNull(pid);

            var send = worker.SendAsync("Using powershell, run exactly: Start-Sleep 30; Write-Output done", null, ct);
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            // Transport death racing cancellation: both may fire; neither may
            // throw ObjectDisposedException outward from CancelAsync.
            Process.GetProcessById(pid!.Value).Kill(entireProcessTree: true);
            var cancelTask = worker.CancelAsync(CancellationToken.None);
            await Task.WhenAll(cancelTask, Task.CompletedTask);

            var threw = false;
            try { await send; }
            catch (OperationCanceledException) { threw = true; }
            catch (PiRpcTransportException) { threw = true; }
            Assert.True(threw, "An interrupted run must surface an error, never a receipt.");
            await Task.Delay(1500); // let the transport-fault path settle the turn

            Assert.False(AnyStreaming(worker.Feed), "Transport failure must close the visible run.");
            Assert.Null(worker.Feed.BuildMap().LastReceipt); // no fabricated receipt

            var state = await worker.ReconnectAsync(ct);
            Assert.Equal(worker.SessionId, state.SessionId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DisposeDuringActiveSend_EndsRun_RetiresProcess_DisposeIdempotent()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"));
            await worker.ConnectAsync(ct);
            var pid = worker.Snapshot().ProcessId;
            Assert.NotNull(pid);

            var send = worker.SendAsync("Using powershell, run exactly: Start-Sleep 30; Write-Output done", null, ct);
            await Task.Delay(TimeSpan.FromSeconds(4), ct);

            await manager.DisposeAsync();

            // The in-flight run must end (cancelled by disposal), not hang.
            var ended = false;
            try
            {
                await send.WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException) { ended = true; }
            catch (PiRpcTransportException) { ended = true; }
            catch (AggregateException ex) when (ex.InnerExceptions.Any(
                e => e is OperationCanceledException or PiRpcTransportException)) { ended = true; }
            Assert.True(ended, "Disposal must end the active run with an error, not a receipt.");

            AssertProcessGone(pid);
            Assert.False(AnyStreaming(worker.Feed));

            // Repeat disposal: no-op, no throw, no double-release.
            await manager.DisposeAsync();
        }
        finally
        {
            Cleanup(root);
        }
    }
}
