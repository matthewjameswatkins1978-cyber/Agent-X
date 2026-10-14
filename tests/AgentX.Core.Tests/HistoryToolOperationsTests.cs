using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// #25 Core half: HistoryToolOperations correlates persisted toolCall ↔
/// toolResult by TRUE call ID (audited on a real 956-line Pi session:
/// 477/477 call blocks and 477/477 results carry matching IDs of the shape
/// call_&lt;uuid&gt;|fc_&lt;uuid&gt;; arguments persist as objects on the
/// call side; assistant messages carry 1–4 call blocks). Fixtures mirror
/// that observed shape. Deterministic and headless. Guarantees under test:
/// never pair without unique identity, never override contradictory names,
/// never drop either side, first-appearance order preserved, redaction
/// applied, malformed input tolerated one entry at a time.
/// </summary>
public sealed class HistoryToolOperationsTests
{
    // Observed real-session ID shape (…\sessions\--D--Projects-agent-x--\2026-10-13T23-19-46…).
    private const string IdA = "call_01a13791e18c7009bbeaacb47e6b4fe6|fc_01a13791e18c7009bbeaacb47e6b4fe6";
    private const string IdB = "call_01a13791ec3f758da25828f83427be11|fc_01a13791ec3f758da25828f83427be11";

    private const string UserGo = "{\"role\":\"user\",\"content\":\"Go\",\"timestamp\":1}";

    private static string AssistantWithCalls(params string[] callBlocks)
        => "{\"role\":\"assistant\",\"content\":["
            + "{\"type\":\"text\",\"text\":\"Working on it.\"},"
            + "{\"type\":\"thinking\",\"thinking\":\"secret rumination\"},"
            + string.Join(",", callBlocks)
            + "],\"stopReason\":\"toolUse\",\"timestamp\":3}";

    private static string CallBlock(string id, string name, string argsJson)
        => "{\"type\":\"toolCall\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"arguments\":" + argsJson + "}";

    private static string CallBlockNoId(string name)
        => "{\"type\":\"toolCall\",\"name\":\"" + name + "\",\"arguments\":{}}";

    private static string ToolResult(string callId, string toolName, string text, bool isError = false, long? durationMs = null)
        => "{\"role\":\"toolResult\",\"toolCallId\":\"" + callId + "\",\"toolName\":\"" + toolName + "\","
            + "\"content\":[{\"type\":\"text\",\"text\":\"" + text + "\"}],\"isError\":" + (isError ? "true" : "false")
            + (durationMs is null ? string.Empty : ",\"durationMs\":" + durationMs.Value)
            + ",\"timestamp\":4}";

    private static IReadOnlyList<HistoryToolOperation> Parse(params string[] messages)
    {
        using var doc = JsonDocument.Parse("{\"data\":{\"messages\":[" + string.Join(",", messages) + "]}}");
        return HistoryToolOperations.Parse(doc);
    }

    // ---- pairing by true call ID ----------------------------------------

    [Fact]
    public void CallAndResult_PairById_WithFullGenuineEvidence()
    {
        var ops = Parse(
            UserGo,
            AssistantWithCalls(CallBlock(IdA, "powershell", "{\"command\":\"git status --short\"}")),
            ToolResult(IdA, "powershell", "M src/App.cs", durationMs: 380));

        var op = Assert.Single(ops);
        Assert.True(op.HasCall);
        Assert.True(op.HasResult);
        Assert.Equal(IdA, op.CallId);
        Assert.Equal("powershell", op.ToolName);
        Assert.Contains("git status --short", op.ArgumentsJson, StringComparison.Ordinal);
        Assert.False(op.IsError);
        Assert.Equal(380, op.DurationMs);
        Assert.Contains("M src/App.cs", op.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleCallsInOneAssistantMessage_PairIndependently()
    {
        // Real sessions contain 1–4 toolCall blocks per assistant message;
        // each pairs by its OWN id, never by position.
        var ops = Parse(
            AssistantWithCalls(
                CallBlock(IdA, "read", "{\"file_path\":\"a.cs\"}"),
                CallBlock(IdB, "read", "{\"file_path\":\"b.cs\"}")),
            ToolResult(IdB, "read", "BODY-B"),
            ToolResult(IdA, "read", "BODY-A"));

        Assert.Equal(2, ops.Count);
        Assert.Equal(IdA, ops[0].CallId); // First-appearance order follows the call side.
        Assert.Contains("BODY-A", ops[0].OutputText, StringComparison.Ordinal);
        Assert.Contains("a.cs", ops[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.Equal(IdB, ops[1].CallId);
        Assert.Contains("BODY-B", ops[1].OutputText, StringComparison.Ordinal);
        Assert.Contains("b.cs", ops[1].ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultArrivingBeforeItsCall_StillPairs_FirstAppearanceWins()
    {
        // Order in the file: result first, call later. Pairing is by identity,
        // so the unit forms and sits at the result's first appearance.
        var ops = Parse(
            ToolResult(IdB, "write", "LATE-CALL"),
            AssistantWithCalls(CallBlock(IdB, "write", "{\"path\":\"x\"}")));

        var op = Assert.Single(ops);
        Assert.True(op.HasCall);
        Assert.True(op.HasResult);
        Assert.Contains("LATE-CALL", op.OutputText, StringComparison.Ordinal);
    }

    // ---- honest absence -------------------------------------------------

    [Fact]
    public void CallWithoutResult_KeepsCallSideAlone()
    {
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "powershell", "{\"command\":\"dotnet build\"}")));

        var op = Assert.Single(ops);
        Assert.True(op.HasCall);
        Assert.False(op.HasResult);
        Assert.Null(op.IsError);
        Assert.Null(op.DurationMs);
        Assert.Null(op.OutputText);
        Assert.Contains("dotnet build", op.ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultWithoutCall_KeepsResultSideAlone()
    {
        var ops = Parse(ToolResult(IdA, "read", "ORPHAN"));

        var op = Assert.Single(ops);
        Assert.False(op.HasCall);
        Assert.True(op.HasResult);
        Assert.Null(op.ArgumentsJson); // Never invented: no call side means no arguments.
        Assert.Contains("ORPHAN", op.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOrAbsentMessages_YieldEmptyList()
    {
        using var noData = JsonDocument.Parse("{\"data\":{}}");
        Assert.Empty(HistoryToolOperations.Parse(noData));
        Assert.Empty(HistoryToolOperations.Parse(null));
        Assert.Empty(Parse()); // messages array present but empty
    }

    // ---- refusal to guess -----------------------------------------------

    [Fact]
    public void DuplicateIdOnCallSide_NeverPairs()
    {
        // Same discipline as ConversationFeed.ReconcileToolResults: an ID
        // appearing twice on EITHER side makes identity unprovable.
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "read", "{}"), CallBlock(IdA, "read", "{}")),
            ToolResult(IdA, "read", "RESULT"));

        Assert.Equal(3, ops.Count); // Two call-only units + one result-only: nothing merged.
        Assert.Equal(2, ops.Count(o => o.HasCall && !o.HasResult));
        Assert.Equal(1, ops.Count(o => !o.HasCall && o.HasResult));
    }

    [Fact]
    public void DuplicateIdOnResultSide_NeverPairs()
    {
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "read", "{}")),
            ToolResult(IdA, "read", "FIRST"),
            ToolResult(IdA, "read", "SECOND"));

        Assert.Equal(3, ops.Count);
        Assert.Equal(1, ops.Count(o => o.HasCall && !o.HasResult));
        Assert.Equal(2, ops.Count(o => !o.HasCall && o.HasResult));
    }

    [Fact]
    public void MissingId_NeverPairs_EvenWithSingleUniqueNames()
    {
        var ops = Parse(
            "{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"name\":\"read\",\"arguments\":{}}]}",
            "{\"role\":\"toolResult\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":\"OUT\"}],\"isError\":false}");

        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Null(o.CallId));
        Assert.Equal(1, ops.Count(o => o.HasCall));
        Assert.Equal(1, ops.Count(o => o.HasResult));
    }

    [Fact]
    public void ContradictoryNames_WithMatchingId_NeverPair()
    {
        // Identity matched but the sides name different tools: contradictory
        // evidence is never resolved by guessing (mirrors ToolPairAgrees).
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "read", "{}")),
            ToolResult(IdA, "write", "OUT"));

        Assert.Equal(2, ops.Count);
        Assert.Equal(1, ops.Count(o => o.HasCall && !o.HasResult));
        Assert.Equal(1, ops.Count(o => !o.HasCall && o.HasResult));
    }

    [Fact]
    public void OversizedId_IsTreatedAsAbsent()
    {
        var huge = "call_" + new string('x', 200); // >160 chars: same normalization as HistoryCallId.
        var ops = Parse(
            "{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"" + huge + "\",\"name\":\"read\",\"arguments\":{}}]}",
            ToolResult(huge, "read", "OUT"));

        Assert.Equal(2, ops.Count);
        Assert.All(ops, o => Assert.Null(o.CallId));
    }

    // ---- fidelity -------------------------------------------------------

    [Fact]
    public void ErrorResult_ReportsFailureWithTiming()
    {
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "powershell", "{}")),
            ToolResult(IdA, "powershell", "command not found", isError: true, durationMs: 42));

        var op = Assert.Single(ops);
        Assert.True(op.IsError);
        Assert.Equal(42, op.DurationMs);
        Assert.Contains("command not found", op.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsObjectAndString_BothRetained_Redacted()
    {
        var ops = Parse(
            AssistantWithCalls(
                CallBlock(IdA, "powershell", "{\"command\":\"curl -H 'authorization: Bearer abc123token' x\"}"),
                CallBlock(IdB, "bash", "\"echo plain\"")),
            ToolResult(IdA, "powershell", "ok"),
            ToolResult(IdB, "bash", "ok"));

        Assert.Equal(2, ops.Count);
        // Object form serialized + redacted (bearer credential caught by PiRedaction).
        Assert.DoesNotContain("Bearer abc123token", ops[0].ArgumentsJson, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ops[0].ArgumentsJson, StringComparison.Ordinal);
        // String form kept as the genuine persisted string.
        Assert.Contains("echo plain", ops[1].ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ThinkingBlocks_AreNotCalls_AndNotLeaked()
    {
        var ops = Parse(
            "{\"role\":\"assistant\",\"content\":["
                + "{\"type\":\"thinking\",\"thinking\":\"private\"},"
                + CallBlock(IdA, "read", "{}") + "]}",
            ToolResult(IdA, "read", "OUT"));

        var op = Assert.Single(ops);
        Assert.True(op.HasResult);
        Assert.DoesNotContain("private", op.OutputText ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("private", op.ArgumentsJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Order_IsFirstAppearance_MixedSides()
    {
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "read", "{}")),        // unit A first appearance: position 0
            ToolResult(IdA, "read", "OUT-A"),                         // pairs into A
            ToolResult(IdB, "write", "ORPHAN-RESULT"),                // unit B first appearance: position 2
            "{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"" + IdB
                + "\",\"name\":\"write\",\"arguments\":{}}]}");       // pairs into B (first seen at 2)

        Assert.Equal(2, ops.Count);
        Assert.Equal(IdA, ops[0].CallId);
        Assert.True(ops[0].HasResult);
        Assert.Equal(IdB, ops[1].CallId);
        Assert.True(ops[1].HasCall);
        Assert.True(ops[1].HasResult);
        Assert.Contains("ORPHAN-RESULT", ops[1].OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void NameCaseDifference_Pairs()
    {
        // Observed toolName on both sides derives from the same tool
        // definition; case-only differences agree, not contradict.
        var ops = Parse(
            AssistantWithCalls(CallBlock(IdA, "PowerShell", "{}")),
            ToolResult(IdA, "powershell", "OUT"));

        var op = Assert.Single(ops);
        Assert.True(op.HasResult);
    }

    // ---- tolerance ------------------------------------------------------

    [Fact]
    public void MalformedEntries_AreTolerated_OneEntryAtATime_NeverThrown()
    {
        var ops = Parse(
            "{\"role\":\"assistant\",\"content\":[1,2,3]}",          // bad block array → no calls from it
            "{\"role\":\"assistant\",\"content\":\"just text\"}",     // string content (no blocks)
            "{\"no-role\":true}",                                     // missing role → skipped
            "{\"role\":\"toolResult\",\"content\":\"not-an-array\"}", // tolerated like ParseHistory: a result with no id/name
            "{\"role\":\"futureRole\",\"content\":[]}",               // unknown role → no tool units
            AssistantWithCalls(CallBlock(IdA, "read", "{}")),         // the one paired unit
            ToolResult(IdA, "read", "OUT"));

        Assert.Equal(2, ops.Count);
        var paired = Assert.Single(ops, o => o.HasCall && o.HasResult);
        Assert.Contains("OUT", paired.OutputText, StringComparison.Ordinal);

        // The odd result is PRESERVED as its own honest unit (same tolerance
        // as ParseHistory keeping the row) with its identity fields honestly
        // absent — never dropped, never guessed.
        var odd = Assert.Single(ops, o => !o.HasCall);
        Assert.True(odd.HasResult);
        Assert.Null(odd.CallId);
        Assert.Null(odd.ToolName);
        Assert.Contains("not-an-array", odd.OutputText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoToolCallsAtAll_YieldsEmpty_NotPhantomUnits()
    {
        var ops = Parse(
            "{\"role\":\"user\",\"content\":\"Hi\"}",
            "{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"}]}");

        Assert.Empty(ops);
    }
}
