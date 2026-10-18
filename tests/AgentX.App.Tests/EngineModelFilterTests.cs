using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>Model picker search: Pi-supplied fields only, never invented data.</summary>
public sealed class EngineModelFilterTests
{
    private static readonly PiModelOption Opt = new("qwen-token-plan", "qwen3.8-max", "Qwen Max");

    [Fact]
    public void EmptyQuery_MatchesEverything()
    {
        Assert.True(EngineModelFilter.Matches(Opt, string.Empty));
        Assert.True(EngineModelFilter.Matches(Opt, "   "));
    }

    [Fact]
    public void Matches_ProviderNameAndId_SubstringCaseInsensitive()
    {
        Assert.True(EngineModelFilter.Matches(Opt, "qwen"));
        Assert.True(EngineModelFilter.Matches(Opt, "QWEN-TOKEN"));
        Assert.True(EngineModelFilter.Matches(Opt, "max"));
        Assert.True(EngineModelFilter.Matches(Opt, "3.8"));
        Assert.True(EngineModelFilter.Matches(Opt, "  max  "));
    }

    [Fact]
    public void NoMatch_ReturnsFalse()
    {
        Assert.False(EngineModelFilter.Matches(Opt, "anthropic"));
        Assert.False(EngineModelFilter.Matches(Opt, "zzz"));
    }
}
