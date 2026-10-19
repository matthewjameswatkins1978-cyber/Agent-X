using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class PiSessionTests
{
    [Fact]
    public void SessionArgs_UseManagedDirAndStableId_WithoutNoSession()
    {
        var options = new PiSessionOptions(Path.Combine(Path.GetTempPath(), "AgentX Test", "x"), "agent-x-main");
        var args = options.ToArguments();
        Assert.Contains("--session-dir", args, StringComparison.Ordinal);
        Assert.Contains("--session-id agent-x-main", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--no-session", args, StringComparison.Ordinal);
        Assert.Contains('"', args); // Paths with spaces are quoted.
    }

    [Fact]
    public void SessionId_ValidationAcceptsPiRules()
    {
        PiSessionOptions.ValidateSessionId("agent-x-main");
        PiSessionOptions.ValidateSessionId("a1._-b2");
        Assert.Throws<ArgumentException>(() => PiSessionOptions.ValidateSessionId(""));
        Assert.Throws<ArgumentException>(() => PiSessionOptions.ValidateSessionId("-lead"));
        Assert.Throws<ArgumentException>(() => PiSessionOptions.ValidateSessionId("trail-"));
        Assert.Throws<ArgumentException>(() => PiSessionOptions.ValidateSessionId("has space"));
        Assert.Throws<ArgumentException>(() => PiSessionOptions.ValidateSessionId("has/slash"));
    }

    [Fact]
    public void PersistentOptions_KeepEphemeralDefaultUntouched()
    {
        var ephemeral = PiRpcOptions.Default();
        Assert.Contains("--no-session", ephemeral.Arguments, StringComparison.Ordinal);
        Assert.Contains("--no-extensions", ephemeral.Arguments, StringComparison.Ordinal);
        Assert.Contains("--no-skills", ephemeral.Arguments, StringComparison.Ordinal);
        Assert.Equal(3, ephemeral.Arguments.Split("--extension", StringSplitOptions.None).Length - 1);
        Assert.Null(ephemeral.WorkingDirectory);

        var root = Path.Combine(Path.GetTempPath(), $"agentx-{Guid.NewGuid():N}");
        try
        {
            var session = new PiSessionOptions(root, "agent-x-main");
            var persistent = PiRpcOptions.Persistent(session);
            Assert.DoesNotContain("--no-session", persistent.Arguments, StringComparison.Ordinal);
            Assert.Contains("--session-id agent-x-main", persistent.Arguments, StringComparison.Ordinal);
            Assert.Equal(session.WorkingDirectory, persistent.WorkingDirectory);
            Assert.True(Directory.Exists(session.SessionDirectory));
            Assert.True(Directory.Exists(session.WorkingDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseHistory_RestoresMixedRoles_AndSkipsSystemBoilerplate()
    {
        using var response = JsonDocument.Parse(SampleMessagesResponse());
        var history = PiConversation.ParseHistory(response);

        Assert.DoesNotContain(history, m => m.Role == "system" && m.Text.Contains("coding assistant"));
        var user = Assert.Single(history, m => m.Role == "user");
        Assert.Equal("Hello Pi", user.Text);
        var assistant = Assert.Single(history, m => m.Role == "assistant");
        Assert.Contains("Hello human", assistant.Text, StringComparison.Ordinal);
        Assert.Contains("[tool call: read]", assistant.Text, StringComparison.Ordinal);
        // Matthew's direction: verbose reasoning is first-class content like
        // Pi CLI shows it — quoted, not hidden (was DoesNotContain).
        Assert.Contains("> should stay hidden", assistant.Text, StringComparison.Ordinal);
        var tool = Assert.Single(history, m => m.Role == "tool");
        Assert.Contains("read", tool.Text, StringComparison.Ordinal);
        Assert.Contains(history, m => m.Role == "system" && m.Text.Contains("context summary"));
    }

    [Fact]
    public void ParseHistory_ToleratesUnknownRolesAndMalformedEntries()
    {
        const string payload = """{"data":{"messages":[{"role":"future-role","content":"keep me"},{"no-role":true},{"role":"user","content":[1,2,3]}]}}""";
        using var response = JsonDocument.Parse(payload);
        var history = PiConversation.ParseHistory(response);
        Assert.Equal(2, history.Count);
        Assert.Equal("future-role", history[0].Role);
        Assert.Equal(string.Empty, history[1].Text);
    }

    [Fact]
    public void ParseHistory_EmptyWhenNoMessages()
    {
        using var response = JsonDocument.Parse("""{"data":{}}""");
        Assert.Empty(PiConversation.ParseHistory(response));
    }

    [Fact]
    public void Receipt_DistinguishesIdleFromHealthy_AndHealthyFromExternallyVerified()
    {
        var healthy = new PiRunReceipt(Settled: true, Aborted: false, ErrorCount: 0, AssistantChars: 10, MessageCount: 2);
        Assert.True(healthy.RunHealthy);
        Assert.Contains("Healthy", healthy.Display, StringComparison.Ordinal);

        Assert.False(new PiRunReceipt(true, Aborted: true, 0, 0, 0).RunHealthy);
        var withErrors = new PiRunReceipt(true, false, 2, 5, 2);
        Assert.False(withErrors.RunHealthy);
        Assert.Contains("2 problems", withErrors.Display, StringComparison.Ordinal);
        Assert.False(new PiRunReceipt(false, false, 0, 0, 0).RunHealthy);

        // A healthy run is not external verification of task output: no
        // receipt wording may claim the task passed acceptance checks.
        foreach (var receipt in new[]
        {
            healthy,
            new PiRunReceipt(true, Aborted: true, 0, 0, 0),
            withErrors,
            new PiRunReceipt(false, false, 0, 0, 0),
        })
        {
            Assert.DoesNotContain("verif", receipt.Display, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Activity_DescribesToolsAndErrors_WithoutDumpingConversation()
    {
        Assert.Equal("Running tool: read…", ActivityOf("""{"type":"tool_execution_start","toolCallId":"c1","toolName":"read"}"""));
        Assert.Equal("Tool read finished.", ActivityOf("""{"type":"tool_execution_end","toolCallId":"c1","toolName":"read","isError":false}"""));
        Assert.Equal("Tool bash failed.", ActivityOf("""{"type":"tool_execution_end","toolCallId":"c2","toolName":"bash","isError":true}"""));
        Assert.Equal("Pi hit a transient error — retrying…", ActivityOf("""{"type":"auto_retry_start"}"""));
        Assert.Null(ActivityOf("""{"type":"agent_settled","aborted":false}"""));
        Assert.Null(ActivityOf("""{"type":"message_update","assistantMessageEvent":{"type":"text_delta","delta":"hi"}}"""));
    }

    [Fact]
    public void ErrorCounter_CountsFailures_NotAborts()
    {
        var errors = 0;
        CountOf("""{"type":"tool_execution_end","toolCallId":"c1","toolName":"read","isError":false}""", ref errors);
        CountOf("""{"type":"tool_execution_end","toolCallId":"c2","toolName":"bash","isError":true}""", ref errors);
        CountOf("""{"type":"extension_error","extensionPath":"x","event":"y","error":"z"}""", ref errors);
        CountOf("""{"type":"message_update","assistantMessageEvent":{"type":"error","reason":"overloaded"}}""", ref errors);
        CountOf("""{"type":"message_end","message":{"role":"assistant","stopReason":"error"}}""", ref errors);
        CountOf("""{"type":"auto_retry_end","success":false}""", ref errors);
        Assert.Equal(5, errors);

        // A user-aborted turn is reported through the receipt, not as an error.
        CountOf("""{"type":"message_end","message":{"role":"assistant","stopReason":"aborted"}}""", ref errors);
        Assert.Equal(5, errors);
    }

    [Fact]
    public async Task RestartRecovery_NewConnectionSeesPriorExchange()
    {
        // Simulates GUI close/reopen: a fresh connection restores the persisted
        // exchange via get_messages, parsed by the same code the GUI uses.
        // The input pipe stays open so no EOF faults the fresh connection.
        var pipe = new Pipe();
        var written = new MemoryStream();
        await using var reopened = new PiRpcConnection(pipe.Reader.AsStream(), written, leaveOpen: true);
        reopened.Start();
        var load = reopened.SendCommandAsync("get_messages");
        var sentId = await WaitForSentIdAsync(written);
        reopened.HandleProtocolLine(
            "{\"id\":\"" + sentId + "\",\"type\":\"response\",\"command\":\"get_messages\",\"success\":true,"
            + SampleMessagesPayload() + "}");
        using var response = await load.WaitAsync(TimeSpan.FromSeconds(5));
        var history = PiConversation.ParseHistory(response);
        Assert.Contains(history, m => m.Role == "user" && m.Text == "Hello Pi");
        Assert.Contains(history, m => m.Role == "assistant" && m.Text.Contains("Hello human"));
        await pipe.Writer.CompleteAsync();
    }

    [Fact]
    public void DisplayFormat_TruncatesLargeMessages_ButKeepsFullText()
    {
        var big = new string('y', 50_000);
        var history = new List<ConversationMessage> { new("assistant", big) };
        var display = PiConversation.FormatHistoryForDisplay(history, perMessageChars: 100);
        Assert.DoesNotContain(big, display);
        Assert.Contains("preserved in session", display, StringComparison.Ordinal);
        Assert.Equal(big, history[0].Text);
        Assert.Equal("No previous conversation in this session.", PiConversation.FormatHistoryForDisplay([]));
    }

    // --- Helpers ---

    private static string SampleMessagesResponse() => "{" + SampleMessagesPayload() + "}";

    private static string SampleMessagesPayload() =>
        "\"data\":{\"messages\":["
        + "{\"role\":\"system\",\"content\":\"You are a coding assistant\",\"timestamp\":1},"
        + "{\"role\":\"user\",\"content\":\"Hello Pi\",\"timestamp\":2},"
        + "{\"role\":\"assistant\",\"content\":["
        + "{\"type\":\"text\",\"text\":\"Hello human\"},"
        + "{\"type\":\"thinking\",\"thinking\":\"should stay hidden\"},"
        + "{\"type\":\"toolCall\",\"id\":\"c1\",\"name\":\"read\",\"arguments\":{}}"
        + "],\"stopReason\":\"toolUse\",\"timestamp\":3},"
        + "{\"role\":\"toolResult\",\"toolCallId\":\"c1\",\"toolName\":\"read\","
        + "\"content\":[{\"type\":\"text\",\"text\":\"file bytes\"}],\"isError\":false,\"timestamp\":4},"
        + "{\"role\":\"compactionSummary\",\"summary\":\"Older context summary\",\"tokensBefore\":10,\"timestamp\":5}"
        + "]}";

    private static string? ActivityOf(string line)
    {
        using var record = JsonDocument.Parse(line);
        return PiConversation.DescribeActivity(record);
    }

    private static void CountOf(string line, ref int errors)
    {
        using var record = JsonDocument.Parse(line);
        using var copy = JsonDocument.Parse(record.RootElement.GetRawText());
        PiConversation.CountRunError(new PiRpcEventArgs(copy, line), ref errors);
    }

    private static async Task<string> WaitForSentIdAsync(MemoryStream written)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            string text;
            lock (written)
            {
                text = Encoding.UTF8.GetString(written.ToArray());
            }

            var newline = text.IndexOf('\n');
            if (newline >= 0)
            {
                using var record = JsonDocument.Parse(text[..newline]);
                return record.RootElement.GetProperty("id").GetString()!;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for a written RPC command.");
    }
}
