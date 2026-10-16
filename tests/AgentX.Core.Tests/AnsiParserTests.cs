using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>ANSI display parsing: colours, resets, malformed/truncated
/// sequences, non-SGR swallowing, and source preservation for copy.</summary>
public sealed class AnsiParserTests
{
    [Fact]
    public void Parse_PlainText_YieldsSingleUnstyledSpan()
    {
        var spans = AnsiParser.Parse("hello world");
        var only = Assert.Single(spans);
        Assert.Equal("hello world", only.Text);
        Assert.False(only.Bold);
        Assert.Null(only.Foreground);
    }

    [Fact]
    public void Parse_BasicColourAndReset_SplitsRuns()
    {
        var spans = AnsiParser.Parse("\x1b[32;1mgreen-bold\x1b[0mplain");
        Assert.Equal(2, spans.Count);
        Assert.Equal("green-bold", spans[0].Text);
        Assert.True(spans[0].Bold);
        Assert.Equal(new RgbColor(0, 205, 0), spans[0].Foreground);
        Assert.Equal("plain", spans[1].Text);
        Assert.False(spans[1].Bold);
        Assert.Null(spans[1].Foreground);
    }

    [Fact]
    public void Parse_BareReset_ResetsStyle()
    {
        var spans = AnsiParser.Parse("\x1b[31mred\x1b[mplain");
        Assert.Equal(new RgbColor(205, 0, 0), spans[0].Foreground);
        Assert.Null(spans[1].Foreground);
    }

    [Fact]
    public void Parse_BrightAndExtendedColours_MapCorrectly()
    {
        var spans = AnsiParser.Parse("\x1b[91mbright\x1b[38;5;27midx\x1b[38;2;10;20;30mrgb\x1b[39mx");
        Assert.Equal(new RgbColor(255, 0, 0), spans[0].Foreground);
        Assert.Equal(AnsiParser.Xterm256(27), spans[1].Foreground);
        Assert.Equal(new RgbColor(10, 20, 30), spans[2].Foreground);
        Assert.Null(spans[3].Foreground);
    }

    [Fact]
    public void Parse_Background_PresentedSeparately()
    {
        var spans = AnsiParser.Parse("\x1b[44mbg\x1b[49mfg");
        Assert.Equal(new RgbColor(0, 0, 238), spans[0].Background);
        Assert.Null(spans[1].Background);
    }

    [Fact]
    public void Parse_EraseAndCursorSequences_Swallowed()
    {
        var spans = AnsiParser.Parse("a\x1b[2Kb\x1b[1Ac\x1b[?25ld");
        Assert.Equal("abcd", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Parse_OscTitle_Swallowed()
    {
        var spans = AnsiParser.Parse("\x1b]0;window title\x07visible");
        Assert.Equal("visible", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Parse_MalformedAndTruncated_DroppedTextKept()
    {
        // Unknown SGR code: ignored, text kept unstyled.
        var unknown = AnsiParser.Parse("a\x1b[999mb");
        Assert.Equal("ab", string.Concat(unknown.Select(s => s.Text)));
        Assert.All(unknown, s => Assert.Null(s.Foreground));

        // Truncated CSI at end of a stream chunk: dropped, head kept.
        var cut = AnsiParser.Parse("abc\x1b[3");
        Assert.Equal("abc", string.Concat(cut.Select(s => s.Text)));

        // Lone ESC at end: dropped.
        var lone = AnsiParser.Parse("abc\x1b");
        Assert.Equal("abc", string.Concat(lone.Select(s => s.Text)));
    }

    [Fact]
    public void Parse_LoneCarriageReturn_BecomesBreak()
    {
        var spans = AnsiParser.Parse("10%\r20%\r\n30%");
        var text = string.Concat(spans.Select(s => s.Text));
        Assert.Equal("10%\n20%\n30%", text);
    }

    [Fact]
    public void Parse_ControlChars_Dropped()
    {
        var spans = AnsiParser.Parse("a\0b\ac\td");
        Assert.Equal("abc\td", string.Concat(spans.Select(s => s.Text)));
    }

    [Fact]
    public void Parse_MergesAdjacentSameStyle()
    {
        var spans = AnsiParser.Parse("\x1b[31mab\x1b[31mcd");
        var only = Assert.Single(spans);
        Assert.Equal("abcd", only.Text);
    }

    [Fact]
    public void FeedStore_KeepsWireBytesVerbatim()
    {
        // End-to-end copy fidelity: the feed store keeps escape bytes
        // exactly; only the display layer interprets them. Copy, expand
        // and diagnostics all read from this stored source.
        var feed = new ConversationFeed();
        const string wire = "\x1b[32mok\x1b[0m and \x1b[2Kcleared";
        feed.AddSystemNotice(wire);
        var entry = feed.Snapshot().Last();
        Assert.Equal(wire, entry.Text);
    }

    [Fact]
    public void Parse_NullOrEmpty_YieldsNothing()
    {
        Assert.Empty(AnsiParser.Parse(null));
        Assert.Empty(AnsiParser.Parse(string.Empty));
    }

    [Fact]
    public void Parse_SourceString_NeverMutated()
    {
        // Copy fidelity: the parser returns spans, never a rewritten
        // string; the feed entry keeps the exact wire bytes.
        var source = "\x1b[32mok\x1b[0m";
        var before = source;
        AnsiParser.Parse(source);
        Assert.Equal(before, source);
        Assert.Contains("\x1b[32m", source, StringComparison.Ordinal);
    }
}
