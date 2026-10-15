using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Real-Pi verification for the Gate C tool-evidence increment: connect
/// records engine state through the host boundary, a genuine tool-using
/// prompt correlates announced/start/end under one call id with retained
/// output, prompt acceptance is captured, and the post-settle stats poll
/// refreshes the bridge binding. Ephemeral session (--no-session) in a temp
/// working directory: nothing touches the real Pi profile.
/// Shim-gated with a loud skip (WorkerRuntimeLiveTests conventions).
/// </summary>
public sealed class ToolEvidenceLiveTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    private readonly ITestOutputHelper _output;

    public ToolEvidenceLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; tool-evidence live test cannot run here.");
        return false;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-gatec-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception) { /* never fails the test */ }
    }

    [Fact]
    public async Task LiveToolPrompt_CorrelatesAnnouncedStartEnd_AndCapturesPolls()
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
            PiRpcClient? client = null;
            await using var conversation = new PiConversation(() => client = new PiRpcClient(PiRpcOptions.Default(root)));
            var feed = new ConversationFeed();
            using var bridge = new FeedBridge(feed);
            conversation.AttachBridge(bridge);

            var state = await conversation.ConnectAsync(ct);
            Assert.NotNull(client);
            // A second subscriber on the same client: observation only, no
            // competing log — the bridge tap remains the single feed source.
            using var tap = new PiEventTap(client);
            Assert.NotNull(bridge.LatestEngine);
            _output.WriteLine($"engine: model={bridge.LatestEngine.State.ModelId ?? "none"} session={bridge.LatestEngine.State.SessionId ?? "none"}");

            var marker = $"LIVEMARKER-{Guid.NewGuid():N}";
            var (text, receipt) = await conversation.SendAsync(
                "Use the powershell tool to run exactly this command and nothing else: " +
                $"Write-Output {marker} " +
                "Then reply with only the word DONE.",
                null,
                ct);

            Assert.True(receipt.Settled);

            var lifecycles = ToolCallCorrelation.Summarize(tap.Events);
            var toolCall = lifecycles.FirstOrDefault(l => l.Ended && l.Started && l.Announced);
            Assert.True(toolCall is not null, "Expected one fully correlated announced/start/end tool call.");
            Assert.Equal("powershell", toolCall.ToolName);
            _output.WriteLine($"correlated: id={toolCall.CallId} progress={toolCall.ProgressUpdates}");

            var retained = bridge.GetRetainedText(toolCall.CallId);
            Assert.NotNull(retained);
            Assert.Contains(marker, retained, StringComparison.Ordinal);

            // Prompt acceptance capture is proven deterministically (the loopback
            // PromptAndWait hook test) and wired here in SendAsync; acceptance
            // responses bypass subscribers by protocol design, so no streamed
            // tap — test or bridge — can ever observe them. Live proof of the
            // record boundary is the stats/state binding below.
            Assert.NotNull(bridge.LatestStats);
            _output.WriteLine($"stats: tools={bridge.LatestStats.ToolCalls} tokens={bridge.LatestStats.TotalTokens}");
            Assert.True(bridge.LatestStats.ToolCalls >= 1);
            Assert.Contains("DONE", text, StringComparison.OrdinalIgnoreCase);
            _ = state;
        }
        finally
        {
            Cleanup(root);
        }
    }
}
