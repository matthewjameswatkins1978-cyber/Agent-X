using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// Beautiful Feed appearance data: parse/display round-trips, bundled font
/// sources, and size rules. Rendering itself is evidenced by screenshots
/// (font loading can only be proven by a real Avalonia run); these tests
/// pin the contracts the renderer depends on.
/// </summary>
public sealed class AxFeedTests
{
    [Theory]
    [InlineData("Studio", AxFeedAppearance.Studio)]
    [InlineData("Operator", AxFeedAppearance.Operator)]
    [InlineData(null, AxFeedAppearance.Studio)]
    [InlineData("", AxFeedAppearance.Studio)]
    [InlineData("operator", AxFeedAppearance.Studio)] // unknown falls back, never throws
    [InlineData("Pi Faithful", AxFeedAppearance.Studio)]
    public void Parse_MapsNamesWithStudioFallback(string? name, AxFeedAppearance expected)
    {
        Assert.Equal(expected, AxFeed.Parse(name));
    }

    [Theory]
    [InlineData(AxFeedAppearance.Studio, "Studio")]
    [InlineData(AxFeedAppearance.Operator, "Operator")]
    public void DisplayName_RoundTrips(AxFeedAppearance appearance, string expected)
    {
        Assert.Equal(expected, AxFeed.DisplayName(appearance));
        Assert.Equal(appearance, AxFeed.Parse(AxFeed.DisplayName(appearance)));
    }

    [Fact]
    public void Names_CoversBothAppearances()
    {
        Assert.Equal(["Studio", "Operator"], AxFeed.Names);
    }

    [Fact]
    public void Chrome_StaysGeistInBothAppearances()
    {
        Assert.Equal("Geist", AxFeed.ChromeFamily);
        Assert.Contains("Geist#Geist", AxFeed.ChromeSource);
    }

    [Fact]
    public void Studio_PairsGeistSansWithGeistMono()
    {
        Assert.Equal("Geist", AxFeed.ProseFamily(AxFeedAppearance.Studio));
        Assert.Equal("Geist Mono", AxFeed.MonoFamily(AxFeedAppearance.Studio));
        Assert.Equal("Geist", AxFeed.VoiceFamily(AxFeedAppearance.Studio));
    }

    [Fact]
    public void Operator_SetsWholeFeedInNeonWithArgonVoice()
    {
        Assert.Equal("Monaspace Neon", AxFeed.ProseFamily(AxFeedAppearance.Operator));
        Assert.Equal("Monaspace Neon", AxFeed.MonoFamily(AxFeedAppearance.Operator));
        Assert.Equal("Monaspace Argon", AxFeed.VoiceFamily(AxFeedAppearance.Operator));
    }

    [Theory]
    [InlineData(AxFeedAppearance.Studio)]
    [InlineData(AxFeedAppearance.Operator)]
    public void Sources_PointAtBundledFolders(AxFeedAppearance appearance)
    {
        Assert.StartsWith("avares://AgentX.App/Assets/Fonts/", AxFeed.ProseSource(appearance));
        Assert.StartsWith("avares://AgentX.App/Assets/Fonts/", AxFeed.MonoSource(appearance));
        Assert.StartsWith("avares://AgentX.App/Assets/Fonts/", AxFeed.VoiceSource(appearance));
        Assert.Contains($"#{AxFeed.ProseFamily(appearance)}", AxFeed.ProseSource(appearance));
    }

    [Theory]
    [InlineData(12.9, 13)]
    [InlineData(13, 13)]
    [InlineData(14, 14)]
    [InlineData(16, 16)]
    [InlineData(16.1, 16)]
    [InlineData(99, 16)]
    public void ClampSize_KeepsSizesInRange(double input, double expected)
    {
        Assert.Equal(expected, AxFeed.ClampSize(input));
    }

    [Fact]
    public void ClampSize_NaNFallsBackToDefault()
    {
        Assert.Equal(AxFeed.DefaultSize, AxFeed.ClampSize(double.NaN));
    }

    [Fact]
    public void MonoSize_SitsOneStepUnderProse()
    {
        Assert.Equal(13, AxFeed.MonoSize(14));
        Assert.Equal(12, AxFeed.MonoSize(13));
        Assert.Equal(15, AxFeed.MonoSize(16));
    }

    [Fact]
    public void LineHeight_GivesComfortableRhythm()
    {
        Assert.Equal(21, AxFeed.LineHeight(14));
    }
}
