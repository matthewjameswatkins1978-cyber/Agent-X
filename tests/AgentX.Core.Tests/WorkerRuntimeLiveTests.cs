using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Real-Pi multi-worker verification (mirrors WorkspaceSessionTests
/// conventions: temp managed roots, exact-echo markers, loud skip without
/// the shim, generous budgets). Covers the C3 matrix: two simultaneous
/// workers with distinct sessions, independent concurrent prompts,
/// selection that never interrupts, per-worker cancellation, process kill
/// with the other worker continuing, reconnect with history restoration,
/// duplicate session ownership, and manager disposal without orphans.
/// </summary>
public sealed class WorkerRuntimeLiveTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper _output;

    public WorkerRuntimeLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; multi-worker test cannot run here.");
        return false;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-workers-it-{Guid.NewGuid():N}");
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

    [Fact]
    public async Task TwoWorkers_ConcurrentPrompts_SelectionNeverInterrupts_ModelsIndependent()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        var sw = Stopwatch.StartNew();
        try
        {
            await using var manager = new PiWorkerManager(root);
            var a = manager.CreateWorker(ProjectDir(root, "proj-a"), "Agent ONE");
            var b = manager.CreateWorker(ProjectDir(root, "proj-b"), "Agent TWO");
            Assert.NotEqual(a.SessionId, b.SessionId);

            var stateA = await a.ConnectAsync(ct);
            var stateB = await b.ConnectAsync(ct);
            Assert.Equal(a.SessionId, stateA.SessionId);
            Assert.Equal(b.SessionId, stateB.SessionId);
            Assert.True(a.IsConnected && b.IsConnected);
            _output.WriteLine($"CONNECT: A+B in {sw.ElapsedMilliseconds} ms; A caps setModel={a.Capabilities?.SetModel} version={a.Version}");

            // Independent concurrent prompts with distinct markers.
            var markerA = $"ax-wkr-a-{Guid.NewGuid():N}";
            var markerB = $"ax-wkr-b-{Guid.NewGuid():N}";
            var sendA = a.SendAsync($"Reply with exactly: {markerA}", null, ct);
            var sendB = b.SendAsync($"Reply with exactly: {markerB}", null, ct);
            var (textA, receiptA) = await sendA;
            var (textB, receiptB) = await sendB;
            Assert.True(receiptA.RunHealthy && receiptB.RunHealthy);

            // No cross-worker history or feed leakage.
            Assert.Contains(a.History, m => m.Role == "user" && m.Text.Contains(markerA, StringComparison.Ordinal));
            Assert.DoesNotContain(a.History, m => m.Text.Contains(markerB, StringComparison.Ordinal));
            Assert.Contains(b.History, m => m.Role == "user" && m.Text.Contains(markerB, StringComparison.Ordinal));
            Assert.DoesNotContain(b.History, m => m.Text.Contains(markerA, StringComparison.Ordinal));
            var feedA = a.Feed.BuildTranscriptText();
            var feedB = b.Feed.BuildTranscriptText();
            Assert.Contains(markerA, feedA, StringComparison.Ordinal);
            Assert.DoesNotContain(markerB, feedA, StringComparison.Ordinal);
            Assert.Contains(markerB, feedB, StringComparison.Ordinal);
            Assert.DoesNotContain(markerA, feedB, StringComparison.Ordinal);

            // Selection switches the VIEW only; the other worker keeps running.
            manager.SelectWorker(b.Id);
            Assert.Same(b, manager.Selected);
            Assert.True(a.IsConnected);
            Assert.True(b.IsConnected);

            // One run per worker at a time (deterministic refusal, no queueing games).
            // Runs on the default model — before any model switching below.
            var longRun = a.SendAsync("Using powershell, run exactly: Start-Sleep 20; Write-Output done", null, ct);
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => a.SendAsync("second", null, ct));

            // Cancel A: its wait ends, its run is aborted, B is unaffected.
            await a.CancelAsync(ct);
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await longRun);
            Assert.NotNull(cancelled);
            var stateBMid = await b.GetStateAsync(ct);
            Assert.False(stateBMid.IsStreaming); // B was never asked to run anything.

            // Model operations are per-worker and confirmed; B is unaffected.
            // (Last: no inference follows the switch in this test.)
            if (a.Capabilities is { SetModel: true, AvailableModels: true })
            {
                var models = await a.GetModelsAsync(ct);
                var target = models.FirstOrDefault(m => m.Provider == stateA.Provider && m.Id != stateA.ModelId)
                    ?? models.FirstOrDefault();
                if (target is not null && !(target.Provider == stateA.Provider && target.Id == stateA.ModelId))
                {
                    var confirmed = await a.SetModelAsync(target.Provider, target.Id, ct);
                    Assert.Equal(target.Id, confirmed.ModelId);
                    var stateBAgain = await b.GetStateAsync(ct);
                    Assert.Equal(stateB.ModelId, stateBAgain.ModelId); // B untouched.
                    Assert.Equal(stateB.ThinkingLevel, stateBAgain.ThinkingLevel);
                }
            }

            _output.WriteLine($"MATRIX: concurrent prompts, selection, cancel isolation, model isolation OK in {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task KillOneWorker_OtherContinues_KilledWorkerReconnectsWithHistory()
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
            var a = manager.CreateWorker(ProjectDir(root, "proj-a"), "Agent ONE");
            var b = manager.CreateWorker(ProjectDir(root, "proj-b"), "Agent TWO");
            await a.ConnectAsync(ct);
            await b.ConnectAsync(ct);

            // Seed A's session with a persisted marker.
            var marker = $"ax-kill-{Guid.NewGuid():N}";
            await a.SendAsync($"Reply with exactly: {marker}", null, ct);

            var lost = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.ConnectionLost += (_, ex) => lost.TrySetResult(ex);

            // Kill A's Pi mid-run; B keeps working untouched.
            var pid = a.RunningProcessId;
            Assert.NotNull(pid);
            var dying = a.SendAsync("Using powershell, run exactly: Start-Sleep 30; Write-Output done", null, ct);
            await Task.Delay(TimeSpan.FromSeconds(6), ct);
            Process.GetProcessById(pid!.Value).Kill(entireProcessTree: true);
            await Assert.ThrowsAnyAsync<PiRpcTransportException>(async () => await dying);
            await lost.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(a.IsConnected);

            var markerB = $"ax-killb-{Guid.NewGuid():N}";
            var (_, receiptB) = await b.SendAsync($"Reply with exactly: {markerB}", null, ct);
            Assert.True(receiptB.RunHealthy); // B never noticed.

            // A reconnects to the SAME session with history intact.
            var state = await a.ReconnectAsync(ct);
            Assert.Equal(a.SessionId, state.SessionId);
            Assert.True(a.IsConnected);
            Assert.Contains(a.History, m => m.Role == "user" && m.Text.Contains(marker, StringComparison.Ordinal));
            var (_, receiptA2) = await a.SendAsync($"Reply with exactly: {marker}-after", null, ct);
            Assert.True(receiptA2.RunHealthy);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DisposeManager_KillsEveryWorkerProcess_NoOrphans()
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
            var a = manager.CreateWorker(ProjectDir(root, "proj-a"));
            var b = manager.CreateWorker(ProjectDir(root, "proj-b"));
            await a.ConnectAsync(ct);
            await b.ConnectAsync(ct);
            var pidA = a.RunningProcessId;
            var pidB = b.RunningProcessId;
            Assert.NotNull(pidA);
            Assert.NotNull(pidB);

            await manager.DisposeAsync();

            AssertThrowsGone(pidA);
            AssertThrowsGone(pidB);
            Assert.False(a.IsConnected);
            Assert.False(b.IsConnected);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task DuplicateSessionOwnership_IsRefused()
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

            // Forge a second worker on the SAME session (internal seam): the
            // claim registry must refuse the second connection outright.
            var impostor = new PiWorker(root, worker.Registration);
            await Assert.ThrowsAsync<InvalidOperationException>(() => impostor.ConnectAsync(ct));
            await impostor.DisposeAsync();

            // And the rightful owner still works.
            var state = await worker.GetStateAsync(ct);
            Assert.Equal(worker.SessionId, state.SessionId);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void AssertThrowsGone(int? pid)
    {
        try
        {
            var process = Process.GetProcessById(pid!.Value);
            process.Refresh();
            Assert.True(process.HasExited, $"Process {pid} should be gone after disposal.");
        }
        catch (ArgumentException)
        {
            // Process table no longer knows it: exited. Expected.
        }
    }
}
