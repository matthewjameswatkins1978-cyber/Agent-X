using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class PiRpcTests
{
    [Fact]
    public void Framing_EncodesOneLfTerminatedUtf8Record()
    {
        var framed = PiRpcFraming.Encode(new Dictionary<string, object?>
        {
            ["id"] = "req-1",
            ["type"] = "get_state",
        });

        Assert.Equal((byte)'\n', framed[^1]);
        Assert.DoesNotContain((byte)'\n', framed[..^1]);
        var text = Encoding.UTF8.GetString(framed);
        Assert.EndsWith("\n", text);
        using var record = JsonDocument.Parse(text);
        Assert.Equal("get_state", record.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Framing_PreservesUnicodeSeparatorsAndEmojiInsideStrings()
    {
        const string tricky = "line\u2028para\u2029emoji \U0001F680 end";
        var framed = PiRpcFraming.Encode(new Dictionary<string, object?>
        {
            ["id"] = "u1",
            ["type"] = "prompt",
            ["message"] = tricky,
        });

        // Exactly one LF terminator: embedded U+2028/U+2029 must not split records.
        var lfCount = framed.Count(b => b == (byte)'\n');
        Assert.Equal(1, lfCount);

        var text = Encoding.UTF8.GetString(framed);
        using var record = JsonDocument.Parse(PiRpcFraming.StripRecordTerminator(text));
        Assert.Equal(tricky, record.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void Framing_StripsOptionalCarriageReturn()
    {
        Assert.Equal("{}", PiRpcFraming.StripRecordTerminator("{}\r"));
        Assert.Equal("{}", PiRpcFraming.StripRecordTerminator("{}"));
    }

    [Fact]
    public async Task Correlation_MatchesResponseById_AcrossInterleavedEvents()
    {
        await using var connection = ConnectedLoopback(out var written);
        connection.Start();

        var commandTask = connection.SendCommandAsync("get_state");
        var sentId = await WaitForSentIdAsync(written);

        // An unrelated session event arrives first and must not resolve the command.
        connection.HandleProtocolLine("""{"type":"agent_start"}""");
        Assert.False(commandTask.IsCompleted);

        connection.HandleProtocolLine(
            "{\"id\":\"" + sentId + "\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{}}");
        using var response = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Acceptance_DispositionIsNotCompletion_WaitRequiresSettledEvent()
    {
        // Documents the protocol rule: a prompt response with disposition
        // "started" means acceptance, not completion. Completion is only
        // signaled by agent_settled.
        const string acceptance = """{"id":"p1","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";
        using var record = JsonDocument.Parse(acceptance);
        Assert.True(PiRpcFraming.IsResponseRecord(record, out var id, out var command));
        Assert.Equal("p1", id);
        Assert.Equal("prompt", command);
        Assert.Equal("started", record.RootElement.GetProperty("data").GetProperty("disposition").GetString());
        Assert.False(PiRpcFraming.IsSettledEvent(record, out _));

        using var settled = JsonDocument.Parse("""{"type":"agent_settled","aborted":false}""");
        Assert.True(PiRpcFraming.IsSettledEvent(settled, out var aborted));
        Assert.False(aborted);
    }

    [Fact]
    public async Task MalformedOutput_DoesNotBreakSubsequentResponses()
    {
        await using var connection = ConnectedLoopback(out var written);
        connection.Start();

        var commandTask = connection.SendCommandAsync("get_state");
        var sentId = await WaitForSentIdAsync(written);

        connection.HandleProtocolLine("this is not json {{{");
        connection.HandleProtocolLine("""{"type":"turn_start"}""");
        Assert.Equal(1, connection.MalformedLineCount);
        Assert.False(commandTask.IsCompleted);

        connection.HandleProtocolLine(
            "{\"id\":\"" + sentId + "\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{\"note\":\"recovered\"}}");
        using var response = await commandTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("recovered", response.RootElement.GetProperty("data").GetProperty("note").GetString());
    }

    [Fact]
    public async Task LargeMessage_StreamsThroughReaderWithoutTruncation()
    {
        var pipe = new Pipe();
        var output = new MemoryStream();
        await using var connection = new PiRpcConnection(pipe.Reader.AsStream(), output, leaveOpen: false);
        connection.Start();

        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.EventReceived += (_, args) =>
        {
            if (args.EventType == "message_update")
            {
                seen.TrySetResult(PiRpcFraming.GetTextDelta(args.Record) ?? string.Empty);
            }
        };

        var bigDelta = new string('x', 2_000_000) + "🚀 tail";
        var line = JsonSerializer.Serialize(new
        {
            type = "message_update",
            usage = new { input = 0, output = 1 },
            assistantMessageEvent = new { type = "text_delta", contentIndex = 0, delta = bigDelta },
        }) + "\n";
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line));
        await pipe.Writer.CompleteAsync();

        var received = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(bigDelta, received);
    }

    [Fact]
    public async Task ProcessFailure_FailsPendingCommands()
    {
        await using var connection = ConnectedLoopback(out _);
        connection.Start();

        var commandTask = connection.SendCommandAsync("get_state");
        await Task.Delay(100); // Let the send register.
        connection.FailPendingCommands(new PiRpcTransportException("Pi exited with code 1."));

        var error = await Assert.ThrowsAsync<PiRpcTransportException>(() => commandTask);
        Assert.Contains("exited", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_DoesNotLeavePendingCommands_AndAbortIsSent()
    {
        await using var connection = ConnectedLoopback(out var written);
        connection.Start();

        using var cts = new CancellationTokenSource();
        var settledTask = connection.WaitForSettledAsync(cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => settledTask);

        var abortTask = connection.SendCommandAsync("abort");
        var sentLine = await WaitForSentLineAsync(written);
        using (var sent = JsonDocument.Parse(sentLine))
        {
            Assert.Equal("abort", sent.RootElement.GetProperty("type").GetString());
            var abortId = sent.RootElement.GetProperty("id").GetString()!;
            connection.HandleProtocolLine(
                "{\"id\":\"" + abortId + "\",\"type\":\"response\",\"command\":\"abort\",\"success\":true}");
        }

        using var response = await abortTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, connection.PendingCommandCount);
    }

    [Fact]
    public void FailedCommand_SurfacesCommandAndError()
    {
        const string line = """{"id":"r3","type":"response","command":"set_model","success":false,"error":"Model not found: invalid/model"}""";
        using var record = JsonDocument.Parse(line);
        Assert.True(PiRpcFraming.IsResponseRecord(record, out var id, out var command));
        Assert.Equal("r3", id);
        Assert.Equal("set_model", command);
        Assert.False(record.RootElement.GetProperty("success").GetBoolean());
    }

    // --- Helpers: loopback connection with inspectable written commands. ---

    private static PiRpcConnection ConnectedLoopback(out MemoryStream written)
    {
        // Input stream is never read from in these tests; lines are injected
        // directly via HandleProtocolLine, which exercises the same dispatch
        // path as the background reader.
        written = new InspectableWriteStream();
        return new PiRpcConnection(Stream.Null, written, leaveOpen: true);
    }

    private static async Task<string> WaitForSentIdAsync(MemoryStream written)
    {
        var line = await WaitForSentLineAsync(written);
        using var record = JsonDocument.Parse(line);
        return record.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<string> WaitForSentLineAsync(MemoryStream written)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var text = Encoding.UTF8.GetString(written.ToArray());
            var newline = text.IndexOf('\n');
            if (newline >= 0)
            {
                return text[..newline];
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for a written RPC command.");
    }

    private sealed class InspectableWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (this)
            {
                base.Write(buffer, offset, count);
            }
        }

        public override void WriteByte(byte value)
        {
            lock (this)
            {
                base.WriteByte(value);
            }
        }

        public new byte[] ToArray()
        {
            lock (this)
            {
                return base.ToArray();
            }
        }
    }
}
