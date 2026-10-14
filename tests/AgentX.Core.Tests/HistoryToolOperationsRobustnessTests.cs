using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Lucy review regression suite for HistoryToolOperations (PR #29
/// correction): malformed envelopes and malformed bash/content rows must
/// degrade individually — never throw — while valid records keep exact-ID
/// pairing. Complements (does not replace) the original 18 behavior tests.
/// </summary>
public sealed class HistoryToolOperationsRobustnessTests
{
    // ---- malformed envelopes --------------------------------------------

    [Theory]
    [InlineData("[]")]
    [InlineData("[1,2]")]
    [InlineData("\"hello\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void Parse_NonObjectRoots_YieldEmpty_WithoutThrowing(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        Assert.Empty(HistoryToolOperations.Parse(doc));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":\"messages\"}")]
    [InlineData("{\"data\":{\"messages\":null}}")]
    [InlineData("{\"data\":{\"messages\":{}}}")]
    [InlineData("{\"data\":{\"messages\":\"nope\"}}")]
    [InlineData("{\"data\":{\"messages\":123}}")]
    public void Parse_EnvelopeShapeErrors_YieldEmpty_WithoutThrowing(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        Assert.Empty(HistoryToolOperations.Parse(doc));
    }

    [Fact]
    public void Parse_NullResponse_IsEmpty()
    {
        Assert.Empty(HistoryToolOperations.Parse(null));
    }

    // ---- malformed bashExecution fields ---------------------------------

    [Fact]
    public void Parse_BashWithNonStringCommand_DegradesLikeAbsentField()
    {
        using var doc = Response(
            Call("call_a", "bash") +
            ",{\"role\":\"bashExecution\",\"toolCallId\":\"call_a\",\"toolName\":\"bash\",\"command\":42,\"output\":\"ok\"}");

        var ops = HistoryToolOperations.Parse(doc);

        var op = Assert.Single(ops);
        Assert.True(op.HasCall && op.HasResult);
        Assert.Equal("call_a", op.CallId);
        // Command unreadable => treated exactly like the shared formatter's
        // absent-field fallback: "Ran ``" with the readable output line.
        Assert.Equal("Ran ``\nok", op.OutputText);
    }

    [Fact]
    public void Parse_BashWithNonStringOutput_DegradesToCommandOnly()
    {
        using var doc = Response(
            Call("call_b", "bash") +
            ",{\"role\":\"bashExecution\",\"toolCallId\":\"call_b\",\"toolName\":\"bash\",\"command\":\"ls\",\"output\":123}");

        var op = Assert.Single(HistoryToolOperations.Parse(doc));
        Assert.True(op.HasCall && op.HasResult);
        Assert.Equal("Ran `ls`", op.OutputText);
    }

    [Fact]
    public void Parse_BashWithBothFieldsMalformed_KeepsRowEmptyOutput()
    {
        using var doc = Response(
            Call("call_c", "bash") +
            ",{\"role\":\"bashExecution\",\"toolCallId\":\"call_c\",\"toolName\":\"bash\",\"command\":true,\"output\":{\"x\":1}}");

        var op = Assert.Single(HistoryToolOperations.Parse(doc));
        Assert.True(op.HasCall && op.HasResult);
        Assert.Equal("call_c", op.CallId);
        Assert.Equal("Ran ``", op.OutputText);
    }

    [Fact]
    public void Parse_ValidBashRow_StillMatchesSharedFormatterExactly()
    {
        using var doc = Response(
            Call("call_d", "bash") +
            ",{\"role\":\"bashExecution\",\"toolCallId\":\"call_d\",\"toolName\":\"bash\",\"command\":\"ls\",\"output\":\"ok\"}");

        var op = Assert.Single(HistoryToolOperations.Parse(doc));
        // Byte-identical to PiConversation.FormatBashExecution for valid input.
        Assert.Equal("Ran `ls`\nok", op.OutputText);
    }

    // ---- malformed content on toolResult --------------------------------

    [Fact]
    public void Parse_ToolResultWithMalformedContentBlock_KeepsRowWithEmptyBody()
    {
        using var doc = Response(
            "{\"role\":\"toolResult\",\"toolCallId\":\"call_e\",\"toolName\":\"read\",\"content\":[{\"type\":42,\"text\":5}]}");

        var op = Assert.Single(HistoryToolOperations.Parse(doc));
        // Row preserved as honest result-only evidence; only unreadable text
        // is lost (shared ExtractContentText contract untouched).
        Assert.True(op.HasResult);
        Assert.False(op.HasCall);
        Assert.Equal("call_e", op.CallId);
        Assert.Equal("read", op.ToolName);
        Assert.True(string.IsNullOrEmpty(op.OutputText));
    }

    // ---- valid neighbors survive malformed rows -------------------------

    [Fact]
    public void Parse_MalformedNeighbors_DoNotDisturbExactIdPairing()
    {
        using var doc = Response(
            // Malformed pair first (non-string command + junk message rows),
            Call("call_x", "bash") +
            ",{\"role\":\"bashExecution\",\"toolCallId\":\"call_x\",\"toolName\":\"bash\",\"command\":7,\"output\":null}" +
            // a non-object message row, a row with non-string content type,
            ",42" +
            ",{\"role\":\"toolResult\",\"toolCallId\":\"call_y\",\"toolName\":\"read\",\"content\":[{\"type\":\"text\",\"text\":null}]}" +
            // then its valid call — pairing and order must be unaffected.
            ",{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"call_y\",\"name\":\"read\",\"arguments\":{\"path\":\"x\"}}]}");

        var ops = HistoryToolOperations.Parse(doc);

        Assert.Equal(2, ops.Count);
        Assert.Equal("call_x", ops[0].CallId);
        Assert.True(ops[0].HasCall && ops[0].HasResult);
        Assert.Equal("Ran ``", ops[0].OutputText);
        Assert.Equal("call_y", ops[1].CallId);
        Assert.True(ops[1].HasCall && ops[1].HasResult);
        Assert.Equal("read", ops[1].ToolName);
        Assert.Contains("\"path\"", ops[1].ArgumentsJson, StringComparison.Ordinal);
    }

    // ---- builders (real get_messages response shape) --------------------

    private static string Call(string id, string name)
        => "{\"role\":\"assistant\",\"content\":[{\"type\":\"toolCall\",\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"arguments\":{}}]}";

    private static JsonDocument Response(string messages)
        => JsonDocument.Parse("{\"type\":\"response\",\"command\":\"get_messages\",\"success\":true,\"data\":{\"messages\":[" + messages + "]}}");
}
