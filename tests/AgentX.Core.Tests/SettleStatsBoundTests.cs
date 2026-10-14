using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Lucy review regression for PR #30: a settled, responsive run followed by
/// an intentionally SILENT stats responder must return
/// <see cref="PiConversation.SendAsync"/> within the bounded timeout —
/// cancelling the underlying pending RPC and leaving nothing pending —
/// without changing prompt success, receipt, or the explicit public
/// GetSessionStatsAsync contract. Fully deterministic: a scripted loopback
/// plays Pi (prompt/state/messages answer; get_session_stats does not).
/// </summary>
public sealed class SettleStatsBoundTests
{
    private const string StatsData =
        "\"sessionId\":\"s1\",\"toolCalls\":7,\"tokens\":{\"total\":1234},\"cost\":0.02,\"contextUsage\":{\"percent\":3.5}";

    [Fact]
    public async Task SettledRun_WithSilentStatsResponder_ReturnsWithinBound_CancelsAndCleansUp()
    {
        await using var loop = new ScriptedLoopback(); // Pi script: everything answers, stats stays silent.
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);
        await using var conversation = new PiConversation(() => client);

        var stopwatch = Stopwatch.StartNew();
        var run = conversation.SendAsync("hello", null, CancellationToken.None);
        // Hang guard: an unbounded poll fails this test with TimeoutException.
        var (text, receipt) = await run.WaitAsync(TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        // The prompt itself completed normally: settle semantics unchanged.
        Assert.Equal("Hello", text);
        Assert.True(receipt.Settled);
        Assert.False(receipt.Aborted);
        Assert.Equal(0, receipt.ErrorCount);
        Assert.Equal(5, receipt.AssistantChars);

        // The stats poll actually went out and Pi was silent about it.
        Assert.True(loop.StatsPollCount >= 1, "The post-settle stats poll was never sent.");
        Assert.NotEmpty(loop.StatsIds);

        // Bounded: governed by the 2 s timeout (waited it out), never a hang.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(1),
            $"Settled run returned in {stopwatch.Elapsed} — it cannot have waited out the bounded silent poll.");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"Settled run took {stopwatch.Elapsed}: the silent stats responder must not delay it past the bound.");

        // Cancel + cleanup: the timed-out stats request removed its pending
        // entry (PiRpcConnection's OCE path) — no orphan waits remain.
        Assert.Equal(0, loop.Connection.PendingCommandCount);
        Assert.Null(loop.ResponderError);

        // The cancelled request's late response must not corrupt correlation:
        // the reader ignores an unknown id, and a fresh explicit poll still
        // round-trips with typed data on the same connection.
        loop.StatsSilent = false;
        foreach (var staleId in loop.StatsIds)
        {
            await loop.WriteAsync(
                "{\"id\":\"" + staleId + "\",\"type\":\"response\",\"command\":\"get_session_stats\",\"success\":true,\"data\":{" + StatsData + "}}");
        }

        using var fresh = await client.GetSessionStatsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, fresh.RootElement.GetProperty("data").GetProperty("toolCalls").GetInt32());
        Assert.Equal(0, loop.Connection.MalformedLineCount);
        Assert.Null(loop.ResponderError);
    }

    [Fact]
    public async Task ExplicitPoll_SilentResponder_CancellationThrowsPromptly_AndCleansUp()
    {
        // The public GetSessionStatsAsync stays independently cancellable and
        // typed: an external token bounds it directly, with the same pending
        // cleanup, no swallowed OCE and no hollow value.
        await using var loop = new ScriptedLoopback(); // stats permanently silent
        var client = new PiRpcClient();
        client.AttachConnection(loop.Connection);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetSessionStatsAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Cancellation took {stopwatch.Elapsed}: the token must reach the pending RPC.");
        Assert.True(loop.StatsPollCount >= 1, "The stats poll was never sent.");
        Assert.Equal(0, loop.Connection.PendingCommandCount);
        Assert.Null(loop.ResponderError);
    }

    /// <summary>
    /// Scripted Pi: commands are captured from the client's write stream and
    /// answered by a background responder — prompt (settled + one text
    /// delta), get_state, get_messages — while get_session_stats is answered
    /// only while <see cref="StatsSilent"/> is false.
    /// </summary>
    private sealed class ScriptedLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly CapturingWriteStream _written = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _statsIds = new();
        private readonly Task _responder;
        private int _statsPolls;

        public ScriptedLoopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), _written, leaveOpen: true);
            Connection.Start();
            _responder = Task.Run(RespondAsync);
        }

        public PiRpcConnection Connection { get; }

        /// <summary>When true (default), get_session_stats gets no response.</summary>
        public volatile bool StatsSilent = true;

        public Exception? ResponderError;

        public int StatsPollCount => Volatile.Read(ref _statsPolls);

        public IReadOnlyList<string> StatsIds
        {
            get
            {
                lock (_statsIds)
                {
                    return _statsIds.ToArray();
                }
            }
        }

        public async Task WriteAsync(string line)
            => await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));

        private async Task RespondAsync()
        {
            var consumed = 0;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (var line in _written.TakeCompleteLines(ref consumed))
                    {
                        using var command = JsonDocument.Parse(line);
                        var id = command.RootElement.GetProperty("id").GetString()!;
                        var type = command.RootElement.GetProperty("type").GetString();
                        switch (type)
                        {
                            case "prompt":
                                await WriteAsync(Response(id, "prompt", "\"disposition\":\"started\""));
                                await WriteAsync(
                                    "{\"type\":\"message_update\",\"assistantMessageEvent\":{\"type\":\"text_delta\",\"delta\":\"Hello\"}}");
                                await WriteAsync("{\"type\":\"agent_settled\",\"aborted\":false}");
                                break;
                            case "get_state":
                                await WriteAsync(Response(id, "get_state", "\"messageCount\":2,\"isStreaming\":false"));
                                break;
                            case "get_messages":
                                await WriteAsync(Response(id, "get_messages", "\"messages\":[]"));
                                break;
                            case "get_session_stats":
                                Interlocked.Increment(ref _statsPolls);
                                lock (_statsIds)
                                {
                                    _statsIds.Add(id);
                                }

                                if (!StatsSilent)
                                {
                                    await WriteAsync(Response(id, "get_session_stats", StatsData));
                                }

                                break;
                            default:
                                throw new InvalidOperationException($"Scripted Pi received an unexpected command: {type}");
                        }
                    }

                    await Task.Delay(5, _stop.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                ResponderError = ex;
            }
        }

        private static string Response(string id, string command, string dataProperties)
            => "{\"id\":\"" + id + "\",\"type\":\"response\",\"command\":\"" + command
                + "\",\"success\":true,\"data\":{" + dataProperties + "}}";

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _responder;
            }
            catch
            {
                // Captured in ResponderError and asserted by the tests.
            }

            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            _stop.Dispose();
            _written.Dispose();
        }
    }

    /// <summary>Thread-safe capture of everything the client writes toward Pi.</summary>
    private sealed class CapturingWriteStream : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_buffer)
            {
                _buffer.Write(buffer, offset, count);
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_buffer)
            {
                _buffer.Write(buffer.Span);
            }

            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        /// <summary>Yield only complete LF-terminated lines; advances the
        /// caller's consumed offset past them, leaving partial writes pending.</summary>
        public List<string> TakeCompleteLines(ref int consumed)
        {
            lock (_buffer)
            {
                if (_buffer.Length == 0)
                {
                    return new List<string>();
                }

                var text = Encoding.UTF8.GetString(_buffer.ToArray());
                var lines = new List<string>();
                var start = consumed;
                for (var i = consumed; i < text.Length; i++)
                {
                    if (text[i] != '\n')
                    {
                        continue;
                    }

                    if (i > start)
                    {
                        lines.Add(text[start..i]);
                    }

                    start = i + 1;
                    consumed = i + 1;
                }

                return lines;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
