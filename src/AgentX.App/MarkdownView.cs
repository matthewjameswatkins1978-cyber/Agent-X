using AgentX.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// Renders one feed row's text: Pi-grammar Markdown for assistant narrative
/// (headings/bold/code/lists/links in Pi dark hues) or tinted monospace for
/// tool/system output (diff +/- hunks in green/red). Builds Avalonia inlines
/// from the UI-free <see cref="MdDocument"/> model; unknown constructs were
/// already degraded to literal text by the parser, so nothing is lost here.
/// </summary>
public sealed class MarkdownView : SelectableTextBlock
{
    public static readonly StyledProperty<MdDocument?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, MdDocument?>(nameof(Markdown));

    public static readonly StyledProperty<string?> MonoTextProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(MonoText));

    public static readonly StyledProperty<bool> DiffTintProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(DiffTint));

    /// Quiet annotation rendering for bridge detail rows: one step
    /// smaller with the muted ink (explicit ANSI colours survive — only
    /// uncoloured text quiets). Bound from the row's density, so it
    /// scales with the feed size instead of hard-coding pixels.
    public static readonly StyledProperty<bool> DimProperty =
        AvaloniaProperty.Register<MarkdownView, bool>(nameof(Dim));

    public MdDocument? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? MonoText
    {
        get => GetValue(MonoTextProperty);
        set => SetValue(MonoTextProperty, value);
    }

    public bool DiffTint
    {
        get => GetValue(DiffTintProperty);
        set => SetValue(DiffTintProperty, value);
    }

    public bool Dim
    {
        get => GetValue(DimProperty);
        set => SetValue(DimProperty, value);
    }

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        MonoTextProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        DiffTintProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        DimProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
    }

    // B7 treatments: shared live brushes, so a treatment switch repaints
    // already-rendered inlines in place (see AxTheme).
    private static IBrush TextBrush => AxTheme.TextBrush;
    private static IBrush MutedBrush => AxTheme.MutedBrush;
    private static IBrush DimBrush => AxTheme.DimBrush;
    private static IBrush VioletBrush => AxTheme.VioletBrush;
    private static IBrush BlueBrush => AxTheme.BlueBrush;
    private static IBrush GreenBrush => AxTheme.GreenBrush;
    private static IBrush RedBrush => AxTheme.RedBrush;
    private static IBrush YellowBrush => AxTheme.YellowBrush;
    // Bundled faces follow the feed appearance (see AxTheme): read live at
    // every Rebuild, never cached, so an appearance switch + row rebuild
    // picks up the new families with no extra machinery.
    private static FontFamily MonoFamily => AxTheme.MonoFont;
    private static FontFamily VoiceFamily => AxTheme.VoiceFont;

    private void Rebuild()
    {
        Inlines?.Clear();
        Text = null;
        // Adjustable feed type (AxFeed.Sizes): prose rows take the full
        // size in the appearance prose face; technical rows sit one step
        // under with runs in the mono face. Local values override the XAML
        // defaults, so Settings changes apply to existing rows after the
        // standard RebuildConversationList refresh.
        var technical = Markdown is null || Markdown.IsEmpty;
        FontFamily = AxTheme.ProseFont;
        // Local size owns the row (a template binding would be shadowed):
        // density arrives as the Dim flag, never a competing FontSize.
        FontSize = technical ? AxTheme.FeedMonoSize - (Dim ? 1.5 : 0.0) : AxTheme.FeedFontSize;
        LineHeight = AxTheme.FeedLineHeight;
        if (!technical)
        {
            RenderMarkdown(Markdown!);
        }
        else if (MonoText is { } mono)
        {
            RenderMono(mono, DiffTint);
        }
    }

    private void RenderMarkdown(MdDocument document)
    {
        var inlines = Inlines!;
        foreach (var block in document.Blocks)
        {
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    // Pi headings sit near body size (bold + yellow carry the
                    // hierarchy, not large type). The step now scales with the
                    // adjustable feed size (AxFeed.Sizes, 13-16): body+2 / body+1 /
                    // body+0.5 - exactly 16 / 15 / 14.5 at the 14px default, so the
                    // accepted PR1 look is unchanged and 13px/16px keep hierarchy.
                    var size = AxTheme.FeedFontSize + (block.Level switch { 1 => 2.0, 2 => 1.0, _ => 0.5 });
                    AppendInlines(inlines, block.Inlines, null, size: size, weight: FontWeight.Bold, brush: YellowBrush);
                    inlines.Add(new LineBreak());
                    break;
                case MdBlockKind.CodeFence:
                    RenderCodeFence(inlines, block.Code ?? string.Empty, block.Language);
                    break;
                case MdBlockKind.List:
                    // Nested items keep their source level: two spaces per
                    // MdBlock.Depth (the MarkdownLite indent stack) per level.
                    var bullet = new string(' ', block.Depth * 2)
                        + (block.Ordered ? $"{block.Index}. " : "• ");
                    inlines.Add(new Run(bullet) { Foreground = VioletBrush });
                    AppendInlines(inlines, block.Inlines, null);
                    inlines.Add(new LineBreak());
                    break;
                case MdBlockKind.Quote:
                    inlines.Add(new Run("▍ ") { Foreground = DimBrush });
                    AppendInlines(inlines, block.Inlines, DimBrush, italic: true);
                    inlines.Add(new LineBreak());
                    break;
                case MdBlockKind.Hr:
                    inlines.Add(new Run(new string('─', 24)) { Foreground = DimBrush });
                    inlines.Add(new LineBreak());
                    break;
                default:
                    AppendInlines(inlines, block.Inlines, null);
                    inlines.Add(new LineBreak());
                    inlines.Add(new LineBreak());
                    break;
            }
        }
    }

    private static void AppendInlines(
        InlineCollection inlines,
        IReadOnlyList<MdInline> items,
        IBrush? @default,
        bool italic = false,
        double? size = null,
        FontWeight? weight = null,
        IBrush? brush = null)
    {
        foreach (var inline in items)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                    inlines.Add(StyledRun(inline.Text, size, weight ?? FontWeight.Bold, brush ?? @default ?? TextBrush, italic));
                    break;
                case MdInlineKind.BoldItalic:
                    inlines.Add(StyledRun(inline.Text, size, weight ?? FontWeight.Bold, brush ?? @default ?? TextBrush, true));
                    break;
                case MdInlineKind.Italic:
                    inlines.Add(StyledRun(inline.Text, size, weight, brush ?? @default ?? TextBrush, true));
                    break;
                case MdInlineKind.Strikethrough:
                    // GFM del: colour stays with the surrounding voice (Pi
                    // strikes without recolouring); the rule carries the meaning.
                    inlines.Add(StyledRun(inline.Text, size, weight, brush ?? @default ?? TextBrush, italic,
                        Avalonia.Media.TextDecorations.Strikethrough));
                    break;
                case MdInlineKind.Code:
                    // Inline code keeps its violet Pi role but inherits the block's
                    // size/weight, so code inside a heading stays heading-sized.
                    inlines.Add(MonoRun(inline.Text, VioletBrush, size, weight));
                    break;
                case MdInlineKind.Link:
                    var link = new Run(inline.Text)
                    {
                        Foreground = brush ?? BlueBrush,
                        TextDecorations = Avalonia.Media.TextDecorations.Underline,
                    };
                    if (size is { } linkSize)
                    {
                        link.FontSize = linkSize;
                    }

                    if (weight is { } linkWeight)
                    {
                        link.FontWeight = linkWeight;
                    }

                    inlines.Add(link);
                    // Pi renders the target muted beside the label (mdLinkUrl);
                    // a bare URL renders once, as the label itself.
                    if (inline.Url is { } url && url != inline.Text)
                    {
                        inlines.Add(new Run($" ({url})") { Foreground = MutedBrush });
                    }

                    break;
                default:
                    inlines.Add(StyledRun(inline.Text, size, weight, brush ?? @default ?? TextBrush, italic));
                    break;
            }
        }
    }

    /// <summary>
    /// Language-aware fence body (display only). Diff fences take the
    /// universal +/-/hunk verdicts; known languages take the conservative
    /// token treatment in Pi roles; anything else keeps the uniform code
    /// voice (Pi's own CLI emits fences plain — no rainbow is invented).
    /// </summary>
    private void RenderCodeFence(InlineCollection inlines, string code, string? language)
    {
        var normalized = CodeHighlight.Normalize(language);
        if (normalized is null)
        {
            foreach (var line in code.Split('\n'))
            {
                inlines.Add(MonoRun(line.TrimEnd('\r'), GreenBrush));
                inlines.Add(new LineBreak());
            }

            return;
        }

        foreach (var token in CodeHighlight.Highlight(code, normalized))
        {
            if (token.Text == "\n")
            {
                inlines.Add(new LineBreak());
                continue;
            }

            var run = MonoRun(token.Text, CodeBrushFor(token.Kind));
            if (token.Kind == CodeTokenKind.Comment)
            {
                run.FontStyle = FontStyle.Italic;
            }

            inlines.Add(run);
        }
    }

    // Pi 1.3.0 dark theme roles (dist/modes/interactive/theme/dark.json):
    // syntaxString is yellow (okhsl 52), syntaxNumber is green — not the
    // other way around.
    private static IBrush CodeBrushFor(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => BlueBrush,
        CodeTokenKind.String => YellowBrush,
        CodeTokenKind.Comment => DimBrush,
        CodeTokenKind.Number => GreenBrush,
        CodeTokenKind.DiffAdd => GreenBrush,
        CodeTokenKind.DiffDel => RedBrush,
        CodeTokenKind.DiffHunk => YellowBrush,
        CodeTokenKind.Error => RedBrush,
        _ => TextBrush,
    };

    private void RenderMono(string text, bool diffTint)
    {
        // Terminal output keeps its own colours: the parser turns SGR into
        // real runs and swallows every other sequence, so escapes can
        // neither render as garbage nor act. Runs without explicit colour
        // keep the diff verdict brush when asked (decoded line text, so a
        // leading escape never hides a +/- hunk).
        var inlines = Inlines!;
        var lines = new List<List<(AnsiSpan Span, string Text)>>();
        var current = new List<(AnsiSpan Span, string Text)>();
        foreach (var span in AnsiParser.Parse(text))
        {
            var parts = span.Text.Split('\n');
            current.Add((span, parts[0]));
            for (var p = 1; p < parts.Length; p++)
            {
                lines.Add(current);
                current = new List<(AnsiSpan Span, string Text)>();
                current.Add((span, parts[p]));
            }
        }

        lines.Add(current);

        for (var l = 0; l < lines.Count; l++)
        {
            IBrush? diffBrush = null;
            if (diffTint)
            {
                var decoded = string.Concat(lines[l].Select(r => r.Text));
                diffBrush = MarkdownLite.DiffLineKind(decoded) switch
                {
                    "add" => GreenBrush,
                    "del" => RedBrush,
                    "hunk" => YellowBrush,
                    _ => null,
                };
            }

            foreach (var (span, part) in lines[l])
            {
                if (part.Length > 0)
                {
                    // Dim quiets only uncoloured text: explicit ANSI
                    // colours and diff verdicts survive untouched.
                    inlines.Add(AnsiRun(part, span, diffBrush ?? (Dim ? MutedBrush : null)));
                }
            }

            if (l + 1 < lines.Count)
            {
                inlines.Add(new LineBreak());
            }
        }
    }

    private static Run AnsiRun(string text, AnsiSpan span, IBrush? fallback)
    {
        var run = new Run(text) { FontFamily = MonoFamily };
        run.Foreground = span.Foreground is { } fg
            ? new SolidColorBrush(Color.FromRgb(fg.R, fg.G, fg.B))
            : fallback ?? TextBrush;
        if (span.Background is { } bg)
        {
            run.Background = new SolidColorBrush(Color.FromRgb(bg.R, bg.G, bg.B));
        }

        if (span.Bold)
        {
            run.FontWeight = FontWeight.Bold;
        }

        if (span.Italic)
        {
            run.FontStyle = FontStyle.Italic;
        }

        if (span.Underline)
        {
            run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        }

        return run;
    }

    private static Run StyledRun(
        string text,
        double? size,
        FontWeight? weight,
        IBrush brush,
        bool? italic,
        TextDecorationCollection? decorations = null)
    {
        var run = new Run(text) { Foreground = brush };
        if (decorations is not null)
        {
            run.TextDecorations = decorations;
        }

        if (size.HasValue)
        {
            run.FontSize = size.Value;
        }

        if (weight.HasValue)
        {
            run.FontWeight = weight.Value;
        }

        if (italic == true)
        {
            run.FontStyle = FontStyle.Italic;
            // Commentary voice (Operator: Monaspace Argon; Studio: Geist,
            // same as prose). Quotes and agent asides only — never code.
            run.FontFamily = VoiceFamily;
        }

        return run;
    }

    private static Run MonoRun(string text, IBrush? brush, double? size = null, FontWeight? weight = null)
    {
        var run = new Run(text) { FontFamily = MonoFamily, Foreground = brush ?? TextBrush };
        if (size.HasValue)
        {
            run.FontSize = size.Value;
        }

        if (weight.HasValue)
        {
            run.FontWeight = weight.Value;
        }

        return run;
    }
}
