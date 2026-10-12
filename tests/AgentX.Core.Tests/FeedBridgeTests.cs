using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Deterministic tests for the live-feed bridge: visibility levels, the
/// single-history rule, tool correlation, redaction, pollable routing,
/// retained access, attach lifecycle, and bounded streaming.
/// </summary>
public sealed class FeedBridgeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public FeedBridgeTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private static int ToolRows(ConversationFeed feed)
        => feed.Snapshot().Count(e => e.Role == "tool");

    private static int SystemRows(ConversationFeed feed)
        => feed.Snapshot().Count(e => e.Role == "system");

    [Fact]
    public void Levels_CompactSuppressesDetails_DetailedShows_FullAddsDiagnostics()
    {
        var wired = Wire(VisibilityLevel.Compact);
        var feed = wired.Feed;
        using var bridge = wired.Bridge;
        var tap = wired.Tap;
        Assert.Equal(VisibilityLevel.Detailed, new FeedBridge(new ConversationFeed()).Level);

        var start = Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"c1","toolName":"bash","args":{"command":"dotnet test"}}""", At));
        var end = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"c1","toolName":"bash","isError":false,"durationMs":12,"result":{"content":[{"type":"text","text":"ok"}],"structuredContent":{"exit_code":0}}}""", At));
        Push(tap, start, end);
        Assert.Equal(0, SystemRows(feed));

        bridge.Level = VisibilityLevel.Detailed;
        Push(tap, start, end);
        Assert.Equal(2, SystemRows(feed));

        var progress = Assert.IsType<ToolProgress>(PiEventTap.Parse(
            """{"type":"tool_execution_update","toolCallId":"c1","toolName":"bash","args":{},"partialResult":{"content":[{"type":"text","text":"part"}]}}""", At));
        Push(tap, progress);
        Assert.Equal(2, SystemRows(feed));

        bridge.Level = VisibilityLevel.Full;
        Push(tap, progress);
        Assert.Equal(3, SystemRows(feed));
    }

    [Fact]
    public void SingleHistory_TextEventsNeverBecomeRows()
    {
        var wired = Wire();
        var feed = wired.Feed;
        using var bridge = wired.Bridge;
        var before = feed.Count;
        Push(wired.Tap,
            PiEventTap.Parse("""{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"hello"}}""", At),
            PiEventTap.Parse("""{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}""", At),
            PiEventTap.Parse("""{"type":"message_end","message":{"role":"user","content":[{"type":"text","text":"hi"}]}}""", At),
            PiEventTap.Parse("""{"type":"turn_start"}""", At),
            PiEventTap.Parse("""{"type":"agent_settled","aborted":false}""", At));
        Assert.Equal(before, feed.Count);
    }

    [Fact]
    public void Correlation_EndRowCarriesTheStartCallId()
    {
        var wired = Wire();
        var feed = wired.Feed;
        using var bridge = wired.Bridge;
        Push(wired.Tap,
            PiEventTap.Parse("""{"type":"tool_execution_start","toolCallId":"call_abc123xyz","toolName":"powershell","args":{"command":"dir"}}""", At),
            PiEventTap.Parse("""{"type":"tool_execution_end","toolCallId":"call_abc123xyz","toolName":"powershell","isError":false,"durationMs":44,"result":{"content":[{"type":"text","text":"out"}],"structuredContent":{"exit_code":0}}}""", At));
        var rows = feed.Snapshot().Where(e => e.Role == "system").Select(e => e.Text).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, text => Assert.Contains("call_abc123xyz", text, StringComparison.Ordinal));
    }

    [Fact]
    public void Redaction_HostileArgsNeverReachFeedRows()
    {
        var wired = Wire();
        var feed = wired.Feed;
        using var bridge = wired.Bridge;
        Push(wired.Tap,
            PiEventTap.Parse("""{"type":"tool_execution_start","toolCallId":"s1","toolName":"bash","args":{"command":"curl -H \"Authorization: Bearer SYNTHETIC_BRIDGE_1\" https://x"}}""", At),
            PiEventTap.Parse("""{"type":"tool_execution_end","toolCallId":"s1","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"token=SYNTHETIC_BRIDGE_2"}],"structuredContent":{"exit_code":0}}}""", At));
        foreach (var entry in feed.Snapshot())
        {
            Assert.DoesNotContain("SYNTHETIC", entry.Text);
        }
    }

    [Fact]
    public void BasePath_RedactsSummariesPathsAndHistory()
    {
        using var start = JsonDocument.Parse(
            """{"type":"tool_execution_start","toolCallId":"c","toolName":"powershell","args":{"command":"echo api_key=SYNTHETIC_BASE_1"}}""");
        var activity = PiConversation.ParseToolActivity(start);
        Assert.NotNull(activity);
        Assert.DoesNotContain("SYNTHETIC", activity.Summary ?? string.Empty);

        using var result = JsonDocument.Parse(
            """{"role":"toolResult","toolName":"bash","isError":false,"content":[{"type":"text","text":"Bearer SYNTHETIC_BASE_2"}]}""");
        var parsed = PiConversation.ParseHistory(JsonOfArray(result.RootElement));
        Assert.DoesNotContain("SYNTHETIC", parsed[0].Text);

        using var user = JsonDocument.Parse(
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta"}}""");
        Assert.Null(PiConversation.ParseToolActivity(user));
    }

    [Fact]
    public void Pollables_UpdateLatestWithoutRows_UnknownCallIdReturnsNull()
    {
        var wired = Wire();
        var feed = wired.Feed;
        using var bridge = wired.Bridge;
        Assert.Null(bridge.LatestStats);
        Assert.Null(bridge.GetRetainedText("nope"));

        using var stats = JsonDocument.Parse(
            """{"type":"response","command":"get_session_stats","success":true,"data":{"sessionId":"s","toolCalls":7,"tokens":{"total":100},"cost":0.02,"contextUsage":{"percent":3.5}}}""");
        var rowsBefore = feed.Count;
        bridge.RecordResponse(stats);
        Assert.Equal(rowsBefore, feed.Count);
        Assert.NotNull(bridge.LatestStats);
        Assert.Equal(7, bridge.LatestStats.ToolCalls);

        using var ended = JsonDocument.Parse(
            """{"type":"tool_execution_end","toolCallId":"keep1","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"0123456789ABCDEF0123456789ABCDEF"}]}}""");
        bridge.RecordResponse(ended);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", bridge.GetRetainedText("keep1"));
    }

    [Fact]
    public async Task Attach_Detach_Reattach_Lifecycle()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        await using var loop = new BridgeLoopback();
        bridge.AttachConnection(loop.Connection);
        Assert.Throws<InvalidOperationException>(() => bridge.AttachConnection(loop.Connection));

        await loop.WriteStdoutLineAsync("""{"type":"tool_execution_start","toolCallId":"l1","toolName":"bash","args":{"command":"x"}}""");
        Assert.True(await WaitForAsync(() => feed.Count >= 1, TimeSpan.FromSeconds(5)));

        bridge.Detach();
        var count = feed.Count;
        await loop.WriteStdoutLineAsync("""{"type":"tool_execution_start","toolCallId":"l2","toolName":"bash","args":{"command":"y"}}""");
        await Task.Delay(200);
        Assert.Equal(count, feed.Count);

        bridge.AttachConnection(loop.Connection);
        await loop.WriteStdoutLineAsync("""{"type":"tool_execution_start","toolCallId":"l3","toolName":"bash","args":{"command":"z"}}""");
        Assert.True(await WaitForAsync(() => feed.Count > count, TimeSpan.FromSeconds(5)));
        bridge.Detach();
    }

    [Fact]
    public async Task MalformedAndHostileSubscriber_DoNotBreakCapture()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        await using var loop = new BridgeLoopback();
        bridge.AttachConnection(loop.Connection);

        await loop.WriteStdoutLineAsync("{oops");
        await loop.WriteStdoutLineAsync("[]");
        await loop.WriteStdoutLineAsync("""{"type":"tool_execution_start","toolCallId":"m1","toolName":"bash","args":{}}""");
        Assert.True(await WaitForAsync(() => feed.Count >= 1, TimeSpan.FromSeconds(5)));
        bridge.Detach();
    }

    [Fact]
    public async Task Burst_StaysBoundedAndFast()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        await using var loop = new BridgeLoopback();
        bridge.AttachConnection(loop.Connection);

        const int burst = 4000;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < burst; i++)
        {
            var line = i % 2 == 0
                ? """{"type":"tool_execution_start","toolCallId":"T__I__","toolName":"bash","args":{"command":"echo __I__"}}""".Replace("__I__", i.ToString())
                : """{"type":"tool_execution_end","toolCallId":"T__I__","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"__P__"}]}}""".Replace("__I__", (i - 1).ToString()).Replace("__P__", new string('o', 500));
            await loop.WriteStdoutLineAsync(line);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && feed.Count < burst)
        {
            await Task.Delay(10);
        }

        watch.Stop();
        var managed = GC.GetTotalMemory(false);
        _output.WriteLine($"BRIDGE-BURST events={burst} rows={feed.Count} elapsedMs={watch.ElapsedMilliseconds} managedBytes={managed}");
        Assert.True(feed.Count >= burst, $"feed={feed.Count}");
        Assert.True(watch.ElapsedMilliseconds < 25000, $"elapsed={watch.ElapsedMilliseconds}ms");
        bridge.Detach();
    }

    private sealed record Wired(FeedBridge Bridge, ConversationFeed Feed, PiEventTap Tap);

    private static Wired Wire(VisibilityLevel level = VisibilityLevel.Detailed)
    {
        var feed = new ConversationFeed();
        var bridge = new FeedBridge(feed, level);
        bridge.Attach(new PiRpcClient());
        var tap = (PiEventTap)typeof(FeedBridge).GetField("_tap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
        return new Wired(bridge, feed, tap);
    }

    private static void Push(PiEventTap tap, params AgentXEvent[] events)
    {
        foreach (var e in events)
        {
            tap.Publish(e);
        }
    }

    private static JsonDocument JsonOfArray(JsonElement element)
    {
        var json = """{"type":"response","command":"get_messages","success":true,"data":{"messages":[__M__]}}""".Replace("__M__", element.GetRawText());
        return JsonDocument.Parse(json);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(10);
        }

        return condition();
    }

    [Fact]
    public async Task ConversationAttachBridge_RoutesDetachAndRewires()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        await using var conversation = new PiConversation(() => new PiRpcClient());
        conversation.AttachBridge(bridge);
        var tap = (PiEventTap)typeof(FeedBridge).GetField("_tap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
        tap.Publish(Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"w1","toolName":"bash","args":{"command":"echo"}}""", At)));
        Assert.Equal(1, SystemRows(feed));
        conversation.DetachBridge();
        tap.Publish(Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"w2","toolName":"bash","args":{"command":"echo"}}""", At)));
        Assert.Equal(1, SystemRows(feed));
        conversation.AttachBridge(bridge);
        var tap2 = (PiEventTap)typeof(FeedBridge).GetField("_tap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
        Assert.NotSame(tap, tap2);
        tap2.Publish(Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"w3","toolName":"bash","args":{"command":"echo"}}""", At)));
        Assert.Equal(2, SystemRows(feed));
        conversation.DetachBridge();
    }

    private sealed class BridgeLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly MemoryStream _written = new();

        public PiRpcConnection Connection { get; }

        public BridgeLoopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), _written, leaveOpen: true);
            Connection.Start();
        }

        public async Task WriteStdoutLineAsync(string line)
        {
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            _written.Dispose();
        }
    }
}
