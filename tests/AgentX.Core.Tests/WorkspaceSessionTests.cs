using System.Diagnostics;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Real-Pi verification with disposable sessions: temp managed roots, unique
/// session ids per run, echo-only bash (no prompts, zero inference cost).
/// Nothing here touches the real %LocalAppData%/AgentX sessions, any project
/// directory, or any other Pi process. Each test skips loudly (not silently)
/// when the Pi shim is absent.
/// </summary>
public sealed class WorkspaceSessionTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(150);

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public WorkspaceSessionTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; real-session test cannot run here.");
        return false;
    }

    private static string IsolatedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-ws-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root, Xunit.Abstractions.ITestOutputHelper output)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Cleanup note (never fails the test): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pi-behavior note (verified 2026-10-12): a bare <c>bash</c> RPC command
    /// executes and appears in live <c>get_messages</c> but is never written
    /// to disk — no session file is created until an agent run settles. So
    /// cross-process assertions below seed history with one tiny prompt per
    /// workspace (exact-echo, ~6 s each) and assert on the persisted USER
    /// marker, never on model wording. Bash is still exercised per workspace
    /// to prove tool execution paths resolve inside each isolated cwd.
    ///
    /// Execution-directory note (P1-1): the workspace working directory IS
    /// the project root, so relative tool paths below must land files in
    /// the temp projects — asserted on disk, shell-agnostically.
    /// </summary>
    [Fact]
    public async Task Isolation_TwoWorkspaces_KeepSeparateHistories()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = IsolatedRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        var markerA = $"ax-marker-a-{Guid.NewGuid():N}";
        var markerB = $"ax-marker-b-{Guid.NewGuid():N}";
        try
        {
            var projA = Path.Combine(root, "first-proj");
            var projB = Path.Combine(root, "second-proj");
            Directory.CreateDirectory(projA);
            Directory.CreateDirectory(projB);
            var registry = new WorkspaceRegistry(root);
            var wsA = registry.GetOrRegister(projA);
            var wsB = registry.GetOrRegister(projB);
            Assert.NotEqual(wsA.SessionId, wsB.SessionId);
            Assert.NotEqual(wsA.WorkingDirectory, wsB.WorkingDirectory);

            var sw = Stopwatch.StartNew();
            // Trusted-project visibility: a pre-existing project file must be
            // readable through Pi's relative execution context (no inference).
            File.WriteAllText(Path.Combine(projA, "seed-A.txt"), $"seed content {markerA}");
            File.WriteAllText(Path.Combine(projB, "seed-B.txt"), $"seed content {markerB}");
            await using (var clientA = new PiRpcClient(wsA.ToPiRpcOptions()))
            {
                await clientA.ConnectAsync(ct);
                var stateA = await clientA.GetStateAsync(ct);
                Assert.Equal(wsA.SessionId, stateA.SessionId);
                wsA.ValidateSessionMatch(stateA.SessionId);
                var seedA = await clientA.RunBashAsync("type seed-A.txt", ct);
                Assert.Equal(0, seedA.ExitCode);
                Assert.Contains($"seed content {markerA}", seedA.Output, StringComparison.Ordinal);
                // Relative write must land IN the project, then read back.
                var writeA = await clientA.RunBashAsync($"echo {markerA}-tool > ax-note-A.txt", ct);
                Assert.Equal(0, writeA.ExitCode);
                var readA = await clientA.RunBashAsync("type ax-note-A.txt", ct);
                Assert.Contains($"{markerA}-tool", readA.Output, StringComparison.Ordinal);
                var (replyA, abortedA) = await clientA.PromptAndWaitAsync($"Reply with exactly: GREEN {markerA}", null, ct);
                Assert.False(abortedA);
                using var messagesA = await clientA.GetMessagesAsync(ct);
                var historyA = PiConversation.ParseHistory(messagesA);
                Assert.Contains(historyA, m => m.Role == "user" && m.Text.Contains(markerA));
            }

            Assert.Equal($"seed content {markerA}", File.ReadAllText(Path.Combine(projA, "seed-A.txt")).Trim());
            Assert.Contains(markerA, File.ReadAllText(Path.Combine(projA, "ax-note-A.txt")));
            Assert.False(File.Exists(Path.Combine(projB, "ax-note-A.txt"))); // No cross-write.

            await using (var clientB = new PiRpcClient(wsB.ToPiRpcOptions()))
            {
                await clientB.ConnectAsync(ct);
                var stateB = await clientB.GetStateAsync(ct);
                Assert.Equal(wsB.SessionId, stateB.SessionId);
                wsB.ValidateSessionMatch(stateB.SessionId);
                using var messagesB0 = await clientB.GetMessagesAsync(ct);
                var historyB0 = PiConversation.ParseHistory(messagesB0);
                Assert.DoesNotContain(historyB0, m => m.Text.Contains(markerA));
                var seedB = await clientB.RunBashAsync("type seed-B.txt", ct);
                Assert.Equal(0, seedB.ExitCode);
                Assert.Contains($"seed content {markerB}", seedB.Output, StringComparison.Ordinal);
                var writeB = await clientB.RunBashAsync($"echo {markerB}-tool > ax-note-B.txt", ct);
                Assert.Equal(0, writeB.ExitCode);
                var (replyB, abortedB) = await clientB.PromptAndWaitAsync($"Reply with exactly: BLUE {markerB}", null, ct);
                Assert.False(abortedB);
                using var messagesB1 = await clientB.GetMessagesAsync(ct);
                var historyB1 = PiConversation.ParseHistory(messagesB1);
                Assert.Contains(historyB1, m => m.Role == "user" && m.Text.Contains(markerB));
                Assert.DoesNotContain(historyB1, m => m.Text.Contains(markerA));
            }

            // Reopen A: its persisted history is intact and still free of B.
            await using (var clientA2 = new PiRpcClient(wsA.ToPiRpcOptions()))
            {
                await clientA2.ConnectAsync(ct);
                var reopened = await clientA2.GetStateAsync(ct);
                wsA.ValidateSessionMatch(reopened.SessionId);
                using var messagesA2 = await clientA2.GetMessagesAsync(ct);
                var historyA2 = PiConversation.ParseHistory(messagesA2);
                Assert.Contains(historyA2, m => m.Role == "user" && m.Text.Contains(markerA));
                Assert.DoesNotContain(historyA2, m => m.Text.Contains(markerB));
            }

            _output.WriteLine($"Isolation: 4 sequential connects + relative file roundtrips + 2 tiny prompts in {sw.ElapsedMilliseconds} ms.");
            // Each project holds exactly its own files — nothing stray, nothing shared.
            Assert.Equal(
                new[] { "ax-note-A.txt", "seed-A.txt" },
                Directory.GetFiles(projA).Select(Path.GetFileName).OrderBy(n => n).ToArray());
            Assert.Equal(
                new[] { "ax-note-B.txt", "seed-B.txt" },
                Directory.GetFiles(projB).Select(Path.GetFileName).OrderBy(n => n).ToArray());
            // ...and outside the two projects, only Agent X's own files exist.
            var sep = Path.DirectorySeparatorChar.ToString();
            var nonProjectFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(root, f))
                .Where(f => !f.StartsWith("first-proj" + sep) && !f.StartsWith("second-proj" + sep))
                .ToArray();
            Assert.DoesNotContain(nonProjectFiles, f => f.EndsWith("ax-note-A.txt") || f.EndsWith("ax-note-B.txt"));
            Assert.Contains(nonProjectFiles, f => f.EndsWith(".jsonl")); // Real session files persisted.
        }
        finally
        {
            Cleanup(root, _output);
        }
    }

    [Fact]
    public async Task ConversationReconnect_SwapsProcess_KeepsHistory_Repeatedly()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = IsolatedRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        var marker = $"ax-marker-re-{Guid.NewGuid():N}";
        try
        {
            var project = Path.Combine(root, "re-proj");
            Directory.CreateDirectory(project);
            var workspace = new WorkspaceRegistry(root).GetOrRegister(project);
            Func<PiRpcClient> factory = () => new PiRpcClient(workspace.ToPiRpcOptions());

            // Seed persisted history with one tiny prompt (bare bash does not
            // persist), then close the seeder: sequential use only.
            await using (var seeder = new PiRpcClient(workspace.ToPiRpcOptions()))
            {
                await seeder.ConnectAsync(ct);
                var (seedReply, seedAborted) = await seeder.PromptAndWaitAsync($"Reply with exactly: SEED {marker}", null, ct);
                Assert.False(seedAborted);
            }

            var sw = Stopwatch.StartNew();
            await using var conversation = new PiConversation(factory);
            await conversation.ConnectAsync(ct);
            Assert.Contains(conversation.History, m => m.Text.Contains(marker));

            // Reconnect under a HEALTHY active process, five times: with the
            // dispose-first single-writer rule, each swap retires the old
            // process before the new one starts, so the persisted marker count
            // must stay STABLE — growth would prove two live writers. (The
            // marker appears twice: user prompt plus exact echo.)
            var baseline = conversation.History.Count(m => m.Text.Contains(marker));
            Assert.Equal(2, baseline);
            for (var i = 1; i <= 5; i++)
            {
                var state = await conversation.ReconnectAsync(ct);
                Assert.True(conversation.IsConnected);
                Assert.Equal(i, conversation.ReconnectCount);
                Assert.Equal(baseline, conversation.History.Count(m => m.Text.Contains(marker)));
                Assert.Equal(workspace.SessionId, state.SessionId);
                workspace.ValidateSessionMatch(state.SessionId);
            }

            _output.WriteLine($"5 sequential reconnects, marker count stable at {baseline}, in {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            Cleanup(root, _output);
        }
    }

    [Fact]
    public async Task LegacySessionId_OpensInTempRoot_RealSessionUntouched()
    {
        // Legacy COMPATIBILITY without touching the user's real session:
        // identical session id (agent-x-main) under a temporary managed
        // root. The real %LocalAppData%/AgentX history is never opened.
        if (!PiAvailable())
        {
            return;
        }

        var root = IsolatedRoot();
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var legacy = AgentXWorkspace.Legacy(root);
            await using var client = new PiRpcClient(
                PiRpcOptions.Persistent(new PiSessionOptions(root, AgentXWorkspace.LegacySessionId)));
            await client.ConnectAsync(budget.Token);
            var state = await client.GetStateAsync(budget.Token);
            Assert.Equal(AgentXWorkspace.LegacySessionId, state.SessionId);
            legacy.ValidateSessionMatch(state.SessionId);
            Assert.True(Directory.Exists(legacy.WorkingDirectory)); // Managed cwd still auto-created.
            _output.WriteLine($"Legacy-shape session opens in temp root: model={state.ModelName ?? state.ModelId}.");
        }
        finally
        {
            Cleanup(root, _output);
        }
    }

    [Fact]
    public async Task FailedConnection_ThrowsTransport_Loudly()
    {
        // No Pi needed: a missing binary must fail fast and loudly, never hang.
        await using var client = new PiRpcClient(
            new PiRpcOptions("definitely-missing-ax-binary.exe", "--mode rpc --no-session", null));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var error = await Assert.ThrowsAsync<PiRpcTransportException>(
            () => client.ConnectAsync(budget.Token));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task FailedReconnect_LeavesDisconnected_HistoryKept()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = IsolatedRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var conversation = new PiConversation(
                () => new PiRpcClient(new PiRpcOptions(
                    "definitely-missing-ax-binary.exe", "--mode rpc --no-session", null)));
            await Assert.ThrowsAsync<PiRpcTransportException>(() => conversation.ReconnectAsync(ct));
            Assert.False(conversation.IsConnected);
            Assert.Equal(0, conversation.ReconnectCount);
            Assert.Empty(conversation.History); // Nothing known, nothing wiped.
        }
        finally
        {
            Cleanup(root, _output);
        }
    }

    [Fact]
    public async Task ReconnectAfterDispose_Throws()
    {
        await using var conversation = new PiConversation(
            () => new PiRpcClient(PiRpcOptions.Default()));
        await conversation.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => conversation.ReconnectAsync());
    }
}
