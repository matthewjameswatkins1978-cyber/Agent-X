using System.Text.Json;
using AgentX.Core;
using Xunit;

namespace AgentX.Core.Tests;

public sealed class ThinkingExtractionTests
{
    [Fact]
    public void Thinking_Blocks_Inline_As_Quotes_Beside_Answers()
    {
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"assistant","content":[{"type":"thinking","thinking":"first principles first"},{"type":"text","text":"the answer"}]}]}}""");
        var history = PiConversation.ParseHistory(response);
        Assert.Single(history);
        Assert.Equal("> first principles first\nthe answer", history[0].Text);
    }

    [Fact]
    public void Thinking_Text_Field_Shape_Also_Inlines()
    {
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"assistant","content":[{"type":"thinking","text":"pondering"}]}]}}""");
        var history = PiConversation.ParseHistory(response);
        Assert.Single(history);
        Assert.Equal("> pondering\n", history[0].Text);
    }

    [Fact]
    public void Empty_Thinking_Adds_Nothing()
    {
        using var response = JsonDocument.Parse(
            """{"data":{"messages":[{"role":"assistant","content":[{"type":"thinking","signature":"abc"},{"type":"text","text":"hi"}]}]}}""");
        var history = PiConversation.ParseHistory(response);
        Assert.Single(history);
        Assert.Equal("hi", history[0].Text);
    }
}
