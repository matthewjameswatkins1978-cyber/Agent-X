using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class ConversationFeedTests
{
    [Fact]
    public void Accumulator_PreservesExactText_AcrossBatchedFlushes()
    {
        var accumulator = new StreamingTextAccumulator();
        Assert.Equal(string.Empty, accumulator.Flush()); // Nothing pending: no-op.

        accumulator.Append("Hello ");
        Assert.Equal("Hello ", accumulator.Flush());
        Assert.Equal(1, accumulator.FlushCount);
        accumulator.Append(null);
        accumulator.Append(string.Empty);
        Assert.Equal(string.Empty, accumulator.Flush()); // Still no-op.
        Assert.Equal(1, accumulator.FlushCount);
        accumulator.Append("world");
        Assert.Equal(5, accumulator.PendingChars);
        Assert.Equal(6, accumulator.CommittedChars);

        Assert.Equal("world", accumulator.Flush());
        Assert.Equal(2, accumulator.FlushCount);
        Assert.Equal("Hello world", accumulator.CommittedText);
        Assert.Equal(string.Empty, accumulator.Flush()); // Still no-op.
        Assert.Equal(2, accumulator.FlushCount);
        Assert.Equal("Hello world", accumulator.FlushAndGetCommitted());
    }

    [Fact]
    public void Accumulator_HandlesLargeDeltas_WithoutLoss()
    {
        var accumulator = new StreamingTextAccumulator();
        var big = new string('x', 2_000_000) + "🚀 tail";
        accumulator.Append(big[..1_000_000]);
        accumulator.Append(big[1_000_000..]);
        var committed = accumulator.FlushAndGetCommitted();
        Assert.Equal(big, committed);
        Assert.Equal(big.Length, accumulator.CommittedChars);
    }

    [Fact]
    public void Feed_LoadHistory_PreservesFullText_OrderAndRoles()
    {
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"user","content":"Hello Pi"},{"role":"assistant","content":[{"type":"text","text":"Hello human"}]}]}}""");
        var history = PiConversation.ParseHistory(response);

        var feed = new ConversationFeed();
        feed.LoadHistory(history);

        var entries = feed.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("user", entries[0].Role);
        Assert.Equal("Hello Pi", entries[0].Text);
        Assert.Equal("assistant", entries[1].Role);
        Assert.Equal("Hello human", entries[1].Text);
        Assert.False(entries[1].IsStreaming);
    }

    [Fact]
    public void Feed_StreamingTurn_AppendsExactly_AndClosesWithReceipt()
    {
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "Hello ");
        feed.AppendDelta(seq, null);
        feed.AppendDelta(seq, "world");
        feed.AppendDelta(999_999, "ignored — unknown turn");

        var receipt = new PiRunReceipt(Settled: true, Aborted: false, ErrorCount: 0, AssistantChars: 11, MessageCount: 2);
        feed.EndAssistantTurn(seq, receipt);
        feed.AppendDelta(seq, "ignored — turn ended");
        var entries = feed.Snapshot();
        Assert.Equal(2, entries.Count);
        Assert.Equal("Hello world", entries[0].Text);
        Assert.False(entries[0].IsStreaming);
        Assert.Equal("receipt", entries[1].Role);
        Assert.Contains("Healthy", entries[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Feed_CopySource_FollowsReconcile_AndKeepsCancelPartial()
    {
        // GUI copy path reads GetEntryText(seq): completion must serve the
        // authoritative reconciled text, cancellation the kept partial.
        var feed = new ConversationFeed();
        var completed = feed.BeginAssistantTurn();
        feed.AppendDelta(completed, "streamed chunk");
        Assert.Equal("streamed chunk", feed.GetEntryText(completed));
        feed.ReconcileStreamingText(completed, "authoritative full response");
        feed.EndAssistantTurn(completed, new PiRunReceipt(true, false, 0, 28, 2));
        Assert.Equal("authoritative full response", feed.GetEntryText(completed));

        var cancelled = feed.BeginAssistantTurn();
        feed.AppendDelta(cancelled, "partial before cancel");
        feed.EndAssistantTurn(cancelled, null);
        Assert.Equal("partial before cancel", feed.GetEntryText(cancelled));
        Assert.Null(feed.GetEntryText(999_999));
    }

    [Fact]
    public void Feed_Activity_TracksWork_StartToolIdle()
    {
        // The session map must show real observed activity, never a stale idle.
        var feed = new ConversationFeed();
        Assert.Contains("idle", feed.BuildMap().ToDisplayText(verbose: false), StringComparison.OrdinalIgnoreCase);
        feed.SetActivity("Pi is working…");
        Assert.Equal("Pi is working…", feed.BuildMap().CurrentActivity);
        feed.SetActivity("Running tool: read…");
        Assert.Contains("Running tool: read", feed.BuildMap().ToDisplayText(verbose: false), StringComparison.Ordinal);
        feed.SetActivity(null);
        Assert.Null(feed.BuildMap().CurrentActivity);
        Assert.Contains("idle", feed.BuildMap().ToDisplayText(verbose: false), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_ReceiptSummarizes_WithoutAddingErrorEvents()
    {
        // An unhealthy receipt with no tool failures: zero error EVENTS,
        // but the summary stays visible as LastReceipt.
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "text");
        feed.EndAssistantTurn(seq, new PiRunReceipt(true, false, 1, 4, 2));
        var map = feed.BuildMap();
        Assert.Equal(0, map.ErrorCount);
        Assert.NotNull(map.LastReceipt);
        Assert.Contains(map.Milestones, m => m.Kind == "receipt");

        // A transport loss is recorded as one error event, never a receipt.
        feed.AddError("Connection to Pi lost: stdout closed.");
        map = feed.BuildMap();
        Assert.Equal(1, map.ErrorCount);
        Assert.DoesNotContain(map.Milestones, m => m.Kind == "receipt" && m.Seq > seq + 1);
    }

    [Fact]
    public void RunProgressSink_DropsLateReports_AcrossRuns()
    {
        // Simulates queued Progress callbacks arriving after their run
        // closed (cancel/complete) and after the next run began: they must
        // be dropped, never appended into the new run's accumulator.
        var first = new RunProgressSink();
        first.Report("run1-a ");
        Assert.True(first.IsOpen);
        first.Close(); // run ends (cancel or complete)
        Assert.False(first.IsOpen);
        first.Report("run1-LATE "); // delayed callback from the dead run
        Assert.Equal("run1-a ", first.Accumulator.FlushAndGetCommitted());

        var second = new RunProgressSink(); // next prompt's sink
        second.Report("run2-a");
        first.Report("run1-STALER "); // staler still arriving
        Assert.Equal("run2-a", second.Accumulator.FlushAndGetCommitted());
        Assert.Equal("run1-a ", first.Accumulator.CommittedText);
    }

    [Fact]
    public void FeedPath_BoundedStreaming_StaysExact_WithFewFlushes()
    {
        // Bounded companion to the accumulator micro-benchmark: the fuller
        // Core path (accumulator flush -> feed AppendDelta concatenation),
        // still excluding Avalonia layout and clipboard. 50 flushes x 10 KB.
        var sink = new RunProgressSink();
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        var chunk = new string('y', 10_000);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            sink.Report(chunk);
            var flushed = sink.Accumulator.Flush();
            Assert.NotEmpty(flushed);
            feed.AppendDelta(seq, flushed);
        }

        watch.Stop();
        sink.Close();
        feed.EndAssistantTurn(seq, null);
        var text = feed.GetEntryText(seq);
        Assert.NotNull(text);
        Assert.Equal(500_000, text.Length);
        Assert.Equal(new string('y', 500_000), text);
        Assert.Equal(50, sink.Accumulator.FlushCount);
        _output.WriteLine($"FEED PATH (Core only, no Avalonia): 50 flushes x 10 KB = {text.Length:N0} chars in {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Feed_Reconcile_ReplacesPartialChunks_WithAuthoritativeText()
    {
        var feed = new ConversationFeed();
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "partial chunk");
        feed.ReconcileStreamingText(seq, "authoritative full text");
        Assert.Equal("authoritative full text", feed.Snapshot().Single().Text);
        feed.EndAssistantTurn(seq, null);
        feed.ReconcileStreamingText(seq, "ignored — turn ended");
        Assert.Equal("authoritative full text", feed.Snapshot().Single().Text);
        feed.ReconcileStreamingText(999_999, "ignored — unknown turn");
    }

    [Fact]
    public void Feed_Transcript_PreservesMessageBodies_WithRoleLabels()
    {
        var feed = new ConversationFeed();
        feed.AddUser("exact user text 🚀");
        var seq = feed.BeginAssistantTurn();
        feed.AppendDelta(seq, "exact assistant text");
        feed.EndAssistantTurn(seq, null);

        var transcript = feed.BuildTranscriptText();
        Assert.Contains("exact user text 🚀", transcript, StringComparison.Ordinal);
        Assert.Contains("exact assistant text", transcript, StringComparison.Ordinal);
        Assert.Contains("[You", transcript, StringComparison.Ordinal);
        Assert.Contains("[Pi", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_DerivesMilestonesChangedFilesAndTests_WithoutFabricatingDecisions()
    {
        var feed = new ConversationFeed();
        feed.AddUser("Fix the failing tests");
        feed.AddToolStart("read", "src/Foo.cs", path: null, isMutating: false);
        feed.AddToolEnd("read", isError: false);
        feed.AddToolStart("edit", "src/Foo.cs", path: "src/Foo.cs", isMutating: true);
        feed.AddToolEnd("edit", isError: false);
        feed.AddToolStart("bash", "dotnet test AgentX.sln --no-build", path: null, isMutating: false);
        feed.AddToolEnd("bash", isError: true);
        feed.EndAssistantTurn(feed.BeginAssistantTurn(), new PiRunReceipt(true, false, 1, 5, 4));

        var map = feed.BuildMap();
        Assert.Contains(map.Milestones, m => m.Kind == "prompt");
        Assert.Contains(map.Milestones, m => m.Kind == "tool" && m.Title.Contains("edit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(map.Milestones, m => m.Kind == "receipt");
        Assert.Contains(map.Milestones, m => m.Kind == "error");
        Assert.Equal(["src/Foo.cs"], map.ChangedFiles); // edit path only; read is not a change.
        Assert.DoesNotContain("src/Foo.cs", map.TestCommands);
        Assert.Single(map.TestCommands); // bash test command observed (heuristic).
        // One failed tool + its summarizing receipt counts as ONE error event:
        // the receipt summarizes the run, it is not a second failure.
        Assert.Equal(1, map.ErrorCount);
        Assert.DoesNotContain(map.Milestones, m => m.Kind == "decision"); // Decisions are never inferred.
        Assert.All(map.Milestones, m => Assert.True(m.Seq > 0)); // Every milestone links to feed evidence.

        var display = map.ToDisplayText(verbose: false);
        Assert.Contains("heuristic", display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not verified on disk", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_EmptyFeed_StaysHonest()
    {
        var map = new ConversationFeed().BuildMap();
        Assert.Empty(map.Milestones);
        Assert.Empty(map.ChangedFiles);
        Assert.Empty(map.TestCommands);
        Assert.Equal(0, map.ErrorCount);
        Assert.Contains("idle", map.ToDisplayText(verbose: true), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolActivity_ParsesPathsCommandsAndOutcomes_WithoutFabrication()
    {
        AssertActivity("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"edit","args":{"path":"src/Foo.cs"}}""",
            "edit", "src/Foo.cs", IsMutating: true, IsStart: true);
        AssertActivity("""{"type":"tool_execution_start","toolCallId":"c2","toolName":"bash","args":{"command":"dotnet test"}}""",
            "bash", Path: null, IsMutating: false, IsStart: true, SummaryContains: "dotnet test");
        AssertActivity("""{"type":"tool_execution_start","toolCallId":"c3","toolName":"read","args":{"path":"src/Foo.cs"}}""",
            "read", Path: null, IsMutating: false, IsStart: true); // A read path is not a change.
        AssertActivity("""{"type":"tool_execution_end","toolCallId":"c2","toolName":"bash","isError":true}""",
            "bash", Path: null, IsMutating: false, IsStart: false, IsError: true);

        using var other = JsonDocument.Parse("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"hi"}}""");
        Assert.Null(PiConversation.ParseToolActivity(other));
        using var malformed = JsonDocument.Parse("""{"type":"tool_execution_start"}""");
        Assert.Null(PiConversation.ParseToolActivity(malformed)); // No toolName: no fabricated activity.
    }

    [Fact]
    public void StreamingBatching_MatchesNaiveText_WithFewFlushes()
    {
        // Micro-benchmark for the per-character UI rebuild this slice
        // replaces (MainWindow used to assign TextBox.Text = full ToString()
        // on every delta). Scope is deliberately narrow: it measures the
        // STRING cost of naive rebuild vs batched accumulation only — not
        // feed concatenation (see FeedPath_BoundedStreaming), Avalonia
        // layout, or clipboard. Timings print to test output for the PR
        // record; the assertions pin exactness and batching, never
        // wall-clock time.
        const int deltas = 20_000;
        const string delta = "0123456789abcdefghij012345"; // 26 chars -> ~520KB

        var naive = new StringBuilder();
        var naiveWatch = Stopwatch.StartNew();
        for (var i = 0; i < deltas; i++)
        {
            naive.Append(delta);
            _ = naive.ToString();
        }

        naiveWatch.Stop();

        var accumulator = new StreamingTextAccumulator();
        var batchedWatch = Stopwatch.StartNew();
        for (var i = 0; i < deltas; i++)
        {
            accumulator.Append(delta);
            if ((i + 1) % 2_000 == 0)
            {
                _ = accumulator.Flush(); // One incremental UI append per batch.
            }
        }

        var batchedText = accumulator.FlushAndGetCommitted();
        batchedWatch.Stop();

        Assert.Equal(naive.ToString(), batchedText);
        Assert.Equal(520_000, batchedText.Length);
        Assert.Equal(10, accumulator.FlushCount); // One flush per 2,000-delta batch; the final call finds nothing pending.
        _output.WriteLine($"NAIVE per-delta ToString (strings only, no UI): {naiveWatch.ElapsedMilliseconds} ms for {deltas:N0} deltas.");
        _output.WriteLine($"BATCHED accumulator flush (strings only, no UI): {batchedWatch.ElapsedMilliseconds} ms for {deltas:N0} deltas in {accumulator.FlushCount} flushes.");
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ConversationFeedTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static void AssertActivity(
        string line, string toolName, string? Path, bool IsMutating, bool IsStart,
        bool IsError = false, string? SummaryContains = null)
    {
        using var record = JsonDocument.Parse(line);
        var activity = PiConversation.ParseToolActivity(record);
        Assert.NotNull(activity);
        Assert.Equal(toolName, activity.ToolName);
        Assert.Equal(Path, activity.Path);
        Assert.Equal(IsMutating, activity.IsMutating);
        Assert.Equal(IsStart, activity.IsStart);
        Assert.Equal(IsError, activity.IsError);
        if (SummaryContains is not null)
        {
            Assert.Contains(SummaryContains, activity.Summary ?? string.Empty, StringComparison.Ordinal);
        }
    }
}
