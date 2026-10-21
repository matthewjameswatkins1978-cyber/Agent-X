namespace AgentX.Core;

/// <summary>Token role inside a fenced code block. The view maps these to
/// the existing Pi palette roles — no new hues, no rainbow.</summary>
public enum CodeTokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
    DiffAdd,
    DiffDel,
    DiffHunk,
    Error,
}

/// <summary>One highlighted token of fence content (display only).</summary>
public sealed record CodeToken(string Text, CodeTokenKind Kind);

/// <summary>
/// Minimal language-aware fence highlighting (display only — source bytes
/// untouched). Pi's own CLI emits fences plain, so this stays deliberately
/// conservative: diff/error lines and a small set of mainstream languages
/// (PowerShell, C#, Python, JavaScript/TypeScript, JSON, shell) with tight
/// keyword lists, line/block comments, quoted strings and numbers. Unknown
/// languages fall back to the uniform code treatment. Single-line scanner
/// with block-comment state carried across lines; unterminated strings run
/// to end of line rather than swallowing the fence. Pure.
/// </summary>
public static class CodeHighlight
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ps1"] = "powershell", ["psm1"] = "powershell", ["pwsh"] = "powershell",
        ["cs"] = "csharp", ["c#"] = "csharp",
        ["py"] = "python",
        ["js"] = "javascript", ["ts"] = "typescript", ["tsx"] = "typescript", ["jsx"] = "javascript",
        ["sh"] = "shell", ["bash"] = "shell", ["zsh"] = "shell", ["shellscript"] = "shell",
        ["jsonc"] = "json", ["patch"] = "diff",
    };

    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "csharp", "python", "javascript", "typescript", "json", "shell", "diff",
    };

    /// Normalize a fence language tag; null when the highlighter has no
    /// language-aware treatment (the view keeps the uniform code style).
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var tag = language.Trim();
        if (Aliases.TryGetValue(tag, out var aliased))
        {
            return aliased;
        }

        return Known.Contains(tag) ? tag.ToLowerInvariant() : null;
    }

    public static IReadOnlyList<CodeToken> Highlight(string code, string normalizedLanguage)
    {
        var tokens = new List<CodeToken>();
        if (string.IsNullOrEmpty(code))
        {
            return tokens;
        }

        if (normalizedLanguage == "diff")
        {
            foreach (var line in code.Split('\n'))
            {
                var stripped = line.TrimEnd('\r');
                var kind = stripped switch
                {
                    _ when stripped.StartsWith('+') && !stripped.StartsWith("+++") => CodeTokenKind.DiffAdd,
                    _ when stripped.StartsWith('-') && !stripped.StartsWith("---") => CodeTokenKind.DiffDel,
                    _ when stripped.StartsWith('@') => CodeTokenKind.DiffHunk,
                    _ when stripped.StartsWith("diff ", StringComparison.Ordinal)
                        || stripped.StartsWith("+++", StringComparison.Ordinal)
                        || stripped.StartsWith("---", StringComparison.Ordinal)
                        || stripped.StartsWith("index ", StringComparison.Ordinal) => CodeTokenKind.DiffHunk,
                    _ => CodeTokenKind.Plain,
                };
                tokens.Add(new CodeToken(stripped, kind));
                tokens.Add(new CodeToken("\n", CodeTokenKind.Plain));
            }

            return tokens;
        }

        var keywords = KeywordsFor(normalizedLanguage);
        var ignoreCase = normalizedLanguage == "powershell";
        var lineComment = normalizedLanguage switch
        {
            "csharp" or "javascript" or "typescript" => "//",
            "json" => null, // JSON has no comments; '#' and '//' stay literal.
            _ => "#",
        };
        var blockComment = normalizedLanguage is "csharp" or "javascript" or "typescript";
        var inBlock = false;

        foreach (var rawLine in code.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (IsErrorLine(line))
            {
                tokens.Add(new CodeToken(line, CodeTokenKind.Error));
                tokens.Add(new CodeToken("\n", CodeTokenKind.Plain));
                continue;
            }

            var i = 0;
            var plain = new System.Text.StringBuilder();
            void FlushPlain()
            {
                if (plain.Length > 0)
                {
                    tokens.Add(new CodeToken(plain.ToString(), CodeTokenKind.Plain));
                    plain.Clear();
                }
            }

            while (i < line.Length)
            {
                if (inBlock)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        tokens.Add(new CodeToken(line[i..], CodeTokenKind.Comment));
                        i = line.Length;
                        break;
                    }

                    tokens.Add(new CodeToken(line[i..(end + 2)], CodeTokenKind.Comment));
                    i = end + 2;
                    inBlock = false;
                    continue;
                }

                var c = line[i];
                if (char.IsWhiteSpace(c))
                {
                    plain.Append(c);
                    i++;
                    continue;
                }

                if (lineComment is not null && line.AsSpan(i).StartsWith(lineComment, StringComparison.Ordinal))
                {
                    FlushPlain();
                    tokens.Add(new CodeToken(line[i..], CodeTokenKind.Comment));
                    i = line.Length;
                    break;
                }

                if (blockComment && line.AsSpan(i).StartsWith("/*", StringComparison.Ordinal))
                {
                    FlushPlain();
                    var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        tokens.Add(new CodeToken(line[i..], CodeTokenKind.Comment));
                        i = line.Length;
                        inBlock = true;
                        break;
                    }

                    tokens.Add(new CodeToken(line[i..(end + 2)], CodeTokenKind.Comment));
                    i = end + 2;
                    continue;
                }

                if (c is '\'' or '"' or '`')
                {
                    FlushPlain();
                    var end = ScanString(line, i);
                    tokens.Add(new CodeToken(line[i..end], CodeTokenKind.String));
                    i = end;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
                {
                    FlushPlain();
                    var end = i;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '.' or '_'))
                    {
                        end++;
                    }

                    tokens.Add(new CodeToken(line[i..end], CodeTokenKind.Number));
                    i = end;
                    continue;
                }

                if (char.IsLetter(c) || c is '_' or '$')
                {
                    var end = i;
                    while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '_' or '$' or '.'))
                    {
                        end++;
                    }

                    var word = line[i..end];
                    var key = ignoreCase ? word.ToLowerInvariant() : word;
                    FlushPlain();
                    tokens.Add(new CodeToken(word, keywords.Contains(key) ? CodeTokenKind.Keyword : CodeTokenKind.Plain));
                    i = end;
                    continue;
                }

                plain.Append(c);
                i++;
            }

            FlushPlain();
            tokens.Add(new CodeToken("\n", CodeTokenKind.Plain));
        }

        return tokens;
    }

    /// Narrow error-line shape for output fences: a line LED with an error
    /// word. Deliberately start-anchored so prose mentioning errors never
    /// misfires.
    internal static bool IsErrorLine(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("exception", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("failed:", StringComparison.OrdinalIgnoreCase);
    }

    /// Scan a quoted string from the opening quote; escapes consume the next
    /// char. Unterminated runs to end of line (never swallows the fence).
    private static int ScanString(string line, int start)
    {
        var quote = line[start];
        var i = start + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                i += 2;
                continue;
            }

            // PowerShell single-quoted '' is an embedded quote, not the end.
            if (quote == '\'' && line[i] == '\'' && i + 1 < line.Length && line[i + 1] == '\'')
            {
                i += 2;
                continue;
            }

            if (line[i] == quote)
            {
                return i + 1;
            }

            i++;
        }

        return i;
    }

    private static HashSet<string> KeywordsFor(string language) => language switch
    {
        "powershell" => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "function", "filter", "param", "begin", "process", "end",
            "if", "else", "elseif", "switch", "default",
            "foreach", "for", "while", "do", "until", "in",
            "try", "catch", "finally", "throw", "return",
            "break", "continue", "exit",
        },
        "csharp" => new(StringComparer.Ordinal)
        {
            "abstract", "as", "async", "await", "base", "bool", "break", "byte",
            "case", "catch", "char", "checked", "class", "const", "continue",
            "decimal", "default", "do", "double", "else", "enum", "false",
            "finally", "float", "for", "foreach", "goto", "if", "in", "int",
            "interface", "internal", "is", "lock", "long", "namespace", "new",
            "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "record", "ref",
            "return", "sealed", "short", "sizeof", "stackalloc", "static",
            "string", "struct", "switch", "this", "throw", "true", "try",
            "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
            "using", "var", "virtual", "void", "while",
        },
        "python" => new(StringComparer.Ordinal)
        {
            "and", "as", "assert", "async", "await", "break", "class",
            "continue", "def", "del", "elif", "else", "except", "finally",
            "for", "from", "global", "if", "import", "in", "is", "lambda",
            "nonlocal", "not", "or", "pass", "raise", "return", "try",
            "while", "with", "yield", "None", "True", "False",
        },
        "javascript" or "typescript" => new(StringComparer.Ordinal)
        {
            "async", "await", "break", "case", "catch", "class", "const",
            "continue", "debugger", "default", "do", "else", "export",
            "extends", "finally", "for", "from", "function", "if", "import",
            "in", "instanceof", "let", "new", "of", "return", "static",
            "super", "switch", "this", "throw", "try", "typeof", "var",
            "void", "while", "with", "yield", "true", "false", "null",
            "undefined",
        },
        "json" => new(StringComparer.Ordinal) { "true", "false", "null" },
        "shell" => new(StringComparer.Ordinal)
        {
            "if", "then", "else", "elif", "fi", "for", "while", "until",
            "do", "done", "case", "esac", "function", "return", "exit",
            "in", "select",
        },
        _ => new(StringComparer.Ordinal),
    };
}
