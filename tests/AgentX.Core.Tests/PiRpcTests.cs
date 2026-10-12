using System.IO.Pipelines;
using System.Reflection;
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
        await using var loop = new Loopback();
        var connection = loop.Connection;
        var written = loop.Written;

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
        await using var loop = new Loopback();
        var connection = loop.Connection;
        var written = loop.Written;

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
        await using var loop = new Loopback();
        var connection = loop.Connection;

        var commandTask = connection.SendCommandAsync("get_state");
        await Task.Delay(100); // Let the send register.
        connection.FailPendingCommands(new PiRpcTransportException("Pi exited with code 1."));

        var error = await Assert.ThrowsAsync<PiRpcTransportException>(() => commandTask);
        Assert.Contains("exited", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_DoesNotLeavePendingCommands_AndAbortIsSent()
    {
        await using var loop = new Loopback();
        var connection = loop.Connection;
        var written = loop.Written;

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
    public async Task SettledWait_CancelAfterReturn_CancelsPromptly_AndReleasesSubscriptions()
    {
        // Regression: the cancellation registration must live until the wait
        // finishes. Cancelling after WaitForSettledAsync has returned must
        // still cancel the task within a strict timeout, and the orphaned
        // handler must be released instead of leaking.
        await using var loop = new Loopback();
        var connection = loop.Connection;
        Assert.Equal(0, SubscriberCount(connection));

        using var cts = new CancellationTokenSource();
        var wait = connection.WaitForSettledAsync(cts.Token);
        await Task.Delay(100); // Ensure the wait is outstanding after return.
        Assert.False(wait.IsCompleted);

        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, SubscriberCount(connection));

        // The connection still works: a later wait resolves on agent_settled.
        var second = connection.WaitForSettledAsync();
        connection.HandleProtocolLine("""{"type":"agent_settled","aborted":true}""");
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, SubscriberCount(connection));
    }

    [Fact]
    public async Task SettledWait_PreCanceledToken_ReturnsCanceled()
    {
        await using var loop = new Loopback();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<TaskCanceledException>(
            () => loop.Connection.WaitForSettledAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, SubscriberCount(loop.Connection));
    }

    [Fact]
    public async Task StdoutEof_FaultsPendingCommand_AndSettledWaiter_WithoutProcess()
    {
        // No process is involved anywhere here: completing the stdout pipe
        // must fault everyone waiting on records that will never arrive.
        var pipe = new Pipe();
        var written = new InspectableWriteStream();
        await using var connection = new PiRpcConnection(pipe.Reader.AsStream(), written, leaveOpen: true);
        connection.Start();

        var failures = new List<Exception>();
        connection.TransportFailed += (_, error) =>
        {
            lock (failures)
            {
                failures.Add(error);
            }
        };

        var command = connection.SendCommandAsync("get_state");
        var settled = connection.WaitForSettledAsync();
        await Task.Delay(100); // Both outstanding.
        Assert.False(command.IsCompleted);
        Assert.False(settled.IsCompleted);

        await pipe.Writer.CompleteAsync(); // Unexpected stdout EOF.

        var commandError = await Assert.ThrowsAsync<PiRpcTransportException>(
            () => command.WaitAsync(TimeSpan.FromSeconds(5)));
        var settledError = await Assert.ThrowsAsync<PiRpcTransportException>(
            () => settled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("closed", commandError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("closed", settledError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(failures);

        // After EOF the connection refuses new work instead of hanging it.
        await Assert.ThrowsAsync<PiRpcTransportException>(() => connection.SendCommandAsync("get_state"));
        await Assert.ThrowsAsync<PiRpcTransportException>(
            () => connection.WaitForSettledAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotKillReader_OrStrandWaiters()
    {
        // Lucy's blocker: a throwing EventReceived observer used to propagate
        // out of ReadLoopAsync, faulting the background reader without
        // FailTransport and stranding every outstanding request. The reader
        // must survive (or fail transport explicitly), remaining observers
        // must still be notified, and dispatched documents must be released.
        await using var loop = new Loopback();
        var connection = loop.Connection;

        connection.EventReceived += (_, _) => throw new InvalidOperationException("boom");
        var observed = new List<string>();
        var rawLines = new List<string>();
        connection.EventReceived += (_, args) =>
        {
            lock (observed)
            {
                observed.Add(args.EventType);
                // Read synchronously during dispatch, as the ownership
                // contract requires; the raw line survives disposal regardless.
                rawLines.Add(args.RawLine);
            }
        };
        var subscriberErrors = new List<Exception>();
        connection.SubscriberError += (_, error) =>
        {
            lock (subscriberErrors)
            {
                subscriberErrors.Add(error);
            }
        };

        // Outstanding command and settled waiter, resolved through the real
        // background reader while the throwing subscriber is attached.
        var command = connection.SendCommandAsync("get_state");
        var settled = connection.WaitForSettledAsync();
        var sentId = await WaitForNextSentIdAsync(loop.Written, linesSeen: 0);
        await loop.WriteStdoutLineAsync("""{"type":"agent_start"}""");
        await loop.WriteStdoutLineAsync(
            "{\"id\":\"" + sentId + "\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{}}");
        using var response = await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());

        await loop.WriteStdoutLineAsync("""{"type":"agent_settled","aborted":false}""");
        Assert.False(await settled.WaitAsync(TimeSpan.FromSeconds(5)));

        // The fault was reported, not hidden; the healthy observer still saw
        // every event byte-intact despite the thrower running first; the
        // reader stayed alive throughout. Dispatched documents are released
        // by the connection after dispatch (rented pool buffers), so the
        // test pins the synchronous view instead of retaining documents.
        Assert.Equal(2, connection.SubscriberErrorCount);
        Assert.Equal(2, subscriberErrors.Count);
        Assert.IsType<InvalidOperationException>(connection.LastSubscriberError);
        Assert.Contains("agent_start", Snapshot(observed));
        Assert.Contains("agent_settled", Snapshot(observed));
        Assert.Contains("""{"type":"agent_start"}""", Snapshot(rawLines));
        Assert.Contains("""{"type":"agent_settled","aborted":false}""", Snapshot(rawLines));

        // Reader still healthy: a further command round-trips.
        var linesSeen = SentLineCount(loop.Written);
        var second = connection.SendCommandAsync("get_state");
        var secondId = await WaitForNextSentIdAsync(loop.Written, linesSeen);
        await loop.WriteStdoutLineAsync(
            "{\"id\":\"" + secondId + "\",\"type\":\"response\",\"command\":\"get_state\",\"success\":true,\"data\":{}}");
        using var secondResponse = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(secondResponse.RootElement.GetProperty("success").GetBoolean());
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

    private sealed class Loopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();

        public InspectableWriteStream Written { get; } = new();

        public PiRpcConnection Connection { get; }

        public Loopback()
        {
            // The pipe stays open for the test lifetime: unlike Stream.Null it
            // does not signal EOF, so the reader loop stays alive and only
            // injected lines drive dispatch.
            Connection = new PiRpcConnection(_input.Reader.AsStream(), Written, leaveOpen: true);
            Connection.Start();
        }

        /// <summary>Feed one protocol line through the real background reader.</summary>
        public async Task WriteStdoutLineAsync(string line)
        {
            var framed = Encoding.UTF8.GetBytes(line + "\n");
            await _input.Writer.WriteAsync(framed);
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            Written.Dispose();
        }
    }

    private static int SentLineCount(MemoryStream written)
    {
        string text;
        lock (written)
        {
            text = Encoding.UTF8.GetString(written.ToArray());
        }

        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static async Task<string> WaitForNextSentIdAsync(MemoryStream written, int linesSeen)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            string text;
            lock (written)
            {
                text = Encoding.UTF8.GetString(written.ToArray());
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > linesSeen)
            {
                using var record = JsonDocument.Parse(lines[^1]);
                return record.RootElement.GetProperty("id").GetString()!;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Timed out waiting for a written RPC command.");
    }

    private static List<T> Snapshot<T>(List<T> source)
    {
        lock (source)
        {
            return new List<T>(source);
        }
    }

    private static int SubscriberCount(PiRpcConnection connection)
    {
        var count = 0;
        foreach (var name in new[] { "EventReceived", "TransportFailed" })
        {
            var field = typeof(PiRpcConnection).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            var handler = (Delegate?)field?.GetValue(connection);
            count += handler?.GetInvocationList().Length ?? 0;
        }

        return count;
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
