using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>Fence highlighting: language routing, token kinds, diff lines,
/// error lines, and conservative fallbacks.</summary>
public sealed class CodeHighlightTests
{
    [Theory]
    [InlineData("powershell", "powershell")]
    [InlineData("ps1", "powershell")]
    [InlineData("c#", "csharp")]
    [InlineData("py", "python")]
    [InlineData("js", "javascript")]
    [InlineData("ts", "typescript")]
    [InlineData("sh", "shell")]
    [InlineData("patch", "diff")]
    [InlineData("brainfuck", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Normalize_RoutesAliasesAndRefusesUnknown(string? tag, string? expected)
    {
        Assert.Equal(expected, CodeHighlight.Normalize(tag));
    }

    [Fact]
    public void Highlight_PowerShell_DifferentiatesTokens()
    {
        var tokens = CodeHighlight.Highlight("if ($x) { Write-Host 'hi' # comment\n  $n = 42 }", "powershell");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "if");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && t.Text == "'hi'");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Comment && t.Text.StartsWith("#"));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Number && t.Text == "42");
    }

    [Fact]
    public void Highlight_PowerShellKeywords_CaseInsensitive()
    {
        var tokens = CodeHighlight.Highlight("IF ($x) { RETURN 1 }", "powershell");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "IF");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "RETURN");
    }

    [Fact]
    public void Highlight_CSharp_BlockCommentSpansLines()
    {
        var tokens = CodeHighlight.Highlight("/* open\nstill comment */ var x = \"s\";", "csharp");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Comment && t.Text == "/* open");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Comment && t.Text == "still comment */");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "var");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && t.Text == "\"s\"");
    }

    [Fact]
    public void Highlight_Json_LiteralsAndNoComments()
    {
        var tokens = CodeHighlight.Highlight("{\"a\": true, // kept literal\n \"n\": 12}", "json");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Keyword && t.Text == "true");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Number && t.Text == "12");
        Assert.DoesNotContain(tokens, t => t.Kind == CodeTokenKind.Comment);
    }

    [Fact]
    public void Highlight_UnterminatedString_RunsToEndOfLineOnly()
    {
        var tokens = CodeHighlight.Highlight("\"open\nnext \"closed\"", "python");
        var first = tokens.First(t => t.Kind == CodeTokenKind.String);
        Assert.Equal("\"open", first.Text);
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.String && t.Text == "\"closed\"");
    }

    [Fact]
    public void Highlight_Diff_ClassifiesHunks()
    {
        var tokens = CodeHighlight.Highlight("@@ -1 +1 @@\n+added\n-removed\n context", "diff");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.DiffHunk && t.Text.StartsWith("@@"));
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.DiffAdd && t.Text == "+added");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.DiffDel && t.Text == "-removed");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Plain && t.Text == " context");
    }

    [Fact]
    public void Highlight_ErrorLines_FlaggedAtLineStartOnly()
    {
        var tokens = CodeHighlight.Highlight("Error: boom\nmentioning errors stays plain", "powershell");
        Assert.Contains(tokens, t => t.Kind == CodeTokenKind.Error && t.Text == "Error: boom");
        Assert.DoesNotContain(tokens, t => t.Kind == CodeTokenKind.Error && t.Text.Contains("mentioning"));
    }

    [Fact]
    public void Highlight_PreservesAllSourceChars()
    {
        // Display formatting never mutates the source: rejoining the token
        // texts reproduces the fence input exactly.
        const string code = "if ($x) { # c\n  Write-Host \"s\" 42 }\n+diff?";
        foreach (var lang in new[] { "powershell", "csharp", "python", "javascript", "json", "shell", "diff" })
        {
            var joined = string.Concat(CodeHighlight.Highlight(code, lang).Select(t => t.Text));
            Assert.Equal(code + "\n", joined);
        }
    }
}
