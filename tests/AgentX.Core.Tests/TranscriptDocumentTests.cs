using System.Collections;
using System.Text;
using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class TranscriptDocumentTests
{
    [Fact]
    public void PreservesExactTextAndIndexesCrLfAndFinalNewline()
    {
        const string input = "first\r\n\r\nthird\r\n";
        var result = TranscriptImporter.Import(new TranscriptImportRequest(input));

        Assert.Equal(input, result.Document.Text);
        Assert.Equal(3, result.LineCount);
        Assert.Equal(new[] { "first", "", "third" }, result.Document.Lines.Select(line => line.Text));
        Assert.Equal(Encoding.UTF8.GetByteCount(input), result.Utf8ByteCount);
    }

    [Fact]
    public void SupportsLfCrOnlyAndEmptyInput()
    {
        Assert.Equal(new[] { "a", "b", "c" }, TranscriptDocument.FromText("a\nb\rc").Lines.Select(line => line.Text));
        Assert.Empty(TranscriptDocument.FromText(string.Empty).Lines);
        Assert.Equal(string.Empty, Assert.Single(TranscriptDocument.FromText("\r\n").Lines).Text);
    }

    [Fact]
    public void KeepsTheLineViewLazyAndSupportsRandomAccess()
    {
        const int count = 100_000;
        var input = string.Join('\n', Enumerable.Range(0, count).Select(i => $"event-{i:D6}"));
        var document = TranscriptDocument.FromText(input);

        Assert.Equal(count, document.LineCount);
        Assert.Equal(1, document.Lines[0].Number);
        Assert.Equal("event-050000", document.Lines[50_000].Text);
        Assert.Equal(50_000, ((IList)document.Lines).IndexOf(document.Lines[50_000]));
        Assert.Equal($"event-{count - 1:D6}", document.Lines[count - 1].Text);
        Assert.Equal(input, document.Text);
    }
}