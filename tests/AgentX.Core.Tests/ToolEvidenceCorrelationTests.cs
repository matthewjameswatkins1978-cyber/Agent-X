using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Gate C tool-evidence increment: invocation/result correlation, host poll
/// capture boundaries, and the unavailable-vs-truncated distinction.
/// Deterministic and headless (pipe loopback mirrors SessionStatsPollTests;
/// no live process, no GUI). Live Pi coverage lives in ToolEvidenceLiveTests.
/// </summary>
public sealed class ToolEvidenceCorrelationTests
{
    private static readonly DateTime T0 = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    // ---- correlation ledger -------------------------------------------------

    [Fact]
    public void Summarize_HappyPath_AllStagesPairedInOrder()
    {
        var events = new AgentXEvent[]
        {
            new ToolCallAnnounced(T0, "c1", "powershell"),
            new ToolStarted(T0, "c1", "powershell", "{}", false, "no args"),
            new ToolProgress(T0, "c1", "powershell", "part", 4, false, "part"),
            new ToolEnded(T0, "c1", "powershell", false, 10, 0, false, false, 3, 3, "out", "out", true),
        };

        var lifecycle = Assert.Single(ToolCallCorrelation.Summarize(events));

        Assert.Equal("c1", lifecycle.CallId);
        Assert.Equal("powershell", lifecycle.ToolName);
        Assert.True(lifecycle.Announced);
        Assert.True(lifecycle.Started);
        Assert.True(lifecycle.Ended);
        Assert.Equal(1, lifecycle.ProgressUpdates);
        Assert.False(lifecycle.EndWithoutStart);
        Assert.False(lifecycle.StartWithoutEnd);
        Assert.False(lifecycle.DuplicateStart);
        Assert.False(lifecycle.DuplicateEnd);
    }

    [Fact]
    public void Summarize_EndWithoutStart_IsFlaggedNotHidden()
    {
        var events = new AgentXEvent[]
        {
            new ToolEnded(T0, "orphan", "powershell", false, 5, null, false, false, 0, 0, string.Empty, string.Empty, false),
        };

        var lifecycle = Assert.Single(ToolCallCorrelation.Summarize(events));

        Assert.True(lifecycle.Ended);
        Assert.False(lifecycle.Started);
        Assert.True(lifecycle.EndWithoutStart);
        Assert.False(lifecycle.StartWithoutEnd);
    }

    [Fact]
    public void Summarize_StartWithoutEnd_IsFlaggedNotHidden()
    {
        var events = new AgentXEvent[]
        {
            new ToolStarted(T0, "open", "powershell", "{}", false, "no args"),
        };

        var lifecycle = Assert.Single(ToolCallCorrelation.Summarize(events));

        Assert.True(lifecycle.StartWithoutEnd);
        Assert.False(lifecycle.EndWithoutStart);
    }

    [Fact]
    public void Summarize_DuplicateStartsAndEnds_AreCounted()
    {
        var events = new AgentXEvent[]
        {
            new ToolStarted(T0, "c2", "powershell", "{}", false, "no args"),
            new ToolStarted(T0, "c2", "powershell", "{}", false, "no args"),
            new ToolEnded(T0, "c2", "powershell", false, 1, 0, false, false, 1, 1, "x", "x", true),
            new ToolEnded(T0, "c2", "powershell", false, 1, 0, false, false, 1, 1, "x", "x", true),
        };

        var lifecycle = Assert.Single(ToolCallCorrelation.Summarize(events));

        Assert.Equal(2, lifecycle.StartCount);
        Assert.Equal(2, lifecycle.EndCount);
        Assert.True(lifecycle.DuplicateStart);
        Assert.True(lifecycle.DuplicateEnd);
    }

    [Fact]
    public void Summarize_EmptyCallId_IsExcluded()
    {
        // Withheld or absent ids cannot correlate: they are skipped, not grouped.
        var events = new AgentXEvent[]
        {
            new ToolStarted(T0, string.Empty, "powershell", "{}", false, "no args"),
            new ToolEnded(T0, string.Empty, "powershell", false, 1, 0, false, false, 0, 0, string.Empty, string.Empty, true),
        };

        Assert.Empty(ToolCallCorrelation.Summarize(events));
    }

    [Fact]
    public void Summarize_NameComesFromFirstNonEmptyObservation_MultiCallOrder()
    {
        var events = new AgentXEvent[]
        {
            new ToolEnded(T0, "second", "bash-tool", false, 1, 0, false, false, 0, 0, string.Empty, string.Empty, true),
            new ToolCallAnnounced(T0, "first", "powershell"),
        };

        var lifecycles = ToolCallCorrelation.Summarize(events);

        Assert.Equal(2, lifecycles.Count);
        Assert.Equal("second", lifecycles[0].CallId);
        Assert.Equal("first", lifecycles[1].CallId);
        Assert.Equal("bash-tool", lifecycles[0].ToolName);
    }

    [Fact]
    public void Summarize_NullEvents_ThrowsLoudly()
    {
        Assert.Throws<ArgumentNullException>(() => ToolCallCorrelation.Summarize(null!));
    }

    // ---- state poll boundary --------------------------------------------------

    private const string StateData =
        "\"model\":{\"id\":\"meta/muse-spark\",\"name\":\"Spark\",\"provider\":\"meta\"}," +
        "\"thinkingLevel\":\"high\",\"isStreaming\":false,\"isCompacting\":false," +
        "\"sessionId\":\"sess-abc123\",\"messageCount\":4,\"pendingMessageCount\":0";

    [Fact]
    public void RecordStateResponse_WithAttachedBridge_UpdatesLatestEngine_WithoutRows()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient());
        Assert.Null(bridge.LatestEngine);
        var rowsBefore = feed.Count;

        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{" + StateData + "}}");

        var state = PiConversation.RecordStateResponse(bridge, response);

        Assert.NotNull(state);
        Assert.Equal("meta/muse-spark", state.ModelId);
        Assert.Equal("sess-abc123", state.SessionId);
        Assert.Equal(4, state.MessageCount);
        Assert.NotNull(bridge.LatestEngine);
        Assert.Equal(state.ModelId, bridge.LatestEngine.State.ModelId);
        Assert.Equal(state.SessionId, bridge.LatestEngine.State.SessionId);
        Assert.Equal(rowsBefore, feed.Count);
    }

    [Fact]
    public void RecordStateResponse_WithoutBridge_StillReturnsTypedState()
    {
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{" + StateData + "}}");

        var state = PiConversation.RecordStateResponse(null, response);

        Assert.NotNull(state);
        Assert.Equal("meta/muse-spark", state.ModelId);
    }

    [Fact]
    public void RecordStateResponse_WrongShape_ReturnsNull_NeverHollow()
    {
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");

        Assert.Null(PiConversation.RecordStateResponse(null, response));
    }

    [Fact]
    public void RecordStateResponse_NullResponse_ThrowsLoudly()
    {
        Assert.Throws<ArgumentNullException>(() => PiConversation.RecordStateResponse(null, null!));
        using var bridge = new FeedBridge(new ConversationFeed());
        Assert.Throws<ArgumentNullException>(() => PiConversation.RecordStateResponse(bridge, null!));
    }

    [Fact]
    public void RecordStateResponse_CanarySessionId_NeverRetainedBare()
    {
        // A secret-shaped session value must fail closed through the same
        // gating as every other metadata id; ordinary ids still pass through.
        using var bridge = new FeedBridge(new ConversationFeed());
        bridge.Attach(new PiRpcClient());
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{" +
            "\"model\":{\"id\":\"m\",\"name\":\"n\",\"provider\":\"p\"}," +
            "\"thinkingLevel\":\"high\",\"isStreaming\":false,\"isCompacting\":false," +
            "\"sessionId\":\"sess-01 api_key=CANARY-9F8E7D\",\"messageCount\":0,\"pendingMessageCount\":0}}");

        var state = PiConversation.RecordStateResponse(bridge, response);

        Assert.NotNull(state);
        Assert.DoesNotContain("CANARY-9F8E7D", state.SessionId ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY-9F8E7D", bridge.LatestEngine?.State.SessionId ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- prompt acceptance boundary -------------------------------------------

    [Fact]
    public void RecordResponse_PromptAcceptance_DecodesWithoutRows()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient());
        var rowsBefore = feed.Count;

        using var tap = new PiEventTap(new PiRpcClient());
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");
        bridge.RecordResponse(response);
        using var tapDoc = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");
        tap.RecordResponse(tapDoc);

        var accepted = Assert.Single(tap.Events, e => e is PromptAccepted);
        Assert.Equal("started", ((PromptAccepted)accepted).Disposition);
        Assert.Equal(rowsBefore, feed.Count);
    }

    // ---- unavailable vs truncated vs complete -----------------------------------

    [Fact]
    public void ToolEnded_MissingResult_RendersNotCaptured_NotTruncated()
    {
        var parsed = PiEventTap.Parse(
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"c9\",\"toolName\":\"powershell\",\"isError\":false,\"durationMs\":5}",
            T0);

        var ended = Assert.IsType<ToolEnded>(parsed);
        Assert.False(ended.ResultPresent);

        Assert.Contains("[result not captured]", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
        Assert.DoesNotContain("[truncated]", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
        Assert.DoesNotContain("omitted", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
        Assert.Contains("result-present: False", ended.Render(VisibilityLevel.Full), StringComparison.Ordinal);
        Assert.Equal("Tool powershell: completed, exit unknown. in 5 ms", ended.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void ToolEnded_EmptyDeliveredResult_IsComplete_NoMarker()
    {
        var parsed = PiEventTap.Parse(
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"c9\",\"toolName\":\"powershell\",\"isError\":false,\"durationMs\":5,\"result\":{}}",
            T0);

        var ended = Assert.IsType<ToolEnded>(parsed);
        Assert.True(ended.ResultPresent);
        Assert.DoesNotContain("[result not captured]", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolEnded_WireTruncatedResult_KeepsTruncationMarker()
    {
        var parsed = PiEventTap.Parse(
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"c9\",\"toolName\":\"powershell\",\"isError\":false,\"durationMs\":5," +
            "\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"out\"}],\"structuredContent\":{\"exit_code\":0,\"truncated\":true}}}",
            T0);

        var ended = Assert.IsType<ToolEnded>(parsed);
        Assert.True(ended.ResultPresent);
        Assert.True(ended.WireTruncated);
        Assert.Contains("[truncated]", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
        Assert.DoesNotContain("[result not captured]", ended.Render(VisibilityLevel.Detailed), StringComparison.Ordinal);
    }

    [Fact]
    public void ToolEnded_CanaryInResult_IsRedactedOnThePresentPath()
    {
        var parsed = PiEventTap.Parse(
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"c9\",\"toolName\":\"powershell\",\"isError\":false,\"durationMs\":5," +
            "\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"output password=CANARY-PW-555 end\"}]}}",
            T0);

        var ended = Assert.IsType<ToolEnded>(parsed);
        Assert.True(ended.ResultPresent);
        Assert.DoesNotContain("CANARY-PW-555", ended.RetainedText, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY-PW-555", ended.OutputPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolStarted_CanaryInArgs_IsRedacted()
    {
        var parsed = PiEventTap.Parse(
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"c9\",\"toolName\":\"powershell\"," +
            "\"args\":{\"command\":\"echo token=CANARY-ARG-666\"}}",
            T0);

        var started = Assert.IsType<ToolStarted>(parsed);
        Assert.DoesNotContain("CANARY-ARG-666", started.ArgsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARY-ARG-666", started.ArgSummary, StringComparison.Ordinal);
    }

    // ---- bridge correlation suffixes --------------------------------------------

    private static string LastSystemRow(ConversationFeed feed)
        => feed.Snapshot().Last(e => e.Role == "system").Text;

    private static JsonDocument EndDoc(string callId, bool withResult = true)
        => JsonDocument.Parse(
            "{\"type\":\"tool_execution_end\",\"toolCallId\":\"" + callId + "\",\"toolName\":\"powershell\"," +
            "\"isError\":false,\"durationMs\":5" + (withResult ? ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}" : string.Empty) + "}");

    private static JsonDocument StartDoc(string callId)
        => JsonDocument.Parse(
            "{\"type\":\"tool_execution_start\",\"toolCallId\":\"" + callId + "\",\"toolName\":\"powershell\",\"args\":{}}");

    [Fact]
    public void Bridge_OrphanEnd_EvictionFree_NamesTheMissingStart()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient());

        using var end = EndDoc("orphan-1", withResult: false);
        bridge.RecordResponse(end);

        var row = LastSystemRow(feed);
        Assert.Contains("[result orphan-1]", row, StringComparison.Ordinal);
        Assert.Contains("(no observed start)", row, StringComparison.Ordinal);
        Assert.Contains("[result not captured]", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_OrphanEnd_AfterEviction_StaysSilentInsteadOfGuessing()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient());

        for (var i = 0; i < 2001; i++)
        {
            using var filler = JsonDocument.Parse(
                "{\"type\":\"response\",\"command\":\"get_session_stats\",\"success\":true,\"data\":{}}");
            bridge.RecordResponse(filler);
        }

        using var end = EndDoc("orphan-9", withResult: false);
        bridge.RecordResponse(end);

        var row = LastSystemRow(feed);
        Assert.Contains("[result orphan-9]", row, StringComparison.Ordinal);
        Assert.DoesNotContain("(no observed start)", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Bridge_RepeatedEnd_MarksTheRepeat_PairedEndStaysClean()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient());

        using var start = StartDoc("dup-1");
        bridge.RecordResponse(start);
        using var first = EndDoc("dup-1");
        bridge.RecordResponse(first);
        var firstRow = LastSystemRow(feed);
        Assert.DoesNotContain("(repeated end)", firstRow, StringComparison.Ordinal);
        Assert.DoesNotContain("(no observed start)", firstRow, StringComparison.Ordinal);

        using var second = EndDoc("dup-1");
        bridge.RecordResponse(second);
        Assert.Contains("(repeated end)", LastSystemRow(feed), StringComparison.Ordinal);
    }

    // ---- loopback wire paths ------------------------------------------------------

    [Fact]
    public async Task StateDocument_WirePath_ReturnsLiveDocumentForTheBoundary()
    {
        await using var loop = new Loopback();
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);

        var pending = client.GetStateDocumentAsync(CancellationToken.None);
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"get_state\",\"success\":true,\"data\":{" + StateData + "}}");

        using var arrived = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("sess-abc123", arrived.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task PromptDocument_WirePath_DispositionAgreesWithTypedRead()
    {
        await using var loop = new Loopback();
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);

        var pending = client.SendPromptDocumentAsync("hello", CancellationToken.None);
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");

        using var arrived = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("started", PiRpcClient.DispositionOf(arrived));
    }

    [Fact]
    public async Task PromptDocument_CommandError_ThrowsTyped_DisposesLeak()
    {
        await using var loop = new Loopback();
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);

        var pending = client.SendPromptDocumentAsync("hello", CancellationToken.None);
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"prompt\",\"success\":false,\"error\":\"no session\"}");

        var thrown = await Assert.ThrowsAsync<PiRpcCommandException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("prompt", thrown.Command);
    }

    [Fact]
    public async Task PromptAndWait_HookObservesLiveAcceptance_BeforeSettle()
    {
        await using var loop = new Loopback();
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);

        var hooked = false;
        var hookedDisposition = string.Empty;
        var pending = client.PromptAndWaitAsync(
            "hello",
            null,
            CancellationToken.None,
            acceptance =>
            {
                hooked = true;
                hookedDisposition = PiRpcClient.DispositionOf(acceptance);
            });
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");
        await loop.WriteStdoutLineAsync("{\"type\":\"agent_settled\",\"aborted\":false}");

        var (text, aborted) = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(hooked);
        Assert.Equal("started", hookedDisposition);
        Assert.Equal(string.Empty, text);
        Assert.False(aborted);
    }

    // ---- loopback helper (SessionStatsPollTests pattern) ----------------------------

    private sealed class Loopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly MemoryStream _written = new();

        public PiRpcConnection Connection { get; }

        public Loopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), _written, leaveOpen: true);
            Connection.Start();
        }

        public async Task WriteStdoutLineAsync(string line)
        {
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public async Task<string> WaitForSentIdAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                string text;
                lock (_written)
                {
                    text = Encoding.UTF8.GetString(_written.ToArray());
                }

                var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (line is not null)
                {
                    using var record = JsonDocument.Parse(line);
                    return record.RootElement.GetProperty("id").GetString()!;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("No command was written.");
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            _written.Dispose();
        }
    }
}
