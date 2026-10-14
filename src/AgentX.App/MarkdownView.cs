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

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        MonoTextProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        DiffTintProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
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
    private static readonly FontFamily MonoFamily = new("Cascadia Code,Consolas,monospace");

    private void Rebuild()
    {
        Inlines?.Clear();
        Text = null;
        if (Markdown is { } document && !document.IsEmpty)
        {
            RenderMarkdown(document);
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
                    // hierarchy, not large type): body 14 → H1 16, H2 15, H3+ 14.5.
                    var size = block.Level switch { 1 => 16.0, 2 => 15.0, _ => 14.5 };
                    foreach (var inline in block.Inlines)
                    {
                        inlines.Add(StyledRun(inline.Text, size, FontWeight.Bold, YellowBrush, null));
                    }

                    inlines.Add(new LineBreak());
                    break;
                case MdBlockKind.CodeFence:
                    foreach (var line in (block.Code ?? string.Empty).Split('\n'))
                    {
                        inlines.Add(MonoRun(line.TrimEnd('\r'), GreenBrush));
                        inlines.Add(new LineBreak());
                    }

                    break;
                case MdBlockKind.List:
                    var bullet = block.Ordered ? $"{block.Index}. " : "• ";
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

    private static void AppendInlines(InlineCollection inlines, IReadOnlyList<MdInline> items, IBrush? @default, bool italic = false)
    {
        foreach (var inline in items)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                    inlines.Add(StyledRun(inline.Text, null, FontWeight.Bold, @default ?? TextBrush, italic));
                    break;
                case MdInlineKind.Italic:
                    inlines.Add(StyledRun(inline.Text, null, null, @default ?? TextBrush, true));
                    break;
                case MdInlineKind.Code:
                    inlines.Add(MonoRun(inline.Text, VioletBrush));
                    break;
                case MdInlineKind.Link:
                    inlines.Add(new Run(inline.Text)
                    {
                        Foreground = BlueBrush,
                        TextDecorations = Avalonia.Media.TextDecorations.Underline,
                    });
                    break;
                default:
                    inlines.Add(StyledRun(inline.Text, null, null, @default ?? TextBrush, italic));
                    break;
            }
        }
    }

    private void RenderMono(string text, bool diffTint)
    {
        var inlines = Inlines!;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            IBrush? brush = null;
            if (diffTint)
            {
                brush = MarkdownLite.DiffLineKind(line) switch
                {
                    "add" => GreenBrush,
                    "del" => RedBrush,
                    "hunk" => YellowBrush,
                    _ => null,
                };
            }

            inlines.Add(MonoRun(line, brush));
            if (i + 1 < lines.Length)
            {
                inlines.Add(new LineBreak());
            }
        }
    }

    private static Run StyledRun(string text, double? size, FontWeight? weight, IBrush brush, bool? italic)
    {
        var run = new Run(text) { Foreground = brush };
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
        }

        return run;
    }

    private static Run MonoRun(string text, IBrush? brush)
        => new(text) { FontFamily = MonoFamily, Foreground = brush ?? TextBrush };
}
