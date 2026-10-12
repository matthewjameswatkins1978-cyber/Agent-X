namespace AgentX.Core;

/// <summary>Block shapes the Live renderer understands (deliberate subset).</summary>
public enum MdBlockKind
{
    Paragraph,
    Heading,
    CodeFence,
    List,
    Quote,
    Hr,
}

/// <summary>Inline shapes inside a block (deliberate subset).</summary>
public enum MdInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
}

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

public sealed record MdBlock(
    MdBlockKind Kind,
    int Level,
    bool Ordered,
    IReadOnlyList<MdInline> Inlines,
    string? Code = null,
    string? Language = null,
    int Index = 0);

public sealed record MdDocument(IReadOnlyList<MdBlock> Blocks)
{
    public bool IsEmpty => Blocks.Count == 0;
}

/// <summary>
/// Restrained Markdown subset for assistant narrative (Pi-style: headings,
/// bold, code, lists, links, quotes, fences). Single-pass, no regex, never
/// throws: unclosed or exotic markup degrades to literal text so no content
/// is ever lost. Unknown constructs (tables, footnotes, HTML) stay literal —
/// documented, not silently dropped.
/// </summary>
public static class MarkdownLite
{
    public static MdDocument Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new MdDocument([]);
        }

        var blocks = new List<MdBlock>();
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var paragraph = new List<string>();
        var inFence = false;
        var fenceLanguage = "";
        var fenceCode = new System.Text.StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Count == 0)
            {
                return;
            }

            blocks.Add(new MdBlock(MdBlockKind.Paragraph, 0, false, ParseInline(string.Join(" ", paragraph))));
            paragraph.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (inFence)
            {
                if (line.TrimStart().StartsWith("```"))
                {
                    blocks.Add(new MdBlock(MdBlockKind.CodeFence, 0, false, [], fenceCode.ToString(), fenceLanguage));
                    fenceCode.Clear();
                    inFence = false;
                }
                else
                {
                    fenceCode.AppendLine(line);
                }

                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                inFence = true;
                fenceLanguage = trimmed.Length > 3 ? trimmed[3..].Trim() : string.Empty;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }

            var headingLevel = HeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, headingLevel, false,
                    ParseInline(trimmed[(headingLevel + 1)..].TrimStart())));
                continue;
            }

            if (IsHr(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Hr, 0, false, []));
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, 0, false,
                    ParseInline(trimmed[1..].TrimStart())));
                continue;
            }

            var listMarker = ListMarkerLength(trimmed, out var ordered);
            if (listMarker > 0)
            {
                FlushParagraph();
                var number = 0;
                if (ordered)
                {
                    number = blocks.Count > 0 && blocks[^1].Kind == MdBlockKind.List && blocks[^1].Ordered
                        ? blocks[^1].Index + 1
                        : 1;
                }

                blocks.Add(new MdBlock(MdBlockKind.List, 0, ordered,
                    ParseInline(trimmed[listMarker..].TrimStart()), Index: number));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        if (inFence)
        {
            // Unclosed fence: the content still shows, as a code block.
            blocks.Add(new MdBlock(MdBlockKind.CodeFence, 0, false, [], fenceCode.ToString(), fenceLanguage));
        }

        FlushParagraph();
        return new MdDocument(blocks);
    }

    private static int HeadingLevel(string trimmed)
    {
        var level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#')
        {
            level++;
        }

        return level > 0 && level < trimmed.Length && trimmed[level] == ' ' ? level : 0;
    }

    private static bool IsHr(string trimmed)
        => trimmed is "---" or "***" or "___"
            || (trimmed.Length > 3 && trimmed.Trim('-', '*', '_', ' ').Length == 0
                && (trimmed.Contains("---") || trimmed.Contains("***") || trimmed.Contains("___")));

    private static int ListMarkerLength(string trimmed, out bool ordered)
    {
        ordered = false;
        if (trimmed.Length >= 2 && (trimmed[0] is '-' or '*' or '+') && trimmed[1] == ' ')
        {
            return 2;
        }

        var digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits > 0 && digits + 1 < trimmed.Length
            && (trimmed[digits] is '.' or ')') && trimmed[digits + 1] == ' ')
        {
            ordered = true;
            return digits + 2;
        }

        return 0;
    }

    /// <summary>Left-to-right inline scan. Code spans are opaque; unclosed markers stay literal.</summary>
    public static IReadOnlyList<MdInline> ParseInline(string text)
    {
        var result = new List<MdInline>();
        var literal = new System.Text.StringBuilder();
        var i = 0;

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                result.Add(new MdInline(MdInlineKind.Text, literal.ToString()));
                literal.Clear();
            }
        }

        while (i < text.Length)
        {
            var c = text[i];
            if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end < 0)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                FlushLiteral();
                result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    literal.Append("**");
                    i += 2;
                    continue;
                }

                FlushLiteral();
                result.Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..end]));
                i = end + 2;
                continue;
            }

            if (c == '*' || c == '_')
            {
                var end = text.IndexOf(c, i + 1);
                if (end < 0 || end == i + 1)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                FlushLiteral();
                result.Add(new MdInline(MdInlineKind.Italic, text[(i + 1)..end]));
                i = end + 1;
                continue;
            }

            if (c == '[')
            {
                var mid = text.IndexOf("](", i + 1, StringComparison.Ordinal);
                if (mid >= 0)
                {
                    var end = text.IndexOf(')', mid + 2);
                    if (end >= 0)
                    {
                        FlushLiteral();
                        result.Add(new MdInline(
                            MdInlineKind.Link, text[(i + 1)..mid], text[(mid + 2)..end]));
                        i = end + 1;
                        continue;
                    }
                }

                literal.Append(c);
                i++;
                continue;
            }

            if ((c == 'h' && text.AsSpan(i).StartsWith("http://"))
                || (c == 'h' && text.AsSpan(i).StartsWith("https://")))
            {
                var end = i;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != ')')
                {
                    end++;
                }

                FlushLiteral();
                var url = text[i..end];
                result.Add(new MdInline(MdInlineKind.Link, url, url));
                i = end;
                continue;
            }

            literal.Append(c);
            i++;
        }

        FlushLiteral();
        return result;
    }

    /// <summary>
    /// Diff tint for mono tool-output lines only (never assistant prose):
    /// "add" for +++, "del" for ---, "hunk" for @@, null otherwise. The +++/---
    /// file headers are excluded so headers don't masquerade as content.
    /// </summary>
    public static string? DiffLineKind(string line)
    {
        if (line.StartsWith("+++") || line.StartsWith("---"))
        {
            return null;
        }

        if (line.StartsWith('+'))
        {
            return "add";
        }

        if (line.StartsWith('-'))
        {
            return "del";
        }

        if (line.StartsWith("@@"))
        {
            return "hunk";
        }

        return null;
    }
}
