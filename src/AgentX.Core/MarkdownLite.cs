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
    BoldItalic,
    Strikethrough,
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
    int Index = 0,
    int Depth = 0);

public sealed record MdDocument(IReadOnlyList<MdBlock> Blocks)
{
    public bool IsEmpty => Blocks.Count == 0;
}

/// <summary>
/// Restrained Markdown subset for assistant narrative (Pi-style: headings,
/// bold / italic / bold-italic, strikethrough, code spans and fences, nested
/// lists, links, quotes, rules, backslash escapes). Single-pass, no regex,
/// never throws: unclosed or exotic markup degrades to literal text so no
/// content is ever lost. Unknown constructs (tables, footnotes, HTML, LaTeX)
/// stay literal — documented, not silently dropped.
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
        var quote = new List<string>();
        // Indent levels seen by list items, shallowest first: an item's depth
        // is its index here, so 2-space and 4-space nesting styles both keep
        // their true levels (one static column rule would misread either).
        var listIndents = new List<int> { 0 };
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

        void FlushQuote()
        {
            if (quote.Count == 0)
            {
                return;
            }

            blocks.Add(new MdBlock(MdBlockKind.Quote, 0, false, ParseInline(string.Join(" ", quote))));
            quote.Clear();
        }

        void FlushBlock()
        {
            FlushParagraph();
            FlushQuote();
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
                FlushBlock();
                inFence = true;
                fenceLanguage = trimmed.Length > 3 ? trimmed[3..].Trim() : string.Empty;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushBlock();
                continue;
            }

            var headingLevel = HeadingLevel(trimmed);
            if (headingLevel > 0)
            {
                FlushBlock();
                blocks.Add(new MdBlock(MdBlockKind.Heading, headingLevel, false,
                    ParseInline(trimmed[(headingLevel + 1)..].TrimStart())));
                continue;
            }

            if (IsHr(trimmed))
            {
                FlushBlock();
                blocks.Add(new MdBlock(MdBlockKind.Hr, 0, false, []));
                continue;
            }

            if (trimmed.StartsWith(">"))
            {
                FlushParagraph();
                // Consecutive ">" lines are ONE blockquote (CommonMark
                // continuation), not one marker run per line.
                quote.Add(trimmed[1..].TrimStart());
                continue;
            }

            var listMarker = ListMarkerLength(trimmed, out var ordered);
            if (listMarker > 0)
            {
                FlushBlock();
                var number = 0;
                if (ordered)
                {
                    number = blocks.Count > 0 && blocks[^1].Kind == MdBlockKind.List && blocks[^1].Ordered
                        ? blocks[^1].Index + 1
                        : 1;
                }

                var columns = IndentColumns(line);
                while (listIndents.Count > 1 && columns < listIndents[^1])
                {
                    listIndents.RemoveAt(listIndents.Count - 1);
                }

                if (columns > listIndents[^1])
                {
                    listIndents.Add(columns);
                }

                // Capped at three visual levels: deep source indentation must
                // never push a bullet off a narrow (720px) window.
                var depth = Math.Min(listIndents.IndexOf(columns), 3);
                blocks.Add(new MdBlock(MdBlockKind.List, 0, ordered,
                    ParseInline(trimmed[listMarker..].TrimStart()), Index: number,
                    Depth: depth));
                continue;
            }

            FlushQuote();
            paragraph.Add(line.Trim());
        }

        if (inFence)
        {
            // Unclosed fence: the content still shows, as a code block.
            blocks.Add(new MdBlock(MdBlockKind.CodeFence, 0, false, [], fenceCode.ToString(), fenceLanguage));
        }

        FlushBlock();
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

    /// <summary>CommonMark's escape set: ASCII punctuation only - the
    /// printable symbol bands around digits and letters (there is no
    /// char.IsAsciiPunctuation helper to use).</summary>
    private static bool IsAsciiPunctuation(char ch)
        => ch is >= '!' and <= '/'
            or >= ':' and <= '@'
            or >= '[' and <= '`'
            or >= '{' and <= '~';

    /// <summary>Leading indentation in columns (tabs advance to the next
    /// multiple of 4). Nesting depth itself comes from the Parse indent stack,
    /// which adapts to both 2-space and 4-space list styles.</summary>
    private static int IndentColumns(string line)
    {
        var columns = 0;
        foreach (var ch in line)
        {
            if (ch == ' ')
            {
                columns++;
            }
            else if (ch == '\t')
            {
                columns += 4 - columns % 4;
            }
            else
            {
                break;
            }
        }

        return columns;
    }

    /// <summary>Left-to-right inline scan. Code spans are opaque; unclosed markers stay literal;
    /// '_' emphasis never starts or ends inside a word, so snake_case stays literal.</summary>
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

            // CommonMark escapes: backslash + ASCII punctuation is literal,
            // so \*never emphasises\* and \`stays a backtick\`.
            if (c == '\\' && i + 1 < text.Length && IsAsciiPunctuation(text[i + 1]))
            {
                literal.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                // Double-backtick spans (``code with ` inside``) are one span;
                // an unpaired delimiter stays literal.
                var width = i + 1 < text.Length && text[i + 1] == '`' ? 2 : 1;
                var delimiter = width == 2 ? "``" : "`";
                var end = text.IndexOf(delimiter, i + width, StringComparison.Ordinal);
                if (end < 0)
                {
                    literal.Append(c);
                    i++;
                    continue;
                }

                FlushLiteral();
                result.Add(new MdInline(MdInlineKind.Code, text[(i + width)..end]));
                i = end + width;
                continue;
            }

            if (c is '*' or '_')
            {
                // One delimiter run (*** / ** / * and ___ / __ / _) pairs as a
                // whole, so ***bold italic*** and **strong** parse exactly;
                // with no matching close, one delimiter falls through literal
                // so a shorter run can still pair instead of losing content.
                var run = 1;
                while (i + run < text.Length && text[i + run] == c)
                {
                    run++;
                }

                var width = Math.Min(run, 3);
                var opens = width > 1
                    || (c == '*'
                        ? i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1])
                        : i == 0 || !char.IsLetterOrDigit(text[i - 1]));

                // '_' never opens or closes inside a word, so snake_case and
                // file_name_with_underscores stay literal; '*' keeps
                // CommonMark's looser intraword rule.
                bool ClosesAt(int at)
                    => at > i + width
                        && !char.IsWhiteSpace(text[at - 1])
                        && (c == '*'
                            || at + width >= text.Length
                            || !char.IsLetterOrDigit(text[at + width]));

                var token = new string(c, width);
                var end = opens
                    ? text.IndexOf(token, i + width, StringComparison.Ordinal)
                    : -1;
                while (end >= 0 && !ClosesAt(end))
                {
                    end = text.IndexOf(token, end + 1, StringComparison.Ordinal);
                }

                if (end >= 0)
                {
                    FlushLiteral();
                    result.Add(new MdInline(
                        width == 3 ? MdInlineKind.BoldItalic
                        : width == 2 ? MdInlineKind.Bold
                        : MdInlineKind.Italic,
                        text[(i + width)..end]));
                    i = end + width;
                    continue;
                }

                literal.Append(c);
                i++;
                continue;
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushLiteral();
                    result.Add(new MdInline(MdInlineKind.Strikethrough, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }

                literal.Append("~~");
                i += 2;
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

            var startsUrl = (c == 'h' && text.AsSpan(i).StartsWith("http://"))
                || (c == 'h' && text.AsSpan(i).StartsWith("https://"));
            // An autolink needs a word boundary before it: "xhttps://y" is text.
            if (startsUrl
                && (i == 0 || char.IsWhiteSpace(text[i - 1])
                    || text[i - 1] is '(' or '[' or '<' or '"' or '\'' or '='))
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
