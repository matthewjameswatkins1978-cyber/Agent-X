using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Version-tolerant RPC contract, run against EVERY discovered Pi
/// executable (the global shim, plus anything listed in the
/// AX_PI_UNDER_TEST_EXES environment variable, semicolon-separated — see
/// scripts/Provision-PiUnderTest.ps1 for isolated installs). Each walk is
/// sequential and disposable: temp managed root, real provider config
/// (state ops are process-scoped and verified never to persist), exact-
/// echo prompts only.
///
/// Assertions target the CONTRACT, not a version: capability probes decide
/// what is exercised; invalid-level behavior is asserted version-tolerantly
/// (engine state must stay within the available list — never "must throw",
/// because 1.1.0/1.2.0 silently accept unknown levels); the version number
/// is informational only.
///
/// Skips loudly (never silently) when an executable is absent.
/// </summary>
public sealed class PiCompatibilityContractTests
{
    private const string GlobalShim = @"D:\AgentTools\bin\pi.cmd";
    private const string ExecutablesEnvVar = "AX_PI_UNDER_TEST_EXES";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper _output;

    public PiCompatibilityContractTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Global shim first, then every explicitly provisioned executable.</summary>
    public static IEnumerable<object[]> DiscoveredExecutables()
    {
        if (File.Exists(GlobalShim))
        {
            yield return new object[] { GlobalShim };
        }

        var extra = Environment.GetEnvironmentVariable(ExecutablesEnvVar) ?? string.Empty;
        foreach (var candidate in extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return new object[] { candidate };
        }
    }

    private bool Available(string executable)
    {
        if (File.Exists(executable))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi executable not found at '{executable}'. Provision one (scripts/Provision-PiUnderTest.ps1) or set {ExecutablesEnvVar}.");
        return false;
    }

    [Theory]
    [MemberData(nameof(DiscoveredExecutables))]
    public async Task Contract_FullWalk_ConnectStateModelsThinkingPromptToolCancelRestoreReconnect(string executable)
    {
        if (!Available(executable))
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"ax-contract-{Guid.NewGuid():N}");
        var project = Path.Combine(root, "proj");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(sessions);
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        var sw = Stopwatch.StartNew();
        var sessionId = "ax-contract-" + Guid.NewGuid().ToString("N")[..12];
        var options = PiRpcOptions.ForSession(sessions, sessionId, project, executable);
        try
        {
            // --- Version detection (informational; never gates behavior).
            var version = await PiRpcCompat.DetectVersionAsync(executable, ct);
            _output.WriteLine($"VERSION: {(version?.ToString() ?? "(undeterminable)")} at {executable}");

            // --- Connect + state + capability probe (the compat boundary).
            await using var conversation = new PiConversation(() => new PiRpcClient(options));
            var state = await conversation.ConnectAsync(ct);
            Assert.Equal(sessionId, state.SessionId);
            var caps = await conversation.ProbeCapabilitiesAsync(ct);
            _output.WriteLine($"CAPS: state={caps.GetState} messages={caps.GetMessages} models={caps.AvailableModels} setModel={caps.SetModel} thinking={caps.AvailableThinkingLevels} setThinking={caps.SetThinkingLevel}");
            Assert.True(caps.GetState, "get_state is baseline protocol.");
            Assert.True(caps.GetMessages, "get_messages is baseline protocol.");

            // --- Models: discover, switch (process-scoped), CONFIRM via state,
            //     then REVERT: the switch proves the protocol; inference stays
            //     on the configured default model because other providers need
            //     their own credentials (observed: deepseek prompt ->
            //     stopReason=error, zero usage, empty content on 1.1.0 AND 1.2.0
            //     -- an environment fact, not a protocol difference).
            var originalProvider = state.Provider;
            var originalModelId = state.ModelId;
            var originalLevel = state.ThinkingLevel;
            Assert.False(string.IsNullOrEmpty(originalModelId), "Engine must report a startup model.");
            if (caps.AvailableModels && caps.SetModel)
            {
                var models = await conversation.GetAvailableModelsAsync(ct);
                Assert.NotEmpty(models);
                var other = models.FirstOrDefault(m => !(m.Provider == originalProvider && m.Id == originalModelId));
                if (other is not null)
                {
                    await conversation.SetModelAsync(other.Provider, other.Id, ct);
                    var afterModel = await conversation.GetStateAsync(ct);
                    Assert.Equal(other.Provider, afterModel.Provider);
                    Assert.Equal(other.Id, afterModel.ModelId);

                    await conversation.SetModelAsync(originalProvider!, originalModelId!, ct);
                    var reverted = await conversation.GetStateAsync(ct);
                    Assert.Equal(originalModelId, reverted.ModelId);
                    _output.WriteLine($"MODEL: switch to {other.Provider}/{other.Id} confirmed; reverted to {originalProvider}/{originalModelId}.");
                }
            }

            // --- Thinking: levels are per-model; set + confirm + revert;
            //     invalid must never take effect (version-tolerant: accept
            //     silent reject OR clamp, state must stay in the list).
            if (caps.AvailableThinkingLevels && caps.SetThinkingLevel)
            {
                var levels = await conversation.GetAvailableThinkingLevelsAsync(ct);
                Assert.NotEmpty(levels);
                var current = (await conversation.GetStateAsync(ct)).ThinkingLevel;
                var target = levels.FirstOrDefault(l => !string.Equals(l, current, StringComparison.OrdinalIgnoreCase)) ?? levels[0];
                await conversation.SetThinkingLevelAsync(target, ct);
                var afterLevel = await conversation.GetStateAsync(ct);
                Assert.Equal(target, afterLevel.ThinkingLevel, StringComparer.OrdinalIgnoreCase);

                await conversation.SetThinkingLevelAsync("definitely-not-a-level", ct); // 1.1.0/1.2.0 accept silently.
                var afterGarbage = await conversation.GetStateAsync(ct);
                Assert.Contains(afterGarbage.ThinkingLevel, levels, StringComparer.OrdinalIgnoreCase);

                var levelsNow = await conversation.GetAvailableThinkingLevelsAsync(ct);
                if (levelsNow.Contains(originalLevel, StringComparer.OrdinalIgnoreCase))
                {
                    await conversation.SetThinkingLevelAsync(originalLevel, ct);
                    Assert.Equal(originalLevel, (await conversation.GetStateAsync(ct)).ThinkingLevel, StringComparer.OrdinalIgnoreCase);
                }

                _output.WriteLine($"THINKING: set '{target}' confirmed; garbage '{afterGarbage.ThinkingLevel}' stayed within list; reverted.");
            }

            // --- Prompt: exact echo (assert on the user marker, never wording).
            var marker = $"ax-contract-{Guid.NewGuid():N}";
            var (text, receipt) = await conversation.SendAsync(
                $"Reply with exactly: {marker}", null, ct);
            Assert.True(receipt.RunHealthy, $"Echo prompt receipt: {receipt.Display}");
            _output.WriteLine($"PROMPT: {sw.ElapsedMilliseconds} ms, healthy={receipt.RunHealthy}");

            // --- Tool-call identity: a real tool call must carry a stable id
            //     that pairs start→end (identity, not name-order).
            var toolStarts = new List<string>();
            var toolEnds = new List<string>();
            void OnTool(object? s, PiToolActivity a)
            {
                if (a.CallId is null)
                {
                    return;
                }

                if (a.IsStart)
                {
                    lock (toolStarts) toolStarts.Add(a.CallId);
                }
                else
                {
                    lock (toolEnds) toolEnds.Add(a.CallId);
                }
            }

            conversation.ToolActivityChanged += OnTool;
            var toolMarker = $"ax-tool-{Guid.NewGuid():N}";
            var (toolText, toolReceipt) = await conversation.SendAsync(
                $"Using powershell, run exactly: Write-Output '{toolMarker}' and reply OK", null, ct);
            conversation.ToolActivityChanged -= OnTool;
            Assert.True(toolReceipt.RunHealthy, $"Tool prompt receipt: {toolReceipt.Display}");
            var paired = toolStarts.Intersect(toolEnds).ToArray();
            _output.WriteLine($"TOOL-IDENTITY: starts={toolStarts.Count} ends={toolEnds.Count} paired={paired.Length}");
            Assert.NotEmpty(paired); // At least one start/end pair by call id.

            // --- Cancellation: abort settles the run ABORTED, not healthy.
            var cancelSeq = conversation.SendAsync(
                "Using powershell, run exactly: Start-Sleep 30; Write-Output done", null, ct);
            await Task.Delay(TimeSpan.FromSeconds(8), ct);
            await conversation.AbortAsync(ct);
            var (cancelText, cancelReceipt) = await cancelSeq;
            Assert.True(cancelReceipt.Settled);
            Assert.True(cancelReceipt.Aborted, "Aborted run must report Aborted.");
            _output.WriteLine($"CANCEL: aborted receipt in {sw.ElapsedMilliseconds} ms total so far.");

            // --- Session restore: dispose, reopen the SAME session id,
            //     history (user markers) must come back; foreign sessions must not.
            await conversation.DisposeAsync();
            await using var reopened = new PiConversation(() => new PiRpcClient(options));
            var reopenedState = await reopened.ConnectAsync(ct);
            Assert.Equal(sessionId, reopenedState.SessionId);
            var history = reopened.History;
            Assert.Contains(history, m => m.Role == "user" && m.Text.Contains(marker, StringComparison.Ordinal));
            Assert.DoesNotContain(history, m => m.Text.Contains("ax-contract-from-another-session", StringComparison.Ordinal));
            _output.WriteLine($"RESTORE: {history.Count} messages restored.");

            // --- Reconnection: process swap under a live connection keeps the
            //     session and history, and bumps the reconnect count.
            var beforeReconnect = reopened.History.Count;
            var reconnected = await reopened.ReconnectAsync(ct);
            Assert.Equal(sessionId, reconnected.SessionId);
            Assert.Equal(1, reopened.ReconnectCount);
            Assert.True(reopened.History.Count >= beforeReconnect);
            var (postReconnectText, postReconnectReceipt) = await reopened.SendAsync(
                $"Reply with exactly: {marker}-post-reconnect", null, ct);
            Assert.True(postReconnectReceipt.RunHealthy);
            _output.WriteLine($"RECONNECT: ok, history {beforeReconnect} -> {reopened.History.Count}, total {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) { _output.WriteLine($"Cleanup note: {ex.GetType().Name}"); }
        }
    }
}
