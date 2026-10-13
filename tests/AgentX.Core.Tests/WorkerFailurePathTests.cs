using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// C3 reliability corrections — deterministic fault injection and contract
/// units (no Pi, no network): persistence-failure rollback for create and
/// remove, session-identity validation (reported / missing / contradictory),
/// and capability-probe classification (unsupported optional feature vs
/// broken connection). The CONNECTED-worker orphan case lives in
/// WorkerFailureLiveTests.
/// </summary>
public sealed class WorkerFailurePathTests
{
    private static string TempRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-failpath-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Deterministic save failure: an exclusive lock on the registry file makes the atomic move fail.</summary>
    private static FileStream LockStore(string root)
        => new(WorkerStore.StorePath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static JsonDocument OkState()
        => JsonDocument.Parse("""{"success":true,"data":{"model":{"id":"m","provider":"p"},"thinkingLevel":"medium","sessionId":"axw-x"}}""");

    private static JsonDocument EmptyState()
        => JsonDocument.Parse("""{"success":true,"data":{"sessionId":"axw-x"}}""");

    private static JsonDocument Rejected()
        => JsonDocument.Parse("""{"success":false,"error":"unknown command"}""");

    private static PiEngineState State(string? sessionId, string? sessionFile)
        => new(null, null, null, "medium", false, false, sessionId, 0, 0, sessionFile);

    [Fact]
    public async Task CreateSaveFailure_RollsBackCompletely_ByteIdenticalRetryWorks()
    {
        var root = TempRoot("create");
        try
        {
            await using var manager = new PiWorkerManager(root);
            var first = manager.CreateWorker(ProjectDir(root, "one")); // creates workers.json
            var beforeBytes = File.ReadAllBytes(WorkerStore.StorePath(root));

            Exception? createFailure;
            using (LockStore(root))
            {
                createFailure = Record.Exception(() => manager.CreateWorker(ProjectDir(root, "two")));
            }

            // Deterministic fault: the locked registry refuses the atomic move.
            Assert.True(createFailure is IOException or UnauthorizedAccessException,
                $"Expected a persistence failure, got: {createFailure?.GetType().Name}");

            // No ghost in any direction: collections, selection, and the
            // bytes on disk are exactly what they were before the failure.
            var survivor = Assert.Single(manager.Workers);
            Assert.Equal(first.Id, survivor.Id);
            Assert.Same(first, manager.Selected);
            Assert.Equal(beforeBytes, File.ReadAllBytes(WorkerStore.StorePath(root)));
            Assert.Single(WorkerStore.Load(root).List());

            // Recovery: the same create succeeds once the lock is gone.
            var second = manager.CreateWorker(ProjectDir(root, "two"));
            Assert.Equal(2, manager.Workers.Count);
            Assert.NotEqual(first.Id, second.Id);
            Assert.Equal(2, WorkerStore.Load(root).List().Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveSaveFailure_KeepsRegistrationIndexAndSelection_RetryCompletes()
    {
        var root = TempRoot("remove");
        try
        {
            string keepId;
            string removeId;
            await using (var setup = new PiWorkerManager(root))
            {
                keepId = setup.CreateWorker(ProjectDir(root, "keep"), "KEEP").Id;
                removeId = setup.CreateWorker(ProjectDir(root, "remove"), "REMOVE").Id;
            }

            await using var manager = new PiWorkerManager(root);
            manager.SelectWorker(removeId);
            var beforeBytes = File.ReadAllBytes(WorkerStore.StorePath(root));

            using (LockStore(root))
            {
                var removeFailure = await Record.ExceptionAsync(() => manager.RemoveWorkerAsync(removeId));
                Assert.True(removeFailure is IOException or UnauthorizedAccessException,
                    $"Expected a persistence failure, got: {removeFailure?.GetType().Name}");
            }

            // Rollback: worker still owned, still persisted, selection intact —
            // nothing about the failed save became half-applied.
            Assert.Equal(2, manager.Workers.Count);
            Assert.Contains(manager.Workers, w => w.Id == removeId);
            Assert.Equal(beforeBytes, File.ReadAllBytes(WorkerStore.StorePath(root)));
            Assert.Equal(removeId, manager.Selected!.Id);
            Assert.Equal(2, WorkerStore.Load(root).List().Count);

            // Recovery: retry once the lock is gone (dispose is idempotent).
            await manager.RemoveWorkerAsync(removeId);
            Assert.Single(manager.Workers);
            Assert.Single(WorkerStore.Load(root).List());
            Assert.Equal(keepId, manager.Selected!.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidateSessionIdentity_MatchAndFileAlternative_Pass()
    {
        const string expected = "axw-aabbccdd";

        // Primary proof: the engine reports our exact session id.
        PiWorker.ValidateSessionIdentity(expected, "W", State(expected, null));

        // Alternative proof: no session id reported, but Pi's own session
        // file name carries the expected id (trustworthy ownership evidence).
        PiWorker.ValidateSessionIdentity(expected, "W", State(null, @"C:\sessions\1712345678_axw-aabbccdd.jsonl"));
        PiWorker.ValidateSessionIdentity(expected, "W", State(null, @"C:\sessions\axw-aabbccdd.jsonl"));
    }

    [Fact]
    public void ValidateSessionIdentity_MismatchAndUnverified_Refuse()
    {
        const string expected = "axw-aabbccdd";

        var mismatch = Assert.Throws<PiRpcTransportException>(
            () => PiWorker.ValidateSessionIdentity(expected, "W", State("axw-ffffffff", null)));
        Assert.Contains("foreign history", mismatch.Message, StringComparison.Ordinal);

        var wrongFile = Assert.Throws<PiRpcTransportException>(
            () => PiWorker.ValidateSessionIdentity(expected, "W", State(null, @"C:\sessions\1712345678_axw-other.jsonl")));
        Assert.Contains("does not carry expected id", wrongFile.Message, StringComparison.Ordinal);

        var absent = Assert.Throws<PiRpcTransportException>(
            () => PiWorker.ValidateSessionIdentity(expected, "W", State(null, null)));
        Assert.Contains("unverified", absent.Message, StringComparison.Ordinal);

        // Contradictory: an id AND a file that disagrees — the id decides,
        // and a wrong id is refused regardless of file.
        var both = Assert.Throws<PiRpcTransportException>(
            () => PiWorker.ValidateSessionIdentity(expected, "W", State("axw-ffffffff", @"C:\sessions\axw-aabbccdd.jsonl")));
        Assert.Contains("foreign history", both.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_BaselineRejection_ThrowsBroken_NotUnsupported()
    {
        // get_state answering success:false is a broken connection, never a
        // capability answer: connect must fail rather than report "no state".
        await Assert.ThrowsAsync<PiRpcTransportException>(() => PiRpcCompat.ProbeAsync(
            (cmd, _, _) => Task.FromResult(cmd == "get_state" ? Rejected() : OkState())));
    }

    [Fact]
    public async Task Probe_BaselineTimeout_ThrowsBroken_NotUnsupported()
    {
        await Assert.ThrowsAsync<PiRpcTransportException>(() => PiRpcCompat.ProbeAsync(
            async (cmd, _, ct) =>
            {
                if (cmd == "get_state")
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return OkState();
            },
            probeTimeout: TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task Probe_OptionalRejectionAndTimeout_FalseWithoutFailingConnection()
    {
        var caps = await PiRpcCompat.ProbeAsync(
            async (cmd, _, ct) =>
            {
                if (cmd == "get_available_models")
                {
                    return Rejected(); // unsupported optional query
                }

                if (cmd == "get_available_thinking_levels")
                {
                    await Task.Delay(Timeout.Infinite, ct); // unresponsive optional query
                }

                return OkState();
            },
            probeTimeout: TimeSpan.FromMilliseconds(150));

        Assert.True(caps.GetState);
        Assert.True(caps.GetMessages);
        Assert.False(caps.AvailableModels);       // rejected → unsupported (fail closed)
        Assert.False(caps.AvailableThinkingLevels); // timeout → unsupported/indeterminate (fail closed)
        Assert.True(caps.SetModel);               // idempotent set against healthy state still probed
        Assert.True(caps.SetThinkingLevel);
    }

    [Fact]
    public async Task Probe_TransportFaultOnOptional_Propagates_NotUnsupported()
    {
        // A dead pipe during an optional probe is a broken connection, not
        // evidence that the command is unsupported.
        await Assert.ThrowsAsync<PiRpcTransportException>(() => PiRpcCompat.ProbeAsync(
            (cmd, _, _) => cmd == "get_available_models"
                ? throw new PiRpcTransportException("pipe died")
                : Task.FromResult(OkState())));
    }

    [Fact]
    public async Task Probe_AllHealthy_AllTrue_AndIdempotenceGateFailsClosed()
    {
        var caps = await PiRpcCompat.ProbeAsync((_, _, _) => Task.FromResult(OkState()));
        Assert.True(caps.GetState);
        Assert.True(caps.GetMessages);
        Assert.True(caps.AvailableModels);
        Assert.True(caps.SetModel);
        Assert.True(caps.AvailableThinkingLevels);
        Assert.True(caps.SetThinkingLevel);

        // Without model/thinking in the state there is nothing idempotent to
        // re-apply: set capabilities stay false (fail closed), queries true.
        var gated = await PiRpcCompat.ProbeAsync((_, _, _) => Task.FromResult(EmptyState()));
        Assert.True(gated.GetState);
        Assert.False(gated.SetModel);
        Assert.False(gated.SetThinkingLevel);
    }
}
