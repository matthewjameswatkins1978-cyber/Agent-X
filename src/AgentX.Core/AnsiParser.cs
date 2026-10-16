namespace AgentX.Core;

/// <summary>Display-only RGB colour from an ANSI SGR sequence (terminal fidelity).</summary>
public readonly record struct RgbColor(byte R, byte G, byte B);

/// <summary>One styled text segment of terminal output. Display only.</summary>
public sealed record AnsiSpan(string Text, bool Bold, bool Italic, bool Underline, RgbColor? Foreground, RgbColor? Background);

/// <summary>
/// Safe ANSI parser for tool output (display only — the source bytes are
/// never mutated; copy/diagnostics keep the original text with escapes).
/// Interprets SGR colour/style sequences (basic, bright, 256-palette, RGB)
/// and swallows all other terminal sequences (cursor, erase, OSC titles,
/// device control) so they can neither render as garbage nor act.
/// Malformed or stream-truncated sequences are dropped with the surrounding
/// text kept; a lone carriage return becomes a line break (progress spam
/// stays readable) while CRLF stays one break. Pure and allocation-light.
/// </summary>
public static class AnsiParser
{
    private const char Esc = '\x1b';

    /// Canonical xterm 16-colour palette (normal 30-37/40-47, bright 90-97/100-107).
    private static readonly RgbColor[] Standard =
    [
        new(0, 0, 0), new(205, 0, 0), new(0, 205, 0), new(205, 205, 0),
        new(0, 0, 238), new(205, 0, 205), new(0, 205, 205), new(229, 229, 229),
        new(127, 127, 127), new(255, 0, 0), new(0, 255, 0), new(255, 255, 0),
        new(92, 92, 255), new(255, 0, 255), new(0, 255, 255), new(255, 255, 255),
    ];

    private static readonly int[] CubeLevels = [0, 95, 135, 175, 215, 255];

    /// 256-colour xterm palette: 0-15 standard, 16-231 RGB cube, 232-255 grey ramp.
    public static RgbColor Xterm256(int n)
    {
        if (n < 0)
        {
            return Standard[0];
        }

        if (n < 16)
        {
            return Standard[n];
        }

        if (n < 232)
        {
            var v = n - 16;
            return new RgbColor(
                (byte)CubeLevels[v / 36],
                (byte)CubeLevels[(v / 6) % 6],
                (byte)CubeLevels[v % 6]);
        }

        if (n < 256)
        {
            var g = (byte)(8 + ((n - 232) * 10));
            return new RgbColor(g, g, g);
        }

        return Standard[15];
    }

    public static IReadOnlyList<AnsiSpan> Parse(string? text)
    {
        var spans = new List<AnsiSpan>();
        if (string.IsNullOrEmpty(text))
        {
            return spans;
        }

        var current = new StringSegment();
        var bold = false;
        var italic = false;
        var underline = false;
        RgbColor? fg = null;
        RgbColor? bg = null;

        void Flush()
        {
            if (current.Length > 0)
            {
                var last = spans.Count > 0 ? spans[^1] : null;
                if (last is not null && last.Bold == bold && last.Italic == italic
                    && last.Underline == underline && last.Foreground.Equals(fg) && last.Background.Equals(bg))
                {
                    spans[^1] = last with { Text = last.Text + current.ToString() };
                }
                else
                {
                    spans.Add(new AnsiSpan(current.ToString(), bold, italic, underline, fg, bg));
                }

                current.Clear();
            }
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == Esc)
            {
                Flush();
                i = ConsumeSequence(text, i, ref bold, ref italic, ref underline, ref fg, ref bg);
                continue;
            }

            if (c == '\r')
            {
                // CRLF stays one break; a lone CR (progress rewrites) becomes
                // its own break so overwritten lines stay readable.
                Flush();
                current.Append('\n');
                i += (i + 1 < text.Length && text[i + 1] == '\n') ? 2 : 1;
                continue;
            }

            if (c == '\0' || c == '\x7f' || (c < ' ' && c != '\n' && c != '\t'))
            {
                // Other C0 controls and DEL are terminal actions, not text.
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        Flush();
        return spans;
    }

    /// Consume one escape sequence at text[i] (text[i] is ESC). Applies SGR,
    /// swallows everything else, drops malformed/truncated introductions.
    /// Returns the index just past the consumed sequence.
    private static int ConsumeSequence(
        string text, int i, ref bool bold, ref bool italic, ref bool underline, ref RgbColor? fg, ref RgbColor? bg)
    {
        var j = i + 1;
        if (j >= text.Length)
        {
            return j; // Truncated: lone ESC at the very end.
        }

        var kind = text[j];
        if (kind == '[')
        {
            // CSI: parameters + intermediates, then a final byte (@-~).
            var k = j + 1;
            while (k < text.Length && text[k] >= '0' && text[k] <= '?')
            {
                k++;
            }

            while (k < text.Length && text[k] >= ' ' && text[k] <= '/')
            {
                k++;
            }

            if (k >= text.Length)
            {
                return text.Length; // Truncated mid-sequence: drop it all.
            }

            var final = text[k];
            if (final == 'm')
            {
                ApplySgr(text.Substring(j + 1, k - j - 1), ref bold, ref italic, ref underline, ref fg, ref bg);
            }

            // Any other CSI final (erase, cursor, scroll, mode) is swallowed.
            return k + 1;
        }

        if (kind == ']')
        {
            // OSC: swallow to BEL or ST (ESC\). Truncated OSC is dropped whole.
            var k = j + 1;
            while (k < text.Length)
            {
                if (text[k] == '\a')
                {
                    return k + 1;
                }

                if (text[k] == Esc && k + 1 < text.Length && text[k + 1] == '\\')
                {
                    return k + 2;
                }

                k++;
            }

            return text.Length;
        }

        if (kind is 'P' or 'X' or '^' or '_')
        {
            // DCS/SOS/PM/APC: swallow to ST (ESC\). Truncated: drop whole.
            var k = j + 1;
            while (k < text.Length)
            {
                if (text[k] == Esc && k + 1 < text.Length && text[k + 1] == '\\')
                {
                    return k + 2;
                }

                k++;
            }

            return text.Length;
        }

        if (kind is '(' or ')' or '#' or '=' or '>' or ' ' || (kind >= '0' && kind <= '7'))
        {
            // Two-character introductions (charset, keypad, DEC specials).
            return Math.Min(j + 2, text.Length);
        }

        if (kind is >= '@' and <= '~')
        {
            // Single-character Fe sequences (RIS, save/restore cursor, …).
            return j + 1;
        }

        // Not a sequence: drop the ESC, keep the following char as text.
        return j;
    }

    private static void ApplySgr(
        string parameters, ref bool bold, ref bool italic, ref bool underline, ref RgbColor? fg, ref RgbColor? bg)
    {
        // Empty (ESC[m) is a full reset. Split-then-parse keeps the common
        // shapes allocation-light; extended colour walks its own tail.
        List<int>? codes = null;
        if (parameters.Length == 0)
        {
            ApplyCode(0, ref bold, ref italic, ref underline, ref fg, ref bg);
            return;
        }

        codes = [];
        var value = 0;
        var hasValue = false;
        foreach (var c in parameters)
        {
            if (c >= '0' && c <= '9')
            {
                value = (value * 10) + (c - '0');
                hasValue = true;
            }
            else if (c == ';' || c == ':')
            {
                codes.Add(hasValue ? value : 0);
                value = 0;
                hasValue = false;
            }
            else
            {
                // Malformed parameter: abandon this SGR, keep prior style.
                return;
            }
        }

        codes.Add(hasValue ? value : 0);

        var k = 0;
        while (k < codes.Count)
        {
            var code = codes[k];
            if ((code == 38 || code == 48) && k + 2 < codes.Count && codes[k + 1] == 5)
            {
                var color = Xterm256(codes[k + 2]);
                if (code == 38)
                {
                    fg = color;
                }
                else
                {
                    bg = color;
                }

                k += 3;
            }
            else if ((code == 38 || code == 48) && k + 4 < codes.Count && codes[k + 1] == 2)
            {
                var color = new RgbColor((byte)codes[k + 2], (byte)codes[k + 3], (byte)codes[k + 4]);
                if (code == 38)
                {
                    fg = color;
                }
                else
                {
                    bg = color;
                }

                k += 5;
            }
            else if ((code == 38 || code == 48) && k + 1 < codes.Count)
            {
                // Truncated extended colour (38;5 with no index, …): ignore
                // the introducer, keep parsing what follows.
                k += 2;
            }
            else
            {
                ApplyCode(code, ref bold, ref italic, ref underline, ref fg, ref bg);
                k++;
            }
        }
    }

    private static void ApplyCode(
        int code, ref bool bold, ref bool italic, ref bool underline, ref RgbColor? fg, ref RgbColor? bg)
    {
        switch (code)
        {
            case 0:
                bold = italic = underline = false;
                fg = bg = null;
                break;
            case 1:
                bold = true;
                break;
            case 3:
                italic = true;
                break;
            case 4:
                underline = true;
                break;
            case 22:
                bold = false;
                break;
            case 23:
                italic = false;
                break;
            case 24:
                underline = false;
                break;
            case 39:
                fg = null;
                break;
            case 49:
                bg = null;
                break;
            default:
                if (code is >= 30 and <= 37)
                {
                    fg = Standard[code - 30];
                }
                else if (code is >= 90 and <= 97)
                {
                    fg = Standard[code - 90 + 8];
                }
                else if (code is >= 40 and <= 47)
                {
                    bg = Standard[code - 40];
                }
                else if (code is >= 100 and <= 107)
                {
                    bg = Standard[code - 100 + 8];
                }

                // Unknown SGR codes are ignored, never rendered.
                break;
        }
    }

    /// Small append buffer to avoid per-character strings.
    private sealed class StringSegment
    {
        private System.Text.StringBuilder? _builder;

        public int Length { get; private set; }

        public void Append(char c)
        {
            _builder ??= new System.Text.StringBuilder();
            _builder.Append(c);
            Length++;
        }

        public void Clear()
        {
            _builder?.Clear();
            Length = 0;
        }

        public override string ToString() => _builder?.ToString() ?? string.Empty;
    }
}
