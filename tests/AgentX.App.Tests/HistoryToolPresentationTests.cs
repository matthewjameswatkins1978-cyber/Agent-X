using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>B7 feed-clarity contract: history-restored tool rows promote the
/// Core-embedded name/outcome prefix into the header and drop it from the
/// display body. Live rows and unknown shapes render exactly as before.</summary>
public sealed class HistoryToolPresentationTests
{
    [Fact]
    public void Result_PromotesNameAndStripsPrefix()
    {
        var parsed = ConversationViewItem.HistoryToolPresentation(null, "[tool result: powershell]\n23");
        Assert.NotNull(parsed);
        Assert.Equal("finished: powershell", parsed.Value.Note);
        Assert.False(parsed.Value.IsError);
        Assert.Equal("23", parsed.Value.DisplayBody);
    }

    [Fact]
    public void Failed_PromotesNameStateAndStripsPrefix()
    {
        var parsed = ConversationViewItem.HistoryToolPresentation(null, "[tool deploy failed]\nboom");
        Assert.NotNull(parsed);
        Assert.Equal("failed: deploy", parsed.Value.Note);
        Assert.True(parsed.Value.IsError);
        Assert.Equal("boom", parsed.Value.DisplayBody);
    }

    [Fact]
    public void HeaderOnly_YieldsEmptyDisplayBody()
    {
        var parsed = ConversationViewItem.HistoryToolPresentation(null, "[tool result: powershell]");
        Assert.NotNull(parsed);
        Assert.Equal("finished: powershell", parsed.Value.Note);
        Assert.Equal(string.Empty, parsed.Value.DisplayBody);
    }

    [Fact]
    public void Crlf_SplitsCleanly()
    {
        var parsed = ConversationViewItem.HistoryToolPresentation(null, "[tool result: ps]\r\nout");
        Assert.NotNull(parsed);
        Assert.Equal("finished: ps", parsed.Value.Note);
        Assert.Equal("out", parsed.Value.DisplayBody);
    }

    [Theory]
    [InlineData("started: powershell", "[tool result: powershell]\n23")] // live rows untouched
    [InlineData(null, "just some output")] // unknown shape untouched
    [InlineData(null, "[tool result: ]\nempty name")] // empty name refuses
    [InlineData(null, "[tool a] b] failed]\nweird")] // bracketed name refuses
    [InlineData(null, "")] // empty text refuses
    public void NonHistoryOrUnknown_ReturnsNull(string? note, string text)
    {
        Assert.Null(ConversationViewItem.HistoryToolPresentation(note, text));
    }

    [Theory]
    [InlineData("finished: powershell", "[tool result: powershell]\n674", "finished: powershell", false, "674")]
    [InlineData("failed: deploy", "[tool deploy failed]\nboom", "failed: deploy", true, "boom")]
    public void LiveFinishedOrFailed_StripsSamePrefix(string? note, string text, string expectedNote, bool expectedError, string expectedBody)
    {
        var parsed = ConversationViewItem.HistoryToolPresentation(note, text);
        Assert.NotNull(parsed);
        Assert.Equal(expectedNote, parsed.Value.Note);
        Assert.Equal(expectedError, parsed.Value.IsError);
        Assert.Equal(expectedBody, parsed.Value.DisplayBody);
    }
}
