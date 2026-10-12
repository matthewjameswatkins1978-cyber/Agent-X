using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Adversarial tests for the hardened tap: synthetic canaries (never real
/// credentials), hostile shapes, the correlated-response boundary, and
/// burst/slow-subscriber behavior. Every secret below is SYNTHETIC_.
/// </summary>
public sealed class PiTapHardeningTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public PiTapHardeningTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly DateTime At = new(2026, 10, 12, 7, 0, 0, DateTimeKind.Utc);

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

    private static void AssertNoCanary(IEnumerable<string> texts, string what)
    {
        foreach (var text in texts)
        {
            Assert.DoesNotContain("SYNTHETIC", text);
        }
    }

    [Fact]
    public void Secrets_RedactedInToolArgsAcrossAllLevels()
    {
        var line = """{"type":"tool_execution_start","toolCallId":"c1","toolName":"bash","args":{"command":"curl -H \"Authorization: Bearer SYNTHETIC_BEARER_abc123xyz\" https://example.com"}}""";
        var started = Assert.IsType<ToolStarted>(PiEventTap.Parse(line, At));
        AssertNoCanary(AllStrings(started), "stored");
        Assert.Contains("[redacted]", started.ArgsJson);
        foreach (var level in new[] { VisibilityLevel.Compact, VisibilityLevel.Detailed, VisibilityLevel.Full })
        {
            AssertNoCanary(new[] { started.Render(level) ?? string.Empty }, $"render {level}");
        }
    }

    [Fact]
    public void Secrets_RedactedAfterQuotesAndShellBoundaries()
    {
        // Live failure shape: a PowerShell single-quoted 'api_key=...' inside
        // JSON args must redact exactly like the bare form.
        var line = """{"type":"tool_execution_start","toolCallId":"cq","toolName":"powershell","args":{"command":"Write-Output 'api_key=SYNTHETIC_Q_1'; Write-Output \"token=SYNTHETIC_Q_2\""}}""";
        var started = Assert.IsType<ToolStarted>(PiEventTap.Parse(line, At));
        AssertNoCanary(AllStrings(started), "quoted");
        Assert.Contains("[redacted]", started.ArgsJson);
        Assert.Contains("[redacted]", started.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Secrets_RedactedInKeyValueAndEnvShapes()
    {
        var line = """{"type":"tool_execution_end","toolCallId":"c2","toolName":"powershell","isError":false,"durationMs":5,"result":{"content":[{"type":"text","text":"api_key=SYNTHETIC_KEY_zz9\nMY_SECRET_TOKEN=SYNTHETIC_ENV_456\npassword: SYNTHETIC_PW\n"}],"structuredContent":{"output":"x","exit_code":0}}}""";
        var ended = Assert.IsType<ToolEnded>(PiEventTap.Parse(line, At));
        AssertNoCanary(AllStrings(ended), "stored");
        Assert.Contains("[redacted]", ended.OutputPreview);
        Assert.Equal(0, ended.ExitCode);
    }

    [Fact]
    public void Secrets_RedactedInDeltasMessagesAndTokenPrefixes()
    {
        var delta = Assert.IsType<TextDelta>(PiEventTap.Parse(
            """{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"deploy with ghp_SYNTHETICGITHUBTOKEN1234567890 now"}}""", At));
        AssertNoCanary(AllStrings(delta), "delta");

        var keyBlock = "-----BEGIN FAKE PRIVATE KEY-----\\nSYNTHETIC_KEY_MATERIAL\\n-----END FAKE PRIVATE KEY-----";
        var message = Assert.IsType<AssistantMessage>(PiEventTap.Parse(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"key: __KEY__"}]}}""".Replace("__KEY__", keyBlock), At));
        AssertNoCanary(AllStrings(message), "message");
        Assert.Contains("[redacted]", message.PreviewText);
    }

    [Fact]
    public void Secrets_WithheldFromUnknownShapesAndTapErrors()
    {
        var unknown = Assert.IsType<OtherEvent>(PiEventTap.Parse(
            """{"type":"future_shape","password":"SYNTHETIC_PW_1","nested":{"token":"SYNTHETIC_T_2"}}""", At));
        AssertNoCanary(AllStrings(unknown), "unknown");
        Assert.Contains("password", unknown.FieldNames);

        var broken = Assert.IsType<TapError>(PiEventTap.Parse(
            """{"type":"tool_execution_start","args":{"command":"Bearer SYNTHETIC_BROKEN""", At));
        AssertNoCanary(AllStrings(broken), "tap-error");
    }

    [Fact]
    public void Secrets_AbsentFromStoreSubscribersAndRenders()
    {
        var lines = new[]
        {
            """{"type":"tool_execution_start","toolCallId":"c9","toolName":"bash","args":{"command":"export TOKEN=SYNTHETIC_SWEEP_1"}}""",
            """{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"sk-proj-SYNTHETIC_SWEEP_2"}}""",
            """{"type":"tool_execution_end","toolCallId":"c9","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"auth_token=SYNTHETIC_SWEEP_3"}],"structuredContent":{"exit_code":0}}}""",
            """{"type":"weird_future","blob":"SYNTHETIC_SWEEP_4"}""",
        };
        var seen = new List<AgentXEvent>();
        foreach (var line in lines)
        {
            var parsed = PiEventTap.Parse(line, At);
            seen.Add(parsed);
            AssertNoCanary(AllStrings(parsed), "stored");
            foreach (var level in new[] { VisibilityLevel.Compact, VisibilityLevel.Detailed, VisibilityLevel.Full })
            {
                AssertNoCanary(new[] { parsed.Render(level) ?? string.Empty }, $"render {level}");
            }
        }

        // A hostile subscriber receives the same already-redacted objects.
        var hostile = new List<string>();
        foreach (var e in seen)
        {
            foreach (var s in AllStrings(e))
            {
                hostile.Add(s);
            }
        }

        AssertNoCanary(hostile, "subscriber");
    }

    [Fact]
    public void Fidelity_PartialKeepsLatestBoundedSnapshot()
    {
        var big = new string('p', 3000);
        var progress = Assert.IsType<ToolProgress>(PiEventTap.Parse(
            """{"type":"tool_execution_update","toolCallId":"c3","toolName":"bash","args":{},"partialResult":{"content":[{"type":"text","text":"__BIG__"}]}}""".Replace("__BIG__", big), At));
        Assert.Equal(3000, progress.PartialChars);
        Assert.True(progress.PartialTruncated);
        Assert.EndsWith("…", progress.PartialPreview);
        Assert.True(progress.PartialPreview.Length <= 1001);
        Assert.NotNull(progress.Render(VisibilityLevel.Detailed));
        Assert.Null(progress.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void Fidelity_TruncationIsExplicitBothLayers()
    {
        var wire = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"c4","toolName":"bash","isError":false,"durationMs":9,"result":{"content":[{"type":"text","text":"short"}],"structuredContent":{"exit_code":0,"truncated":true}}}""", At));
        Assert.True(wire.WireTruncated);
        Assert.False(wire.TapTruncated);
        Assert.Contains("[truncated]", wire.Render(VisibilityLevel.Detailed));

        var huge = new string('x', 20000);
        var tap = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"c5","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"__HUGE__"}]}}""".Replace("__HUGE__", huge), At));
        Assert.True(tap.TapTruncated);
        Assert.Equal(20000, tap.OutputChars);
        Assert.EndsWith("…", tap.OutputPreview);
        Assert.True(tap.OutputPreview.Length <= 2001);
        Assert.Contains("[truncated]", tap.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Fidelity_UnknownExitIsNotReportedAsSuccess()
    {
        var ended = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"c6","toolName":"bash","isError":false,"result":{"content":[]}}""", At));
        Assert.Null(ended.ExitCode);
        Assert.Contains("completed, exit unknown", ended.Render(VisibilityLevel.Compact));
        Assert.DoesNotContain(": ok.", ended.Render(VisibilityLevel.Compact));

        var bash = Assert.IsType<BashResult>(PiEventTap.Parse(
            """{"type":"response","command":"bash","success":true,"data":{"output":"hi"}}""", At));
        Assert.Null(bash.ExitCode);
        Assert.Contains("exit: unknown", bash.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Fidelity_UnicodeCountsCharsAndBytesHonestly()
    {
        var delta = Assert.IsType<TextDelta>(PiEventTap.Parse(
            """{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"hi 🚀 voiced"}}""", At));
        Assert.Equal("hi 🚀 voiced".Length, delta.Delta.Length);

        var ended = Assert.IsType<ToolEnded>(PiEventTap.Parse(
            """{"type":"tool_execution_end","toolCallId":"c7","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"日本語テスト"}]}}""", At));
        Assert.Equal("日本語テスト".Length, ended.OutputChars);
        Assert.Equal(Encoding.UTF8.GetByteCount("日本語テスト"), ended.RetainedBytes);
        Assert.Contains("bytes", ended.Render(VisibilityLevel.Full));
    }

    [Fact]
    public async Task Plumbing_CorrelatedResponseBypassesSubscriber_RecordResponseCaptures()
    {
        await using var loop = new CommandLoopback();
        using var tap = new PiEventTap(loop.Connection);

        // A genuinely pending correlated request: the task completes when the
        // matching response arrives, and the subscriber must NOT see it.
        var pending = loop.Connection.SendCommandAsync("get_session_stats");
        var id = await loop.WaitForSentIdAsync();
        var before = tap.Events.Count;
        await loop.WriteStdoutLineAsync(
            """{"type":"response","id":"__ID__","command":"get_session_stats","success":true,"data":{"sessionId":"s","toolCalls":3,"tokens":{"total":99},"cost":0.01,"contextUsage":{"percent":1.5}}}""".Replace("__ID__", id));
        using var arrived = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, arrived.RootElement.GetProperty("data").GetProperty("toolCalls").GetInt32());
        Assert.Equal(before, tap.Events.Count);

        // The explicit host route captures the same document through the
        // redacting decode path.
        tap.RecordResponse(arrived);
        var stats = tap.Events.OfType<StatsSnapshot>().Single();
        Assert.Equal(3, stats.ToolCalls);
        Assert.Equal(99, stats.TotalTokens);
    }

    [Fact]
    public void Plumbing_EngineStateDecodesViaRecordResponse()
    {
        using var tap = new PiEventTap(new PiRpcClient());
        using var doc = JsonDocument.Parse(
            """{"type":"response","command":"get_state","success":true,"data":{"model":{"id":"m1"},"isStreaming":false,"isCompacting":false,"messageCount":4}}""");
        tap.RecordResponse(doc);
        var engine = tap.Events.OfType<EngineStateObserved>().Single();
        Assert.Equal("m1", engine.State.ModelId);
        Assert.Equal(4, engine.State.MessageCount);
        Assert.Null(engine.Render(VisibilityLevel.Detailed));
        Assert.Contains("m1", engine.Render(VisibilityLevel.Full));
    }

    [Fact]
    public void Robust_MalformedRootsNeverThrow()
    {
        foreach (var line in new[] { "[]", "null", "42", "\"str\"", "{oops", string.Empty, "   " })
        {
            var parsed = PiEventTap.Parse(line, At);
            Assert.IsType<TapError>(parsed);
            Assert.NotNull(parsed.Render(VisibilityLevel.Compact));
        }

        // An empty object is valid JSON with no type: metadata-only, no raw.
        var empty = Assert.IsType<OtherEvent>(PiEventTap.Parse("{}", At));
        Assert.Equal("unknown", empty.EventType);
        AssertNoCanary(AllStrings(empty), "empty");
    }

    [Fact]
    public void Robust_HugeArgsFieldIsCapped()
    {
        var giant = new string('A', 100000);
        var started = Assert.IsType<ToolStarted>(PiEventTap.Parse(
            """{"type":"tool_execution_start","toolCallId":"c8","toolName":"bash","args":{"command":"__GIANT__"}}""".Replace("__GIANT__", giant), At));
        Assert.True(started.ArgsTruncated);
        Assert.True(started.ArgsJson.Length <= 4096);
    }

    [Fact]
    public async Task Robust_BurstStaysBounded_ReportsThroughput()
    {
        await using var loop = new CommandLoopback();
        using var tap = new PiEventTap(loop.Connection);
        tap.Captured += (_, _) => throw new InvalidOperationException("hostile subscriber");
        tap.Captured += (_, _) =>
        {
            var spin = System.Diagnostics.Stopwatch.StartNew();
            while (spin.ElapsedTicks < System.Diagnostics.Stopwatch.Frequency / 2000)
            {
            }
        };

        const int burst = 8000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetTotalMemory(true);
        for (var i = 0; i < burst; i++)
        {
            var line = i % 4 == 0
                ? """{"type":"tool_execution_end","toolCallId":"b","toolName":"bash","isError":false,"result":{"content":[{"type":"text","text":"__PAD__"}]}}""".Replace("__PAD__", new string('o', 1024))
                : """{"type":"turn_start"}""";
            await loop.WriteStdoutLineAsync(line);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && tap.Events.Count + tap.DroppedEvents < burst)
        {
            await Task.Delay(10);
        }

        sw.Stop();
        var retained = tap.RetainedTextChars;
        var output = $"BURST events={burst} captured={tap.Events.Count} dropped={tap.DroppedEvents} " +
            $"retainedChars={retained} elapsedMs={sw.ElapsedMilliseconds} managedDelta={GC.GetTotalMemory(false) - before}";
        Assert.True(tap.Events.Count <= 2000, output);
        Assert.True(retained <= 512 * 1024, output);
        Assert.True(tap.Events.Count + tap.DroppedEvents >= burst, output);
        _output.WriteLine(output);
    }

    private sealed class CommandLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly MemoryStream _written = new();

        public PiRpcConnection Connection { get; }

        public CommandLoopback()
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
