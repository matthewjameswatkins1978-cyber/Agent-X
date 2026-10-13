using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class MarkdownLiteTests
{
    [Fact]
    public void Blocks_HeadingsParagraphsListsCode()
    {
        var doc = MarkdownLite.Parse("# Title\n\nSome **bold** and *italic* text.\n\n- item one\n- item `two`\n\n```csharp\nvar x = 1;\n```\n");
        Assert.Equal(5, doc.Blocks.Count);
        Assert.Equal(MdBlockKind.Heading, doc.Blocks[0].Kind);
        Assert.Equal(1, doc.Blocks[0].Level);
        Assert.Equal("Title", Assert.Single(doc.Blocks[0].Inlines).Text);
        Assert.Equal(MdBlockKind.Paragraph, doc.Blocks[1].Kind);
        var para = doc.Blocks[1].Inlines;
        Assert.Contains(para, i => i.Kind == MdInlineKind.Bold && i.Text == "bold");
        Assert.Contains(para, i => i.Kind == MdInlineKind.Italic && i.Text == "italic");
        Assert.Equal(MdBlockKind.List, doc.Blocks[2].Kind);
        Assert.False(doc.Blocks[2].Ordered);
        Assert.Equal(MdBlockKind.List, doc.Blocks[3].Kind);
        var fence = doc.Blocks[4];
        Assert.Equal(MdBlockKind.CodeFence, fence.Kind);
        Assert.Equal("csharp", fence.Language);
        Assert.Contains("var x = 1;", fence.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_LinksCodeAndBareUrls()
    {
        var inlines = MarkdownLite.ParseInline("See [the docs](https://example.dev/x) and `code` plus https://example.dev/y end");
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Link && i.Text == "the docs" && i.Url == "https://example.dev/x");
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Code && i.Text == "code");
        Assert.Contains(inlines, i => i.Kind == MdInlineKind.Link && i.Url == "https://example.dev/y");
    }

    [Fact]
    public void Malformed_NeverLosesContent()
    {
        foreach (var tricky in new[]
        {
            "Unclosed **bold and *italic",
            "Tick ` without pair",
            "[link without url",
            "####### too many",
            "#nospace stays text",
            "```\nnever closed fence",
            "",
        })
        {
            var doc = MarkdownLite.Parse(tricky);
            var joined = string.Concat(doc.Blocks.SelectMany(b =>
                b.Inlines.Select(i => i.Text).Prepend(b.Code ?? string.Empty)));
            foreach (var token in tricky.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            {
                var word = token.Trim('`', '*', '#');
                if (word.Length > 0)
                {
                    Assert.Contains(word, joined, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(MarkdownLite.Parse("").IsEmpty);
        Assert.True(MarkdownLite.Parse(null).IsEmpty);
    }

    [Fact]
    public void QuoteHrOrdered()
    {
        var doc = MarkdownLite.Parse("> quoted note\n\n---\n\n1. first\n2. second\n");
        Assert.Equal(MdBlockKind.Quote, doc.Blocks[0].Kind);
        Assert.Equal(MdBlockKind.Hr, doc.Blocks[1].Kind);
        Assert.Equal(MdBlockKind.List, doc.Blocks[2].Kind);
        Assert.True(doc.Blocks[2].Ordered);
    }

    [Fact]
    public void DiffTint_OnlyMonoLines()
    {
        Assert.Equal("add", MarkdownLite.DiffLineKind("+added line"));
        Assert.Equal("del", MarkdownLite.DiffLineKind("-removed line"));
        Assert.Equal("hunk", MarkdownLite.DiffLineKind("@@ -1,2 +3,4 @@"));
        Assert.Null(MarkdownLite.DiffLineKind("+++ b/file")); // file headers excluded
        Assert.Null(MarkdownLite.DiffLineKind("--- a/file"));
        Assert.Null(MarkdownLite.DiffLineKind("plain output line"));
        Assert.Null(MarkdownLite.DiffLineKind("a + b = c"));
    }
}
