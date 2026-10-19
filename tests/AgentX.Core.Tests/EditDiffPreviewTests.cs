using System.Text.Json;
using AgentX.Core;

namespace AgentX.Core.Tests;

public sealed class EditDiffPreviewTests
{
    private static JsonElement? Args(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EditTwoHunks_ChangedLinesOnly()
    {
        var args = Args("""{"path":"a.cs","edits":[{"oldText":"a\nb","newText":"a\nB"},{"oldText":"x","newText":"y"}]}""");
        var diff = PiConversation.EditDiffPreview("edit", args, "a.cs");
        Assert.Equal("--- a.cs\n+++ a.cs\n-b\n+B\n-x\n+y", diff);
    }

    [Fact]
    public void CapRespected_RemainderNoted()
    {
        var args = Args("""{"edits":[{"oldText":"1\n2\n3\n4\n5","newText":""}]}""");
        var diff = PiConversation.EditDiffPreview("edit", args, null, maxLines: 2);
        Assert.Equal("-1\n-2\n… 3 more lines behind the expander.", diff);
    }

    [Fact]
    public void WriteContent_AdditionsOnly()
    {
        var args = Args("""{"path":"n.txt","content":"l1\nl2"}""");
        Assert.Equal("+l1\n+l2", PiConversation.EditDiffPreview("write", args, null));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("powershell")]
    [InlineData("grep")]
    public void NonEditTools_Null(string tool)
    {
        var args = Args("""{"path":"a.cs"}""");
        Assert.Null(PiConversation.EditDiffPreview(tool, args, "a.cs"));
    }

    [Fact]
    public void NullArgs_Null()
    {
        Assert.Null(PiConversation.EditDiffPreview("edit", null, "a.cs"));
    }

    [Fact]
    public void MalformedEdits_Skipped()
    {
        var args = Args("""{"edits":[{"oldText":"a"},{"nope":1},"x"]}""");
        Assert.Null(PiConversation.EditDiffPreview("edit", args, null));
    }

    [Fact]
    public void EmptyOldText_PureInsertion()
    {
        var args = Args("""{"edits":[{"oldText":"","newText":"n"}]}""");
        Assert.Equal("+n", PiConversation.EditDiffPreview("edit", args, null));
    }
}
