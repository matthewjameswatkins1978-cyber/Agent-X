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
    public void Feed_EndTurn_ClearsActivity_SoSettledMapShowsIdle()
    {
        // Settle-transition regression (PR #4 re-review): ending a turn must
        // drop transient activity in Core, so a map rendered in any host call
        // order shows idle — never a stale "working" state — once Pi settles.
        // Covers both the success path (receipt) and the cancel path (null).
        var feed = new ConversationFeed();
        feed.SetActivity("Running tool: read…");
        var completed = feed.BeginAssistantTurn();
        feed.AppendDelta(completed, "text");
        feed.EndAssistantTurn(completed, new PiRunReceipt(true, false, 0, 4, 2));
        Assert.Null(feed.BuildMap().CurrentActivity);
        Assert.Contains("idle", feed.BuildMap().ToDisplayText(verbose: false), StringComparison.OrdinalIgnoreCase);

        feed.SetActivity("Pi is working…");
        var cancelled = feed.BeginAssistantTurn();
        feed.AppendDelta(cancelled, "partial");
        feed.EndAssistantTurn(cancelled, null);
        Assert.Null(feed.BuildMap().CurrentActivity);
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
    public void Feed_CopySource_PreservesMultilineToolOutput_ByteForByte()
    {
        // Luna acceptance (clipboard): multiline tool output must round-
        // trip through both copy paths with newlines and spacing intact.
        var feed = new ConversationFeed();
        feed.AddUser("list files");
        var tool = feed.BeginAssistantTurn();
        var output = "alpha.txt\n  beta gamma.txt\n\ttrailing  spaces  \n\u4e2d\u6587";
        feed.AppendDelta(tool, output);
        feed.EndAssistantTurn(tool, new PiRunReceipt(true, false, 0, 9, 2));
        Assert.Equal(output, feed.GetEntryText(tool));
        Assert.Contains(output, feed.BuildTranscriptText(), StringComparison.Ordinal);
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
    public void ToolGrouping_CoherentBlock_SequentialAndNested()
    {
        var feed = new ConversationFeed();
        var read = feed.AddToolStart("read", "src/Foo.cs", path: null, isMutating: false);
        Assert.Equal(read, feed.AddToolEnd("read", isError: false)); // Same seq: one block.
        var entries = feed.Snapshot();
        var single = Assert.Single(entries);
        Assert.Equal(read, single.Seq);
        Assert.Contains("src/Foo.cs", single.Text, StringComparison.Ordinal);
        Assert.Contains("finished", single.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(single.IsError);

        // Nested same-name calls WITHOUT observed IDs cannot be paired by
        // order (ends may arrive in start order): each end stays a separate
        // unverified outcome row — never a swapped attribution.
        var outer = feed.AddToolStart("bash", "outer", path: null, isMutating: false);
        var inner = feed.AddToolStart("bash", "inner", path: null, isMutating: false);
        var endA = feed.AddToolEnd("bash", isError: true);
        var endB = feed.AddToolEnd("bash", isError: false);
        Assert.NotEqual(inner, endA);
        Assert.NotEqual(outer, endB);
        var rows = feed.Snapshot();
        Assert.Equal(5, rows.Count); // read block + outer + inner starts, two separate ends.
        Assert.Contains(rows, e => e.Seq == endA && e.IsError && e.Note != null && e.Note.Contains("(unverified)", StringComparison.Ordinal));
        Assert.Contains(rows, e => e.Seq == endB && !e.IsError && e.Note != null && e.Note.Contains("(unverified)", StringComparison.Ordinal));
        // The starts keep their summaries, unmodified by the ends.
        Assert.Equal("outer", rows.First(e => e.Seq == outer).Text);
        Assert.Equal("inner", rows.First(e => e.Seq == inner).Text);
    }

    [Fact]
    public void ToolGrouping_CallIds_PairInterleavedSameName_RegardlessOfOrder()
    {
        // Two overlapping bash calls whose ends arrive in START order (the
        // swap case): identity pairing puts each outcome on its own row.
        var feed = new ConversationFeed();
        var first = feed.AddToolStart("bash", "first command", path: null, isMutating: false, callId: "call-A");
        var second = feed.AddToolStart("bash", "second command", path: null, isMutating: false, callId: "call-B");
        Assert.Equal(first, feed.AddToolEnd("bash", isError: true, callId: "call-A"));
        Assert.Equal(second, feed.AddToolEnd("bash", isError: false, callId: "call-B"));
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count);
        var firstRow = rows.First(e => e.Seq == first);
        Assert.True(firstRow.IsError);
        Assert.Contains("first command", firstRow.Text, StringComparison.Ordinal);
        var secondRow = rows.First(e => e.Seq == second);
        Assert.False(secondRow.IsError);
        Assert.Contains("second command", secondRow.Text, StringComparison.Ordinal);

        // Reverse arrival order pairs just as exactly.
        var feed2 = new ConversationFeed();
        var a = feed2.AddToolStart("bash", "cmd-a", path: null, isMutating: false, callId: "id-1");
        var b = feed2.AddToolStart("bash", "cmd-b", path: null, isMutating: false, callId: "id-2");
        Assert.Equal(b, feed2.AddToolEnd("bash", isError: false, callId: "id-2"));
        Assert.Equal(a, feed2.AddToolEnd("bash", isError: true, callId: "id-1"));
        Assert.True(feed2.Snapshot().First(e => e.Seq == a).IsError);
        Assert.False(feed2.Snapshot().First(e => e.Seq == b).IsError);
    }

    [Fact]
    public void ToolGrouping_UnknownCallId_StaysSeparateWithoutLoss()
    {
        var feed = new ConversationFeed();
        feed.AddToolStart("bash", "real", path: null, isMutating: false, callId: "call-real");
        var seq = feed.AddToolEnd("bash", isError: true, callId: "call-ghost");
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count);
        var orphan = rows.First(e => e.Seq == seq);
        Assert.True(orphan.IsError);
        Assert.Contains("(unverified)", orphan.Note ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(orphan.CallId); // Unproven start → never an identity key.
        // The proven start is untouched and still pairable by its own ID.
        var start = rows.First(e => e.Note == "started: bash");
        Assert.Equal("real", start.Text);
    }

    [Fact]
    public void ToolGrouping_LoadHistory_ClearsPairingAcrossContexts()
    {
        var feed = new ConversationFeed();
        feed.AddToolStart("bash", "workspace A command", path: null, isMutating: false, callId: "call-A");
        // Workspace switch replaces the feed: A's in-flight pairing is gone.
        feed.LoadHistory([new ConversationMessage("user", "hello B")]);
        var seq = feed.AddToolEnd("bash", isError: false, callId: "call-A");
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count); // Restored user row + separate end row.
        Assert.Equal("hello B", rows[0].Text);
        Assert.Contains("(unverified)", rows[1].Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGrouping_SettledTurn_AbandonsOrphans()
    {
        var feed = new ConversationFeed();
        feed.AddToolStart("bash", "killed", path: null, isMutating: false, callId: "call-1");
        feed.EndAssistantTurn(1, receipt: null); // Cancel path settles with no receipt.
        var seq = feed.AddToolEnd("bash", isError: false, callId: "call-1");
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count); // Orphan start + separate end, never merged.
        Assert.Contains("(unverified)", rows.First(e => e.Seq == seq).Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGrouping_OrphanEnd_FallsBackWithoutLoss()
    {
        var feed = new ConversationFeed();
        var seq = feed.AddToolEnd("bash", isError: true); // No open start.
        var entry = Assert.Single(feed.Snapshot());
        Assert.Equal(seq, entry.Seq);
        Assert.True(entry.IsError);
        Assert.Contains("failed", entry.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(unverified)", entry.Note ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolGrouping_PreservesMapEvidence()
    {
        var feed = new ConversationFeed();
        feed.AddToolStart("edit", "src/Foo.cs", path: "src/Foo.cs", isMutating: true);
        feed.AddToolEnd("edit", isError: false);
        var map = feed.BuildMap();
        Assert.Contains(map.Milestones, m => m.Kind == "tool" && m.Title.Contains("edit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["src/Foo.cs"], map.ChangedFiles);
        Assert.Equal(0, map.ErrorCount);
    }

    [Fact]
    public void ReconcileTools_ReplacesPlaceholders_WithPersistedBodies()
    {
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "src/Foo.cs", path: null, isMutating: false);
        feed.AddToolEnd("read", isError: false);
        var history = new List<ConversationMessage>
        {
            new("user", "read it"),
            new("assistant", "reading"),
            new("tool", "[tool result: read]\nline one\nline two"),
        };
        Assert.Equal(ReconcileOutcome.Applied, feed.ReconcileToolResults(history));
        Assert.Contains("line one\nline two", feed.Snapshot().Single().Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_SkipsMisalignedHistories()
    {
        // Cancelled mid-call: orphan start has no history counterpart.
        // Nothing is touched rather than risking wrong replacement.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "src/Foo.cs", path: null, isMutating: false);
        feed.AddToolEnd("read", isError: false);
        feed.AddToolStart("bash", "sleep 99", path: null, isMutating: false); // Orphan: killed.
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nfull body"),
        };
        Assert.Equal(ReconcileOutcome.SkippedCountMismatch, feed.ReconcileToolResults(history));
        Assert.Equal(2, feed.Snapshot().Count);
        Assert.DoesNotContain("full body", feed.Snapshot()[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("proven to match their invocation", string.Join("\n", feed.Snapshot().Select(e => e.Text)), StringComparison.Ordinal);
        Assert.Equal(ReconcileOutcome.SkippedCountMismatch, feed.ReconcileToolResults([]));
        Assert.Equal(2, feed.Snapshot().Count);
    }

    [Fact]
    public void ReconcileTools_AppliesUniqueNameGroups_EvenWhenHistoryOrderDiffers()
    {
        // One read and one bash call, history returned in reverse order:
        // each (name, outcome) group has exactly ONE row and ONE body, so
        // the mapping is provable without position — applied to the right
        // rows, no marker.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "read it", path: null, isMutating: false);
        feed.AddToolEnd("read", isError: false);
        feed.AddToolStart("bash", "run it", path: null, isMutating: false);
        feed.AddToolEnd("bash", isError: true);
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool bash failed]\nbash body"),
            new("tool", "[tool result: read]\nread body"),
        };
        Assert.Equal(ReconcileOutcome.Applied, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count); // Bodies only — no marker for a proven mapping.
        Assert.Equal("[tool result: read]\nread body", rows[0].Text);
        Assert.Equal("[tool bash failed]\nbash body", rows[1].Text);
        // A repeat with rows already in sync changes nothing, adds nothing.
        Assert.Equal(ReconcileOutcome.InSync, feed.ReconcileToolResults(history));
        Assert.Equal(2, feed.Snapshot().Count);
    }

    [Fact]
    public void ReconcileTools_RefusesTwoSameNameSameOutcome_WhenHistoryOrderIsSwapped()
    {
        // Lucy's case: two concurrent successful reads (A/B), persisted
        // results returned B/A, history carries no call IDs. Headers and
        // error flags are identical — position cannot prove identity, so
        // neither row is overwritten and one truthful marker records it.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "first read", path: null, isMutating: false, callId: "call-A");
        feed.AddToolEnd("read", isError: false, callId: "call-A");
        feed.AddToolStart("read", "second read", path: null, isMutating: false, callId: "call-B");
        feed.AddToolEnd("read", isError: false, callId: "call-B");
        var rowTexts = feed.Snapshot().Select(r => r.Text).ToArray();
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-B"),
            new("tool", "[tool result: read]\nBODY-A"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(3, rows.Count); // two rows untouched + one marker
        Assert.Equal(rowTexts[0], rows[0].Text);
        Assert.Equal(rowTexts[1], rows[1].Text);
        Assert.DoesNotContain("BODY-A", string.Join("\n", rows.Select(r => r.Text)), StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-B", string.Join("\n", rows.Select(r => r.Text)), StringComparison.Ordinal);
        Assert.Contains("proven to match their invocation", rows[2].Text, StringComparison.Ordinal);
        // A repeat refusal must not stack markers.
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        Assert.Equal(3, feed.Snapshot().Count);
    }

    [Fact]
    public void ReconcileTools_CallIdIdentity_ResolvesSwappedSameNameHistory()
    {
        // Same two successful reads — but the history DOES carry the call
        // IDs (both sides derive from toolCall.id), in swapped order.
        // Identity pairing assigns each body to its own row, never by
        // position.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "first read", path: null, isMutating: false, callId: "call-A");
        feed.AddToolEnd("read", isError: false, callId: "call-A");
        feed.AddToolStart("read", "second read", path: null, isMutating: false, callId: "call-B");
        feed.AddToolEnd("read", isError: false, callId: "call-B");
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-B", CallId: "call-B"),
            new("tool", "[tool result: read]\nBODY-A", CallId: "call-A"),
        };
        Assert.Equal(ReconcileOutcome.Applied, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count); // no marker: mapping was proven
        Assert.Contains("BODY-A", rows[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-B", rows[0].Text, StringComparison.Ordinal);
        Assert.Contains("BODY-B", rows[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-A", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_DuplicateHistoryCallIds_AreUnprovable()
    {
        // An ID repeated in the history identifies nothing: no guessing.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "one", path: null, isMutating: false, callId: "call-1");
        feed.AddToolEnd("read", isError: false, callId: "call-1");
        feed.AddToolStart("read", "two", path: null, isMutating: false, callId: "call-2");
        feed.AddToolEnd("read", isError: false, callId: "call-2");
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-1", CallId: "dup"),
            new("tool", "[tool result: read]\nBODY-2", CallId: "dup"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain("BODY-", rows[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("BODY-", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_ConflictingKnownIds_UniqueNameNeverOverrides()
    {
        // One completed read row with CallId "call-A"; the sole persisted
        // body carries CallId "call-B". Step 1 cannot match (IDs differ);
        // Step 2 must NOT fall back to the unique (read, success) name —
        // explicit identity disagreement wins over name/outcome exclusivity.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "first read", path: null, isMutating: false, callId: "call-A");
        feed.AddToolEnd("read", isError: false, callId: "call-A");
        var original = feed.Snapshot().Single().Text;
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-B", CallId: "call-B"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count); // row untouched + one truthful marker
        Assert.Equal(original, rows[0].Text);
        Assert.DoesNotContain("BODY-B", rows[0].Text, StringComparison.Ordinal);
        Assert.Contains("proven to match their invocation", rows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_MixedValidIdMatchAndConflictingPair_AppliesOnlyProven()
    {
        // One exact-ID match (call-1) alongside a leftover pair whose known
        // IDs disagree (call-2 vs call-3). The proven pair applies; the
        // conflicting pair must NOT be name-fallback-paired — its text stays
        // and one truthful marker records the refusal.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "first read", path: null, isMutating: false, callId: "call-1");
        feed.AddToolEnd("read", isError: false, callId: "call-1");
        feed.AddToolStart("read", "second read", path: null, isMutating: false, callId: "call-2");
        feed.AddToolEnd("read", isError: false, callId: "call-2");
        var secondText = feed.Snapshot().Last().Text;
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-1", CallId: "call-1"),
            new("tool", "[tool result: read]\nBODY-3", CallId: "call-3"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(3, rows.Count); // two rows + one marker
        Assert.Contains("BODY-1", rows[0].Text, StringComparison.Ordinal); // proven pair applied
        Assert.Equal(secondText, rows[1].Text);                            // conflicting pair refused
        Assert.DoesNotContain("BODY-3", rows[1].Text, StringComparison.Ordinal);
        Assert.Contains("proven to match their invocation", rows[2].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_DuplicateId_BlocksOtherwiseUniqueNameFallback()
    {
        // "dup" appears on a leftover read row AND a leftover history body
        // — but it is ALSO duplicated on a bash body, so the read row's ID
        // does not uniquely identify its body. Each (name, outcome) group is
        // exactly 1 row / 1 body (would pass unique-name matching), yet the
        // known duplicated identity must block the fallback for those keys.
        var feed = new ConversationFeed();
        feed.AddToolStart("read", "first read", path: null, isMutating: false, callId: "dup");
        feed.AddToolEnd("read", isError: false, callId: "dup");
        feed.AddToolStart("bash", "second", path: null, isMutating: false); // no ID
        feed.AddToolEnd("bash", isError: true);
        var originals = feed.Snapshot().Select(r => r.Text).ToArray();
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nBODY-1", CallId: "dup"),
            new("tool", "[tool bash failed]\nBODY-2", CallId: "dup"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(3, rows.Count); // two rows unchanged + one marker
        Assert.Equal(originals[0], rows[0].Text);
        Assert.Equal(originals[1], rows[1].Text);
        Assert.DoesNotContain("BODY-", string.Join("\n", rows.Select(r => r.Text)), StringComparison.Ordinal);
        Assert.Contains("proven to match their invocation", rows[2].Text, StringComparison.Ordinal);
        // Repeat: refusal must not stack markers.
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        Assert.Equal(3, feed.Snapshot().Count);
    }

    [Fact]
    public void ReconcileTools_RestoredRowsWithoutLifecycles_InSyncWithoutMarker()
    {
        // Restored rows ARE their own history: no lifecycle note to group
        // and no IDs, but the texts match as a multiset — every possible
        // mapping is a no-op, so nothing changes and no marker appears.
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: read]\nsame body"),
            new("tool", "[tool bash failed]\nsame failure"),
        };
        var feed = new ConversationFeed();
        feed.LoadHistory(history);
        Assert.Equal(ReconcileOutcome.InSync, feed.ReconcileToolResults(history));
        var rows = feed.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.Equal("[tool result: read]\nsame body", rows[0].Text);
        Assert.Equal("[tool bash failed]\nsame failure", rows[1].Text);
    }

    [Fact]
    public void ReconcileTools_RefusesUnverifiableBodies()
    {
        // "Ran ..." bash-execution bodies carry no tool name: unprovable.
        var feed = new ConversationFeed();
        feed.AddToolStart("bash", "run it", path: null, isMutating: false);
        feed.AddToolEnd("bash", isError: false);
        var history = new List<ConversationMessage>
        {
            new("tool", "Ran `run it`\noutput here"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        Assert.Equal(2, feed.Snapshot().Count);
        Assert.DoesNotContain("output here", feed.Snapshot()[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconcileTools_RefusesOpenStart()
    {
        // A still-open start can never match a persisted result, even when
        // the counts line up.
        var feed = new ConversationFeed();
        feed.AddToolStart("bash", "run it", path: null, isMutating: false);
        var history = new List<ConversationMessage>
        {
            new("tool", "[tool result: bash]\nbody"),
        };
        Assert.Equal(ReconcileOutcome.SkippedAmbiguous, feed.ReconcileToolResults(history));
        Assert.Equal(2, feed.Snapshot().Count); // Start row + marker.
    }

    [Fact]
    public void ParseHistoryToolIdentity_ReadsPersistedHeaders()
    {
        Assert.Equal(("read", false), ConversationFeed.ParseHistoryToolIdentity("[tool result: read]\nbody"));
        Assert.Equal(("bash", true), ConversationFeed.ParseHistoryToolIdentity("[tool bash failed]\nbody"));
        Assert.Null(ConversationFeed.ParseHistoryToolIdentity("Ran `cmd`\noutput"));
        Assert.Null(ConversationFeed.ParseHistoryToolIdentity("plain text"));
        Assert.Null(ConversationFeed.ParseHistoryToolIdentity("[tool result: ]\nbody"));
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
    public void History_ParsesToolCallIds_AndFeedPropagatesThem()
    {
        var oversized = "{\"data\":{\"messages\":["
            + "{\"role\":\"toolResult\",\"toolCallId\":\"" + new string('x', 200) + "\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"y\"}],\"isError\":false}"
            + "]}}";
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"toolResult","toolCallId":"call-abc|fc-1","toolName":"read","content":[{"type":"text","text":"body"}],"isError":false},{"role":"toolResult","toolCallId":"","toolName":"read","content":[{"type":"text","text":"x"}],"isError":false},{"role":"user","content":"hi"}]}}""");
        var history = PiConversation.ParseHistory(response);
        Assert.Equal(3, history.Count);
        Assert.Equal("call-abc|fc-1", history[0].CallId); // Raw: same value toolCall.id emits.
        Assert.Null(history[1].CallId); // Empty → null (never an identity key).
        Assert.Null(history[2].CallId);

        using var oversizedResponse = JsonDocument.Parse(oversized);
        var oversizedHistory = PiConversation.ParseHistory(oversizedResponse);
        Assert.Null(oversizedHistory[0].CallId); // Oversized → null.

        var feed = new ConversationFeed();
        feed.LoadHistory(history);
        var entries = feed.Snapshot();
        Assert.Equal("call-abc|fc-1", entries[0].CallId); // Restored rows keep identity.
        Assert.Null(entries[1].CallId);
        Assert.Null(entries[2].CallId);
    }

    [Fact]
    public void History_RestoredFailuresCountLikeLiveOnes()
    {
        // Pi's own persisted isError flag propagates: restored failures
        // count in the session map exactly like live tool failures —
        // observed evidence, never inferred from text.
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"user","content":"go"},{"role":"toolResult","toolCallId":"c1","toolName":"exec","content":[{"type":"text","text":"boom"}],"isError":true},{"role":"toolResult","toolCallId":"c2","toolName":"read","content":[{"type":"text","text":"ok"}],"isError":false},{"role":"bashExecution","command":"Get-Date","output":"...","isError":false}]}}""");
        var history = PiConversation.ParseHistory(response);
        Assert.Equal(4, history.Count);
        Assert.True(history[1].IsError);
        Assert.False(history[2].IsError);
        Assert.False(history[3].IsError);
        var feed = new ConversationFeed();
        feed.LoadHistory(history);
        var map = feed.BuildMap();
        Assert.Equal(1, map.ErrorCount);
        Assert.Contains(map.Milestones, m => m.Kind == "error");
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

    [Fact]
    public void FeedBurst_HighRateToolEvents_StayCoherentAndFast()
    {
        // 8k mixed mutations incl. 100 interleaved same-name calls closed in
        // reverse order: identity pairing keeps every outcome on its row.
        var feed = new ConversationFeed();
        var watch = Stopwatch.StartNew();
        const int pairs = 100;
        for (var i = 0; i < pairs; i++)
        {
            feed.AddToolStart("bash", $"command {i}", path: null, isMutating: false, callId: $"call-{i}");
        }

        for (var i = pairs - 1; i >= 0; i--)
        {
            var isError = i % 2 == 0;
            Assert.Equal(feed.Snapshot().First(e => e.Text == $"command {i}").Seq,
                feed.AddToolEnd("bash", isError, callId: $"call-{i}"));
        }

        for (var i = 0; i < 7700; i++)
        {
            feed.AddSystemNotice($"detail row {i}");
        }

        watch.Stop();
        var rows = feed.Snapshot();
        Assert.Equal(pairs + 7700, rows.Count);
        Assert.Equal(50, rows.Count(e => e.Role == "tool" && e.IsError));
        Assert.Equal(50, rows.Count(e => e.Role == "tool" && !e.IsError));
        Assert.DoesNotContain(rows, e => (e.Note ?? string.Empty).Contains("(unverified)", StringComparison.Ordinal));
        _output.WriteLine($"BURST feed mutations: {pairs * 2 + 7700:N0} ops in {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Expansion_BoundsHundredThousandLines_PreservesSource()
    {
        var builder = new StringBuilder(100_000 * 62);
        for (var i = 0; i < 100_000; i++)
        {
            builder.Append("output line ").Append(i.ToString("D6")).Append(" — padding to reach sixty-two chars.\n");
        }

        var source = builder.ToString();
        var watch = Stopwatch.StartNew();
        var display = FeedEntry.TruncateExpanded(source);
        watch.Stop();
        Assert.True(display.Length <= 100_000 + 256, $"display={display.Length}");
        Assert.Contains("of 100,000 lines", display, StringComparison.Ordinal);
        Assert.Contains(source.Length.ToString("N0"), display, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copy the message", display, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100_000, source.Count(c => c == '\n')); // Source untouched.
        _output.WriteLine($"EXPAND 100k-line bound: source {source.Length:N0} chars -> display {display.Length:N0} chars in {watch.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public void Expansion_ShortText_PassesThrough()
    {
        Assert.Equal("small", FeedEntry.TruncateExpanded("small"));
        Assert.Equal(string.Empty, FeedEntry.TruncateExpanded(string.Empty));
    }

    [Fact]
    public void Coalescer_BoundsPendingWork_LatestWins()
    {
        var queued = new List<Action>();
        var coalescer = new FeedEventCoalescer(a => queued.Add(a));
        var runs = new List<int>();
        for (var i = 0; i < 8000; i++)
        {
            var capture = i;
            coalescer.Signal(() => runs.Add(capture));
        }

        Assert.Single(queued); // 8k signals collapse to one pending post.
        queued[0]();
        Assert.Single(runs);
        Assert.Equal(7999, runs[0]); // Latest state wins, none lost in transit.
        queued.Clear();
        coalescer.Signal(() => runs.Add(-1));
        Assert.Single(queued);
        queued[0]();
        Assert.Equal(2, runs.Count);
    }

    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ConversationFeedTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static void AssertActivity(
        string line, string toolName, string? Path, bool IsMutating, bool IsStart,
        bool IsError = false, string? SummaryContains = null, string? CallId = "UNCHECKED")
    {
        using var record = JsonDocument.Parse(line);
        var activity = PiConversation.ParseToolActivity(record);
        Assert.NotNull(activity);
        Assert.Equal(toolName, activity.ToolName);
        Assert.Equal(Path, activity.Path);
        Assert.Equal(IsMutating, activity.IsMutating);
        Assert.Equal(IsStart, activity.IsStart);
        Assert.Equal(IsError, activity.IsError);
        if (CallId != "UNCHECKED")
        {
            Assert.Equal(CallId, activity.CallId);
        }
        if (SummaryContains is not null)
        {
            Assert.Contains(SummaryContains, activity.Summary ?? string.Empty, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ToolActivity_ParsesCallIds_WithoutFabrication()
    {
        AssertActivity("""{"type":"tool_execution_start","toolCallId":"call-abc-123","toolName":"bash","args":{}}""",
            "bash", Path: null, IsMutating: false, IsStart: true, CallId: "call-abc-123");
        AssertActivity("""{"type":"tool_execution_end","toolCallId":"call-abc-123","toolName":"bash","isError":false}""",
            "bash", Path: null, IsMutating: false, IsStart: false, CallId: "call-abc-123");
        // Absent, empty and oversized IDs all read as null: no pairing key.
        AssertActivity("""{"type":"tool_execution_start","toolName":"bash","args":{}}""",
            "bash", Path: null, IsMutating: false, IsStart: true, CallId: null);
        AssertActivity("""{"type":"tool_execution_end","toolCallId":"","toolName":"bash"}""",
            "bash", Path: null, IsMutating: false, IsStart: false, CallId: null);
        var oversized = "{\"type\":\"tool_execution_start\",\"toolCallId\":\"" + new string('x', 200) + "\",\"toolName\":\"bash\",\"args\":{}}";
        AssertActivity(oversized,
            "bash", Path: null, IsMutating: false, IsStart: true, CallId: null);
    }
}
