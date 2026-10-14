using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// #27 Core half: the get_session_stats poll path — send over the wire,
/// typed decode, bridge binding refresh, and honest failure semantics.
/// Deterministic and headless: a pipe loopback stands in for Pi stdout
/// (same pattern as PiTapHardeningTests/PiSessionTests); no live process,
/// no GUI. The settle-time auto-refresh in PiConversation.SendAsync is
/// exercised end-to-end by the existing live worker suites (real Pi).
/// </summary>
public sealed class SessionStatsPollTests
{
    private const string StatsData =
        "\"sessionId\":\"s1\",\"toolCalls\":7,\"tokens\":{\"total\":1234},\"cost\":0.02,\"contextUsage\":{\"percent\":3.5}";

    // ---- wire path ------------------------------------------------------

    [Fact]
    public async Task Poll_Success_ReturnsRawResponseForTheApprovedBoundary()
    {
        await using var loop = new Loopback();

        var pending = PiRpcClient.SendStatsPollAsync(loop.Connection, CancellationToken.None);
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"get_session_stats\",\"success\":true,\"data\":{" + StatsData + "}}");

        using var arrived = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(7, arrived.RootElement.GetProperty("data").GetProperty("toolCalls").GetInt32());
        // The document survives for the caller: the bridge records it and
        // the typed decode reads it AFTER the send returns (caller owns
        // disposal — mirrors GetStateAsync's contract shape).
        Assert.Equal("s1", arrived.RootElement.GetProperty("data").GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Poll_CommandError_ThrowsTypedWithCommandAndError()
    {
        await using var loop = new Loopback();

        var pending = PiRpcClient.SendStatsPollAsync(loop.Connection, CancellationToken.None);
        var id = await loop.WaitForSentIdAsync();
        await loop.WriteStdoutLineAsync(
            "{\"type\":\"response\",\"id\":\"" + id + "\",\"command\":\"get_session_stats\",\"success\":false,\"error\":\"unknown command\"}");

        var thrown = await Assert.ThrowsAsync<PiRpcCommandException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("get_session_stats", thrown.Command);
        Assert.Contains("unknown command", thrown.Error, StringComparison.Ordinal);
        // Contract note: SendStatsPollAsync disposes the failed document
        // before throwing; the typed exception is what the caller observes.
    }

    // ---- record boundary + typed decode ---------------------------------

    [Fact]
    public void RecordStatsResponse_WithAttachedBridge_UpdatesLatestStats_WithoutRows()
    {
        var feed = new ConversationFeed();
        using var bridge = new FeedBridge(feed);
        bridge.Attach(new PiRpcClient()); // decode path needs an attached tap; no live connection required.
        Assert.Null(bridge.LatestStats);
        var rowsBefore = feed.Count;

        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"get_session_stats\",\"success\":true,\"data\":{" + StatsData + "}}");

        var snapshot = PiConversation.RecordStatsResponse(bridge, response);

        // Typed return and the status-strip binding carry the SAME observed values.
        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.ToolCalls);
        Assert.Equal(1234, snapshot.TotalTokens);
        Assert.Equal(0.02, snapshot.Cost, 3);
        Assert.Equal(3.5, snapshot.ContextPercent, 3);
        Assert.NotNull(bridge.LatestStats);
        Assert.Equal(snapshot.ToolCalls, bridge.LatestStats.ToolCalls);
        Assert.Equal(snapshot.SessionId, bridge.LatestStats.SessionId);
        // A poll is not conversation: no rows are fabricated for it.
        Assert.Equal(rowsBefore, feed.Count);
    }

    [Fact]
    public void RecordStatsResponse_WithoutBridge_StillReturnsTypedSnapshot()
    {
        // Legacy workspace path (no bridge attached): the caller still gets
        // the honest snapshot; only the status binding is absent.
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"get_session_stats\",\"success\":true,\"data\":{" + StatsData + "}}");

        var snapshot = PiConversation.RecordStatsResponse(null, response);

        Assert.NotNull(snapshot);
        Assert.Equal(7, snapshot.ToolCalls);
    }

    [Fact]
    public void RecordStatsResponse_WrongShape_ReturnsNull_NeverHollowZero()
    {
        // A successful response that is NOT a stats shape (future Pi change
        // or misrouted document) decodes to some other typed event: the
        // contract returns null rather than inventing zeroed totals.
        using var response = JsonDocument.Parse(
            "{\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}");

        Assert.Null(PiConversation.RecordStatsResponse(null, response));
    }

    [Fact]
    public void RecordStatsResponse_NullResponse_ThrowsLoudly()
    {
        Assert.Throws<ArgumentNullException>(() => PiConversation.RecordStatsResponse(null, null!));
        using var bridge = new FeedBridge(new ConversationFeed());
        Assert.Throws<ArgumentNullException>(() => PiConversation.RecordStatsResponse(bridge, null!));
    }

    // ---- loopback helper (PiTapHardeningTests pattern) ------------------

    private sealed class Loopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly MemoryStream _written = new();

        public PiRpcConnection Connection { get; }

        public Loopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), _written, leaveOpen: true);
            Connection.Start();
        }

        public async Task WriteStdoutLineAsync(string line)
        {
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public async Task<string> WaitForSentIdAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                string text;
                lock (_written)
                {
                    text = Encoding.UTF8.GetString(_written.ToArray());
                }

                var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (line is not null)
                {
                    using var record = JsonDocument.Parse(line);
                    return record.RootElement.GetProperty("id").GetString()!;
                }

                await Task.Delay(10);
            }

            throw new TimeoutException("No command was written.");
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            _written.Dispose();
        }
    }
}
