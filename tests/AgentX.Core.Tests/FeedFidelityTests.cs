using System.Reflection;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Pi Feed Fidelity regressions: session renames, auto-retries and
/// compactions decode as typed events (verified against Pi 1.3.0's own
/// emissions and message constructors); restored failed/aborted assistant
/// turns and non-zero bash outcomes keep their observed evidence instead
/// of restoring as blank clean rows. Synthetic wire records only — no Pi
/// process, network, or inference.
/// </summary>
public sealed class FeedFidelityTests
{
    private static readonly DateTime At = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static AgentXEvent Decode(string json) => PiEventTap.Parse(json, At);

    private static List<ConversationMessage> HistoryOf(string messagesJson)
    {
        using var doc = JsonDocument.Parse(
            """{"type":"response","command":"get_messages","success":true,"data":{"messages":["""
            + messagesJson + "]}}");
        return PiConversation.ParseHistory(doc);
    }

    private static int CountErrors(string recordJson)
    {
        using var doc = JsonDocument.Parse(recordJson);
        var errors = 0;
        PiConversation.CountRunError(new PiRpcEventArgs(doc, recordJson), ref errors);
        return errors;
    }

    // ---- live renames ----------------------------------------------------

    [Fact]
    public void Rename_DecodesName_RendersAtDetailed()
    {
        var e = Assert.IsType<SessionRenamed>(Decode(
            """{"type":"session_info_changed","name":"fidprobe"}"""));
        Assert.Equal("fidprobe", e.Name);
        Assert.Contains("fidprobe", e.Render(VisibilityLevel.Detailed));
        Assert.Null(e.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void Rename_MissingName_DegradesToMetadata()
    {
        var e = Assert.IsType<OtherEvent>(Decode("""{"type":"session_info_changed"}"""));
        Assert.Equal("session_info_changed", e.EventType);
    }

    // ---- live retries -----------------------------------------------------

    [Fact]
    public void RetryStart_DecodesAttemptEnvelope()
    {
        var e = Assert.IsType<AutoRetryStarted>(Decode(
            """{"type":"auto_retry_start","attempt":2,"maxAttempts":5,"delayMs":1500,"errorMessage":"boom"}"""));
        Assert.Equal(2, e.Attempt);
        Assert.Equal(5, e.MaxAttempts);
        Assert.Equal(1500, e.DelayMs);
        Assert.Contains("2/5", e.Render(VisibilityLevel.Detailed));
        Assert.Contains("boom", e.Render(VisibilityLevel.Detailed));
        Assert.Null(e.Render(VisibilityLevel.Compact));
    }

    [Fact]
    public void RetryEnd_RecoveryAndFailure()
    {
        var ok = Assert.IsType<AutoRetryEnded>(Decode(
            """{"type":"auto_retry_end","success":true,"attempt":2}"""));
        Assert.True(ok.Success);
        Assert.Null(ok.FinalError);
        Assert.Contains("recovered", ok.Render(VisibilityLevel.Detailed));

        var failed = Assert.IsType<AutoRetryEnded>(Decode(
            """{"type":"auto_retry_end","success":false,"attempt":5,"finalError":"Retry cancelled"}"""));
        Assert.False(failed.Success);
        Assert.Contains("Retry cancelled", failed.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void RetryEnd_MissingSuccess_NeverFabricatesVerdict()
    {
        Assert.IsType<OtherEvent>(Decode("""{"type":"auto_retry_end","attempt":1}"""));
    }

    // ---- live compaction ---------------------------------------------------

    [Fact]
    public void Compaction_StartAndCleanEnd()
    {
        var start = Assert.IsType<CompactionStarted>(Decode(
            """{"type":"compaction_start","reason":"manual"}"""));
        Assert.Equal("manual", start.Reason);
        Assert.Contains("compacting", start.Render(VisibilityLevel.Detailed));
        Assert.Null(start.Render(VisibilityLevel.Compact));

        var end = Assert.IsType<CompactionEnded>(Decode(
            """{"type":"compaction_end","aborted":false}"""));
        Assert.False(end.Aborted);
        Assert.Null(end.ErrorMessage);
        Assert.Contains("finished", end.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Compaction_AbortedAndFailed()
    {
        var aborted = Assert.IsType<CompactionEnded>(Decode(
            """{"type":"compaction_end","aborted":true,"reason":"manual"}"""));
        Assert.Contains("aborted", aborted.Render(VisibilityLevel.Detailed));

        var failed = Assert.IsType<CompactionEnded>(Decode(
            """{"type":"compaction_end","aborted":false,"errorMessage":"no model"}"""));
        Assert.Contains("no model", failed.Render(VisibilityLevel.Detailed));
    }

    [Fact]
    public void Compaction_FailureCountsAsRunError_CleanEndDoesNot()
    {
        Assert.Equal(1, CountErrors("""{"type":"compaction_end","errorMessage":"no model"}"""));
        Assert.Equal(0, CountErrors("""{"type":"compaction_end","aborted":false}"""));
        Assert.Equal(0, CountErrors("""{"type":"compaction_end","aborted":true}"""));
    }

    // ---- bridge routing -----------------------------------------------------

    private static (FeedBridge Bridge, ConversationFeed Feed, PiEventTap Tap) Wire(
        VisibilityLevel level = VisibilityLevel.Detailed)
    {
        var feed = new ConversationFeed();
        var bridge = new FeedBridge(feed, level);
        bridge.Attach(new PiRpcClient());
        var tap = (PiEventTap)typeof(FeedBridge).GetField(
            "_tap", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(bridge)!;
        return (bridge, feed, tap);
    }

    [Fact]
    public void Bridge_RoutesStoryEventsAtDetailed_SuppressesAtCompact()
    {
        var wired = Wire();
        using var bridge = wired.Bridge;
        wired.Tap.Publish(new SessionRenamed(At, "fidprobe"));
        wired.Tap.Publish(new AutoRetryStarted(At, 1, 3, 500, "boom"));
        wired.Tap.Publish(new AutoRetryEnded(At, true, 1, null));
        wired.Tap.Publish(new CompactionStarted(At, "manual"));
        wired.Tap.Publish(new CompactionEnded(At, false, "manual", null));
        Assert.Equal(5, wired.Feed.Snapshot().Count(e => e.Role == "system"));

        var quiet = Wire(VisibilityLevel.Compact);
        using var quietBridge = quiet.Bridge;
        quiet.Tap.Publish(new SessionRenamed(At, "fidprobe"));
        quiet.Tap.Publish(new AutoRetryStarted(At, 1, 3, 500, "boom"));
        quiet.Tap.Publish(new CompactionStarted(At, "manual"));
        Assert.Equal(0, quiet.Feed.Snapshot().Count(e => e.Role == "system"));
    }

    [Fact]
    public void Bridge_FailedRetryAndCompaction_AreErrorRows()
    {
        var wired = Wire();
        using var bridge = wired.Bridge;
        wired.Tap.Publish(new AutoRetryEnded(At, false, 2, "gone"));
        wired.Tap.Publish(new CompactionEnded(At, false, "manual", "no model"));
        var errors = wired.Feed.Snapshot().Where(e => e.IsError).ToArray();
        Assert.Equal(2, errors.Length);
    }

    // ---- restored failed/aborted turns ---------------------------------------

    [Fact]
    public void Restore_ErrorAssistant_KeepsEvidence_AsError()
    {
        var history = HistoryOf(
            """{"role":"assistant","content":[],"stopReason":"error","errorMessage":"Connection reset","timestamp":1}""");
        var entry = Assert.Single(history);
        Assert.True(entry.IsError);
        Assert.Contains("Connection reset", entry.Text);
    }

    [Fact]
    public void Restore_AbortedAssistant_Marked_NotError()
    {
        var history = HistoryOf(
            """{"role":"assistant","content":[],"stopReason":"aborted","errorMessage":"Request was aborted","timestamp":1}""");
        var entry = Assert.Single(history);
        Assert.False(entry.IsError);
        Assert.Contains("aborted", entry.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_ErrorAssistantWithContent_KeepsBody_MarksError()
    {
        var history = HistoryOf(
            """{"role":"assistant","content":[{"type":"text","text":"partial"}],"stopReason":"error","timestamp":1}""");
        var entry = Assert.Single(history);
        Assert.True(entry.IsError);
        Assert.Equal("partial", entry.Text);
    }

    [Fact]
    public void Restore_CleanAssistant_Unchanged()
    {
        var history = HistoryOf(
            """{"role":"assistant","content":[{"type":"text","text":"hi"}],"stopReason":"stop","timestamp":1}""");
        var entry = Assert.Single(history);
        Assert.False(entry.IsError);
        Assert.Equal("hi", entry.Text);
    }

    [Fact]
    public void Restore_ErrorAssistant_CountsInSessionMap()
    {
        var history = HistoryOf(
            """{"role":"assistant","content":[],"stopReason":"error","errorMessage":"x","timestamp":1}""");
        var feed = new ConversationFeed();
        feed.LoadHistory(history);
        Assert.Equal(1, feed.BuildMap().ErrorCount);
    }

    // ---- restored bash outcomes ------------------------------------------------

    [Fact]
    public void Restore_BashExitFailure_KeepsExitCode()
    {
        var history = HistoryOf(
            """{"role":"bashExecution","command":"exit 1","output":"","exitCode":1,"cancelled":false,"truncated":false,"timestamp":1}""");
        Assert.Contains("[exit 1]", Assert.Single(history).Text);
    }

    [Fact]
    public void Restore_BashCancelledAndTruncated_Marked()
    {
        var cancelled = HistoryOf(
            """{"role":"bashExecution","command":"sleep 9","output":"","exitCode":0,"cancelled":true,"truncated":false,"timestamp":1}""");
        Assert.Contains("[cancelled]", Assert.Single(cancelled).Text);

        var truncated = HistoryOf(
            """{"role":"bashExecution","command":"ls","output":"ok","exitCode":0,"cancelled":false,"truncated":true,"timestamp":1}""");
        Assert.Contains("[truncated]", Assert.Single(truncated).Text);
    }

    [Fact]
    public void Restore_BashSuccess_Unchanged()
    {
        var history = HistoryOf(
            """{"role":"bashExecution","command":"echo hi","output":"hi","exitCode":0,"cancelled":false,"truncated":false,"timestamp":1}""");
        Assert.Equal("Ran `echo hi`\nhi", Assert.Single(history).Text);
    }
}
