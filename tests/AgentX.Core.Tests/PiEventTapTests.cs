using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Focused tests for the typed event-capture prototype. The JSONL fixture
/// holds verbatim records from a disposable live Pi 1.1.0 RPC session
/// (write a file, list it, fail a command, poll stats, direct bash).
/// </summary>
public sealed class PiEventTapTests
{
    private static string[] FixtureLines()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("obs-sample.jsonl", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static List<AgentXEvent> ParseFixture()
    {
        var at = new DateTime(2026, 10, 12, 6, 50, 0, DateTimeKind.Utc);
        return FixtureLines().Select(line => PiEventTap.Parse(line, at)).ToList();
    }

    [Fact]
    public void Fixture_DecodesToolLifecycleWithRealTimingsAndExitCode()
    {
        var events = ParseFixture();

        var started = events.OfType<ToolStarted>().Single();
        Assert.Equal("powershell", started.ToolName);
        Assert.StartsWith("command:", started.ArgSummary);
        Assert.Contains("probe-out", started.ArgsJson);

        Assert.Single(events.OfType<ToolProgress>());

        var ended = events.OfType<ToolEnded>().Single();
        Assert.False(ended.IsError);
        Assert.Equal(976, ended.DurationMs);
        Assert.Equal(0, ended.ExitCode);
        Assert.False(ended.WireTruncated);
        Assert.True(ended.OutputChars > 0);
        Assert.Contains("probe-out", ended.OutputPreview);
        Assert.True(ended.OutputPreview.Length <= 2000);
        Assert.True(ended.RetainedBytes >= ended.OutputChars);
    }

    [Fact]
    public void Fixture_DecodesSettledStatsAndBashResult()
    {
        var events = ParseFixture();

        var settled = events.OfType<RunSettled>().Single();
        Assert.False(settled.Aborted);

        var stats = events.OfType<StatsSnapshot>().Single();
        Assert.Equal(2, stats.ToolCalls);
        Assert.Equal(15284, stats.TotalTokens);
        Assert.True(stats.Cost > 0);
        Assert.True(stats.ContextPercent > 0);

        var bash = events.OfType<BashResult>().Single();
        Assert.NotNull(bash.ExitCode);
        Assert.False(bash.Truncated);
        Assert.Contains("hello-rpc", bash.OutputPreview);
    }

    [Fact]
    public void Levels_HideDeltasAndStartsAtCompact_ShowEverythingAtFull()
    {
        var events = ParseFixture();
        var delta = events.OfType<TextDelta>().First();
        var started = events.OfType<ToolStarted>().Single();
        var ended = events.OfType<ToolEnded>().Single();
        var settled = events.OfType<RunSettled>().Single();

        Assert.Null(delta.Render(VisibilityLevel.Compact));
        Assert.Null(started.Render(VisibilityLevel.Compact));
        Assert.NotNull(ended.Render(VisibilityLevel.Compact));
        Assert.NotNull(settled.Render(VisibilityLevel.Compact));

        Assert.NotNull(delta.Render(VisibilityLevel.Detailed));
        Assert.NotNull(started.Render(VisibilityLevel.Detailed));

        var other = events.OfType<OtherEvent>().First();
        Assert.Null(other.Render(VisibilityLevel.Detailed));
        Assert.NotNull(other.Render(VisibilityLevel.Full));
    }

    [Fact]
    public void Compact_ToolEndReportsExitCodeDistinctFromIsError()
    {
        var ended = new ToolEnded(DateTime.UtcNow, "call_1", "powershell", false, 100, 42, false, false, 10, 30, "out");
        Assert.Contains("exit 42", ended.Render(VisibilityLevel.Compact));
        var failed = ended with { IsError = true };
        Assert.Contains("failed", failed.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void MalformedLine_BecomesTapError_NeverThrows()
    {
        var parsed = PiEventTap.Parse("{not json", DateTime.UtcNow);
        Assert.IsType<TapError>(parsed);
        Assert.NotNull(parsed.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void UnknownType_IsPreservedForFullTrace()
    {
        var parsed = PiEventTap.Parse("""{"type":"future_shape","a":1}""", DateTime.UtcNow);
        var other = Assert.IsType<OtherEvent>(parsed);
        Assert.Equal("future_shape", other.EventType);
        Assert.Null(other.Render(VisibilityLevel.Detailed));
        Assert.Contains("future_shape", other.Render(VisibilityLevel.Full));
    }

    [Fact]
    public void UsageOnlyRecord_ParsesProviderShape()
    {
        var parsed = PiEventTap.Parse(
            """{"type":"message_update","usage":{"input":1,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":3}}""",
            DateTime.UtcNow);
        // text_end carries no new identity beyond the completed message; usage without
        // deltas is the only usage signal worth storing.
        var usage = Assert.IsType<UsageObserved>(parsed);
        Assert.Equal(3, usage.Total);
    }

    [Fact]
    public async Task Tap_CapturesThroughARealConnection_IsolatesSubscriberFaults_AndBoundsTheStore()
    {
        await using var loop = new TapLoopback();
        using var tap = new PiEventTap(loop.Connection);
        var seen = new List<AgentXEvent>();
        tap.Captured += (_, e) => seen.Add(e);
        tap.Captured += (_, _) => throw new InvalidOperationException("hostile subscriber");

        foreach (var line in FixtureLines())
        {
            await loop.WriteStdoutLineAsync(line);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && tap.Events.Count < FixtureLines().Length)
        {
            await Task.Delay(10);
        }

        Assert.Equal(FixtureLines().Length, tap.Events.Count);
        // The hostile subscriber threw on every event; capture continued regardless.
        Assert.Equal(FixtureLines().Length, seen.Count);
        Assert.Contains(tap.Events, e => e is ToolEnded);

        for (var i = 0; i < 5100; i++)
        {
            await loop.WriteStdoutLineAsync("""{"type":"turn_start"}""");
        }

        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && tap.DroppedEvents < 3100)
        {
            await Task.Delay(10);
        }

        Assert.Equal(2000, tap.Events.Count);
        Assert.True(tap.DroppedEvents >= 3100);
    }

    private sealed class TapLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();

        public PiRpcConnection Connection { get; }

        public TapLoopback()
        {
            Connection = new PiRpcConnection(
                _input.Reader.AsStream(), Stream.Null, leaveOpen: true);
            Connection.Start();
        }

        public async Task WriteStdoutLineAsync(string line)
        {
            var framed = Encoding.UTF8.GetBytes(line + "\n");
            await _input.Writer.WriteAsync(framed);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
        }
    }
}
