using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Final-acceptance tests: synthetic secrets in METADATA (not just args and
/// output), exact truncation boundaries, and windowed message retention. All
/// secrets are SYNTHETIC_ canaries. Bare unschemed tokens in metadata remain
/// a documented limitation and are not asserted here.
/// </summary>
public sealed class PiTapMetadataTests
{
    private static readonly DateTime At = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<string> AllStrings(AgentXEvent e)
    {
        foreach (var property in e.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType == typeof(string) && property.GetValue(e) is string s)
            {
                yield return s;
            }
        }
    }

    private static void AssertNoCanary(AgentXEvent e)
    {
        foreach (var s in AllStrings(e))
        {
            Assert.DoesNotContain("SYNTHETIC", s);
        }

        foreach (var level in new[] { VisibilityLevel.Compact, VisibilityLevel.Detailed, VisibilityLevel.Full })
        {
            Assert.DoesNotContain("SYNTHETIC", e.Render(level) ?? string.Empty);
        }
    }

    [Fact]
    public void Metadata_UnknownShapeWithholdsSecretNames()
    {
        // Lucy's probe, verbatim: the canary sits in a property NAME.
        var parsed = Assert.IsType<OtherEvent>(PiEventTap.Parse(
            """{"type":"future_shape","api_key=SYNTHETIC_METADATA":1}""", At));
        Assert.Equal("future_shape", parsed.EventType);
        Assert.Contains("[withheld]", parsed.FieldNames);
        AssertNoCanary(parsed);
    }

    [Fact]
    public void Metadata_HostileTypeIsWithheld()
    {
        var parsed = Assert.IsType<OtherEvent>(PiEventTap.Parse(
            """{"type":"Bearer SYNTHETIC_T9","a":1}""", At));
        Assert.Equal("[withheld]", parsed.EventType);
        AssertNoCanary(parsed);
    }

    [Fact]
    public void Metadata_ToolFieldsGatedButLegitNamesKept()
    {
        var hostile = Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"ok_id|1","toolName":"evil tool!","args":{}}""", At));
        Assert.Equal("[withheld]", hostile.ToolName);
        Assert.Equal("ok_id|1", hostile.CallId);
        AssertNoCanary(hostile);

        var shaped = Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"c","toolName":"api_key=SYNTHETIC_TN","args":{}}""", At));
        AssertNoCanary(shaped);

        var legit = Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"call_abc|fc_def","toolName":"mcp__srv__tool","args":{}}""", At));
        Assert.Equal("mcp__srv__tool", legit.ToolName);
        Assert.Equal("call_abc|fc_def", legit.CallId);
    }

    [Fact]
    public void Metadata_DispositionAllowlisted()
    {
        var ok = Assert.IsType<PromptAccepted>(PiEventTap.Parse(
            """{"type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""", At));
        Assert.Equal("started", ok.Disposition);

        var weird = Assert.IsType<PromptAccepted>(PiEventTap.Parse(
            """{"type":"response","command":"prompt","success":true,"data":{"disposition":"SYNTHETIC_DONE"}}""", At));
        Assert.Equal("[withheld]", weird.Disposition);
        AssertNoCanary(weird);
    }

    [Fact]
    public void Metadata_EngineStateRedactedLegitModelKept()
    {
        var hostile = Assert.IsType<EngineStateObserved>(PiEventTap.Parse(
            """{"type":"response","command":"get_state","success":true,"data":{"model":{"id":"api_key=SYNTHETIC_M1"},"isStreaming":false,"isCompacting":false,"messageCount":1}}""", At));
        AssertNoCanary(hostile);

        var legit = Assert.IsType<EngineStateObserved>(PiEventTap.Parse(
            """{"type":"response","command":"get_state","success":true,"data":{"model":{"id":"meta/muse-spark-1.3-contributor"},"isStreaming":false,"isCompacting":false,"messageCount":1}}""", At));
        Assert.Equal("meta/muse-spark-1.3-contributor", legit.State.ModelId);
    }

    [Fact]
    public void Metadata_SessionPathAndCallIds()
    {
        var stats = Assert.IsType<StatsSnapshot>(PiEventTap.Parse(
            """{"type":"response","command":"get_session_stats","success":true,"data":{"sessionId":"sess 123","toolCalls":0,"tokens":{"total":0},"cost":0,"contextUsage":{"percent":0}}}""", At));
        Assert.Equal("[withheld]", stats.SessionId);

        var bash = Assert.IsType<BashResult>(PiEventTap.Parse(
            """{"type":"response","command":"bash","success":true,"data":{"output":"x","exitCode":0,"fullOutputPath":"C:\\t\\out token=SYNTHETIC_P"}}""", At));
        AssertNoCanary(bash);
        Assert.Contains("[redacted]", bash.FullOutputPath ?? string.Empty);
    }

    [Fact]
    public async Task Metadata_SubscriptionDeliveryIsClean()
    {
        await using var loop = new MetaLoopback();
        using var tap = new PiEventTap(loop.Connection);
        var seen = new List<AgentXEvent>();
        tap.Captured += (_, e) => seen.Add(e);
        var lines = new[]
        {
            """{"type":"tool_execution_start","toolCallId":"m1","toolName":"t api_key=SYNTHETIC_D1","args":{"token":"SYNTHETIC_D2"}}""",
            """{"type":"weird","api_key=SYNTHETIC_D3":1}""",
            """{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"Bearer SYNTHETIC_D4"}}""",
        };
        foreach (var line in lines)
        {
            await loop.WriteStdoutLineAsync(line);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && tap.Events.Count < lines.Length)
        {
            await Task.Delay(10);
        }

        Assert.Equal(lines.Length, seen.Count);
        foreach (var e in seen)
        {
            AssertNoCanary(e);
        }
    }

    [Fact]
    public void Truncation_BoundariesAreExactAndMarked()
    {
        // 3000 retained / 2000 displayed: marker with the exact omitted count.
        var ended = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"b1","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"__T__"}]}}""".Replace("__T__", new string('t', 3000)), At));
        Assert.Equal(3000, ended.OutputChars);
        Assert.True(ended.OutputPreview.Length <= 2000);
        Assert.Contains("[+1000 chars retained]", ended.Render(VisibilityLevel.Detailed));
        Assert.DoesNotContain("[truncated]", ended.Render(VisibilityLevel.Detailed));

        // Exactly at the cap: no marker at all.
        var exact = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"b2","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"__T__"}]}}""".Replace("__T__", new string('t', 2000)), At));
        Assert.DoesNotContain("retained", exact.Render(VisibilityLevel.Detailed));
        Assert.DoesNotContain("[truncated]", exact.Render(VisibilityLevel.Detailed));

        // Partial boundary: 1500 retained / 1000 displayed.
        var partial = Assert.IsType<ToolProgress>(PiEventTap.Parse(
            """{"type":"tool_execution_update","toolCallId":"b3","toolName":"bash","args":{},"partialResult":{"content":[{"type":"text","text":"__T__"}]}}""".Replace("__T__", new string('p', 1500)), At));
        Assert.Equal(1500, partial.PartialChars);
        Assert.Contains("[+500 chars retained]", partial.Render(VisibilityLevel.Detailed));

        // Bash head raised to 1000: 700 chars show whole, 1001 mark +1.
        var small = Assert.IsType<BashChunk>(PiEventTap.Parse(
            """{"type":"bash_execution_update","id":"x","delta":"__T__"}""".Replace("__T__", new string('b', 700)), At));
        Assert.Equal(700, small.HeadPreview.Length);
        Assert.DoesNotContain("retained", small.Render(VisibilityLevel.Detailed));
        var big = Assert.IsType<BashChunk>(PiEventTap.Parse(
            """{"type":"bash_execution_update","id":"x","delta":"__T__"}""".Replace("__T__", new string('b', 1001)), At));
        Assert.Contains("[+1 chars retained]", big.Render(VisibilityLevel.Detailed));

        // Message preview: 401 chars mark +1, 400 show whole.
        var over = Assert.IsType<AssistantMessage>(PiEventTap.Parse(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"__T__"}]}}""".Replace("__T__", new string('m', 401)), At));
        Assert.Equal(401, over.Chars);
        Assert.Contains("[+1 chars retained]", over.Render(VisibilityLevel.Detailed));
        var at = Assert.IsType<AssistantMessage>(PiEventTap.Parse(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"__T__"}]}}""".Replace("__T__", new string('m', 400)), At));
        Assert.DoesNotContain("retained", at.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void TempAlloc_WindowedMessageCountsExactlyRetainsLittle()
    {
        // 200 blocks x 100 chars: true total counted, retention stays capped.
        var blocks = string.Join(",", Enumerable.Range(0, 200).Select(i => """{"type":"text","text":"__B__"}""".Replace("__B__", new string('w', 100))));
        var message = Assert.IsType<AssistantMessage>(PiEventTap.Parse(
            """{"type":"message_end","message":{"role":"assistant","content":[__B__]}}""".Replace("__B__", blocks), At));
        Assert.Equal(20000, message.Chars);
        Assert.True(message.PreviewText.Length <= 400);
        Assert.Contains("[+19600 chars retained]", message.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Malformed_NonStringTypeFailsClosed()
    {
        var parsed = Assert.IsType<OtherEvent>(PiEventTap.Parse("""{"type":123,"a":1}""", At));
        Assert.Equal("unknown", parsed.EventType);
        AssertNoCanary(parsed);
    }

    private sealed class MetaLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();

        public PiRpcConnection Connection { get; }

        public MetaLoopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), Stream.Null, leaveOpen: true);
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
        }
    }
}
