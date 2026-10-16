using AgentX.App;
using AgentX.Core;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AgentX.App.Tests;

/// <summary>
/// Beautiful Feed PR2 renderer contracts, asserted on the real
/// <see cref="MarkdownView"/> inline tree: heading steps that scale with the
/// adjustable feed size, Pi semantic colours/roles (violet code, blue links
/// with muted URLs, yellow headings), strikethrough, bold-italic voice,
/// nested-list indentation and the mono/mono-size split. Controls construct
/// without a render platform (see AxDesignTests); genuine pixel rendering is
/// evidenced by the running-app screenshots.
/// </summary>
public sealed class MarkdownViewTests
{
    private static MarkdownView Build(string? markdown = null, string? mono = null, bool diffTint = false)
        => new() { Markdown = markdown is null ? null : MarkdownLite.Parse(markdown), MonoText = mono, DiffTint = diffTint };

    private static Run[] Runs(MarkdownView view) => [.. view.Inlines!.OfType<Run>()];

    /// Apply a feed size, run the assertion, always restore the previous
    /// appearance (AxTheme statics are shared with other tests).
    private static void WithFeedSize(double size, Action action)
    {
        var priorAppearance = AxTheme.CurrentAppearance;
        var priorSize = AxTheme.FeedFontSize;
        try
        {
            AxTheme.ApplyAppearance(priorAppearance, size);
            action();
        }
        finally
        {
            AxTheme.ApplyAppearance(priorAppearance, priorSize);
        }
    }

    [Theory]
    [InlineData(13.0)]
    [InlineData(14.0)]
    [InlineData(16.0)]
    public void HeadingSteps_ScaleWithFeedSize(double feedSize)
    {
        WithFeedSize(feedSize, () =>
        {
            var runs = Runs(Build("# Title\n## Sub\n### Minor"));
            Assert.Equal(feedSize + 2, runs[0].FontSize);
            Assert.Equal(feedSize + 1, runs[1].FontSize);
            Assert.Equal(feedSize + 0.5, runs[2].FontSize);
            Assert.All(runs, r =>
            {
                Assert.Equal(FontWeight.Bold, r.FontWeight);
                Assert.Same(AxTheme.YellowBrush, r.Foreground);
            });
        });
    }

    [Fact]
    public void InlineCodeInsideHeading_KeepsHeadingSizeAndVioletRole()
    {
        var runs = Runs(Build("# Title with `code`"));
        var code = Assert.Single(runs, r => r.Text == "code");
        Assert.Same(AxTheme.VioletBrush, code.Foreground);
        Assert.Equal(AxTheme.MonoFont, code.FontFamily);
        Assert.Equal(AxTheme.FeedFontSize + 2, code.FontSize);
        Assert.Equal(FontWeight.Bold, code.FontWeight);
    }

    [Fact]
    public void Strikethrough_RendersWithStrikeDecoration()
    {
        var runs = Runs(Build("drop ~~old~~ keep"));
        var struck = Assert.Single(runs, r => r.Text == "old");
        Assert.NotNull(struck.TextDecorations);
        Assert.Contains(struck.TextDecorations!, d => d.Location == TextDecorationLocation.Strikethrough);
    }

    [Fact]
    public void MarkdownLink_ShowsLabelBlueAndUrlMuted()
    {
        var runs = Runs(Build("See [docs](https://pi.dev/docs) today"));
        var label = Assert.Single(runs, r => r.Text == "docs");
        Assert.Same(AxTheme.BlueBrush, label.Foreground);
        var url = Assert.Single(runs, r => r.Text == " (https://pi.dev/docs)");
        Assert.Same(AxTheme.MutedBrush, url.Foreground);
    }

    [Fact]
    public void NestedListItems_IndentByDepth()
    {
        var runs = Runs(Build("- top\n  - child"));
        Assert.Equal(4, runs.Length); // bullet, text, indented bullet, text
        Assert.Equal("• ", runs[0].Text);
        Assert.Equal("  • ", runs[2].Text);
        Assert.Same(AxTheme.VioletBrush, runs[2].Foreground);
    }

    [Fact]
    public void BoldItalic_UsesBoldVoiceFace()
    {
        var runs = Runs(Build("***both ways***"));
        var both = Assert.Single(runs);
        Assert.Equal(FontWeight.Bold, both.FontWeight);
        Assert.Equal(FontStyle.Italic, both.FontStyle);
        Assert.Same(AxTheme.TextBrush, both.Foreground);
        Assert.Equal(AxTheme.VoiceFont, both.FontFamily);
    }

    [Fact]
    public void ProseRows_UseProseFaceAtFeedSize_TechnicalRowsOneStepUnder()
    {
        WithFeedSize(16, () =>
        {
            var prose = Build("plain words");
            Assert.Equal(AxTheme.ProseFont, prose.FontFamily);
            Assert.Equal(16, prose.FontSize);

            var mono = Build(mono: "tool output line");
            // Technical rows size at the control level and paint every run in
            // the appearance mono face (the control family stays the prose
            // face, exactly as Rebuild sets it).
            Assert.Equal(15, mono.FontSize); // AxFeed.MonoSize: one step under prose
            var monoRun = Assert.Single(Runs(mono));
            Assert.Equal(AxTheme.MonoFont, monoRun.FontFamily);
        });
    }

    [Fact]
    public void Dim_QuietsUncolouredMonoOnly()
    {
        var view = new MarkdownView { MonoText = "plain line", Dim = true };
        Assert.Equal(AxTheme.FeedMonoSize - 1.5, view.FontSize);
        var quiet = Assert.Single(Runs(view));
        Assert.Same(AxTheme.MutedBrush, quiet.Foreground);
        // Explicit ANSI colours and diff verdicts survive the quieting.
        var ansi = new MarkdownView { MonoText = "\x1b[31mred line", Dim = true };
        Assert.NotSame(AxTheme.MutedBrush, Assert.Single(Runs(ansi)).Foreground);
        var diff = new MarkdownView { MonoText = "+added line", DiffTint = true, Dim = true };
        Assert.Same(AxTheme.GreenBrush, Assert.Single(Runs(diff)).Foreground);
    }

    [Fact]
    public void DiffTint_StillTintsOnlyDiffLines()
    {
        var add = Assert.Single(Runs(Build(mono: "+added line", diffTint: true)));
        Assert.Same(AxTheme.GreenBrush, add.Foreground);
        var plain = Assert.Single(Runs(Build(mono: "plain line", diffTint: true)));
        Assert.Same(AxTheme.TextBrush, plain.Foreground);
    }
}
