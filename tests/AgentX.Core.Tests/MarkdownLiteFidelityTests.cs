using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Beautiful Feed PR2 fidelity: these are the parser shapes the renderer
/// sees. They pin the CommonMark-aligned corrections (strikethrough, ***bold
/// italic***, __strong__, intraword underscores, backslash escapes,
/// double-backtick spans, autolink boundaries, nested-list depth and merged
/// blockquotes) without needing a render platform - pixel rendering stays
/// evidenced by the running-app screenshots.
/// </summary>
public sealed class MarkdownLiteFidelityTests
{
    private static IReadOnlyList<MdInline> Inline(string text)
        => MarkdownLite.ParseInline(text);

    [Fact]
    public void Strikethrough_ParsesAsDel()
    {
        var inlines = Inline("keep ~~old~~ value");
        var struck = Assert.Single(inlines, i => i.Kind == MdInlineKind.Strikethrough);
        Assert.Equal("old", struck.Text);
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Text && i.Text.Contains("keep", StringComparison.Ordinal));
    }

    [Fact]
    public void BoldItalic_ParsesTripleRun()
    {
        var inlines = Inline("***both ways***");
        var both = Assert.Single(inlines, i => i.Kind == MdInlineKind.BoldItalic);
        Assert.Equal("both ways", both.Text);
    }

    [Fact]
    public void StrongUnderscore_ParsesAsBold()
    {
        var inlines = Inline("__strong__ text");
        var bold = Assert.Single(inlines, i => i.Kind == MdInlineKind.Bold);
        Assert.Equal("strong", bold.Text);
    }

    [Fact]
    public void IntrawordUnderscore_StaysLiteral()
    {
        var inlines = Inline("file_name with other_name");
        Assert.DoesNotContain(inlines, i => i.Kind is MdInlineKind.Italic or MdInlineKind.Bold or MdInlineKind.BoldItalic);
        var joined = string.Concat(inlines.Select(i => i.Text));
        Assert.Contains("file_name", joined, StringComparison.Ordinal);
        Assert.Contains("other_name", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void IntrawordAsterisk_StillEmphasises()
    {
        // CommonMark keeps * as an intraword emphasiser; only '_' is barred.
        var inlines = Inline("foo*bar*baz");
        var italic = Assert.Single(inlines, i => i.Kind == MdInlineKind.Italic);
        Assert.Equal("bar", italic.Text);
    }

    [Fact]
    public void BackslashEscape_NeverEmphasises()
    {
        var inlines = Inline(@"\*literal\* and \`tick\`");
        Assert.DoesNotContain(inlines, i => i.Kind is MdInlineKind.Italic or MdInlineKind.Bold);
        var joined = string.Concat(inlines.Select(i => i.Text));
        Assert.Contains("*literal*", joined, StringComparison.Ordinal);
        Assert.Contains("`tick`", joined, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleBacktickSpan_HoldsSingleBackticks()
    {
        var inlines = Inline("``a ` inside``");
        var code = Assert.Single(inlines, i => i.Kind == MdInlineKind.Code);
        Assert.Equal("a ` inside", code.Text);
    }

    [Fact]
    public void Autolink_RequiresWordBoundary()
    {
        var inlines = Inline("xhttps://example.dev/y");
        Assert.DoesNotContain(inlines, i => i.Kind == MdInlineKind.Link);
        Assert.Contains("xhttps://example.dev/y", string.Concat(inlines.Select(i => i.Text)), StringComparison.Ordinal);

        var bounded = Inline("(https://example.dev/y)");
        Assert.Contains(bounded, i => i.Kind == MdInlineKind.Link);
    }

    [Fact]
    public void NestedLists_TwoSpaceStyle_KeepThreeLevels()
    {
        var doc = MarkdownLite.Parse("- top\n  - child\n    - grandchild\n");
        var lists = doc.Blocks.Where(b => b.Kind == MdBlockKind.List).ToArray();
        Assert.Equal(3, lists.Length);
        Assert.Equal([0, 1, 2], lists.Select(b => b.Depth).ToArray());
    }

    [Fact]
    public void NestedLists_FourSpaceStyle_KeepsTwoLevels()
    {
        var doc = MarkdownLite.Parse("- top\n    - child\n");
        var lists = doc.Blocks.Where(b => b.Kind == MdBlockKind.List).ToArray();
        Assert.Equal([0, 1], lists.Select(b => b.Depth).ToArray());
    }

    [Fact]
    public void ListDepth_DedentsBackToTopLevel()
    {
        var doc = MarkdownLite.Parse("- a\n  - b\n- c\n");
        var lists = doc.Blocks.Where(b => b.Kind == MdBlockKind.List).ToArray();
        Assert.Equal([0, 1, 0], lists.Select(b => b.Depth).ToArray());
    }

    [Fact]
    public void ConsecutiveQuoteLines_MergeIntoOneBlock()
    {
        var doc = MarkdownLite.Parse("> first line\n> second line\n");
        var quote = Assert.Single(doc.Blocks, b => b.Kind == MdBlockKind.Quote);
        Assert.Contains("first line", string.Concat(quote.Inlines.Select(i => i.Text)), StringComparison.Ordinal);
        Assert.Contains("second line", string.Concat(quote.Inlines.Select(i => i.Text)), StringComparison.Ordinal);

        // A blank line still separates two quotes (documented subset).
        var split = MarkdownLite.Parse("> one\n\n> two\n");
        Assert.Equal(2, split.Blocks.Count(b => b.Kind == MdBlockKind.Quote));
    }

    [Fact]
    public void QuoteThenParagraph_KeepsBothBlocks()
    {
        var doc = MarkdownLite.Parse("> quoted\nplain tail\n");
        Assert.Equal(MdBlockKind.Quote, doc.Blocks[0].Kind);
        Assert.Equal(MdBlockKind.Paragraph, doc.Blocks[1].Kind);
        Assert.Contains("plain tail", string.Concat(doc.Blocks[1].Inlines.Select(i => i.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedMarkup_StillNeverLosesContent()
    {
        foreach (var tricky in new[]
        {
            "Unclosed ~~strike and ***triple",
            "___ also unbalanced",
            @"a \ trailing backslash",
            "``",
            "xhttps://nowhere",
            "stray * asterisk without close",
            "> quote with ** unbalanced",
        })
        {
            var doc = MarkdownLite.Parse(tricky);
            var joined = string.Concat(doc.Blocks.SelectMany(b =>
                b.Inlines.Select(i => i.Text).Prepend(b.Code ?? string.Empty)));
            foreach (var token in tricky.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var word = token.Trim('`', '*', '~', '_', '\\', '#', '>');
                if (word.Length > 0)
                {
                    Assert.Contains(word, joined, StringComparison.Ordinal);
                }
            }
        }
    }
}
