using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Build-identity formatting: deterministic pure functions plus the
/// generation contract (compiled-in constants agree with each other).</summary>
public sealed class BuildInfoTests
{
    [Fact]
    public void ShortLabel_ShowsVersionAndSha()
    {
        Assert.Equal("Agent X v0.2.0-preview · abc1234", BuildInfo.ShortLabel("0.2.0-preview", "abc1234", false));
    }

    [Fact]
    public void ShortLabel_MarksDirtyTree()
    {
        Assert.Equal("Agent X v0.2.0-preview · abc1234*", BuildInfo.ShortLabel("0.2.0-preview", "abc1234", true));
    }

    [Theory]
    [InlineData("preview", "Debug", "Preview")]
    [InlineData("preview.4", "Release", "Preview")]
    [InlineData("", "Release", "Production")]
    [InlineData("", "Debug", "Development")]
    public void ChannelOf_ClassifiesBuilds(string suffix, string configuration, string expected)
    {
        Assert.Equal(expected, BuildInfo.ChannelOf(suffix, configuration));
    }

    [Fact]
    public void DetailText_CarriesEveryField()
    {
        var text = BuildInfo.DetailText("0.2.0-preview", "Preview", "abc1234full", "feature/x",
            "2026-10-18T12:00:00Z", "Debug", true, "C:\\app\\AgentX.App.exe", 1234);
        Assert.Contains("Agent X v0.2.0-preview (Preview, dirty working copy)", text, StringComparison.Ordinal);
        Assert.Contains("Commit: abc1234full", text, StringComparison.Ordinal);
        Assert.Contains("Branch: feature/x", text, StringComparison.Ordinal);
        Assert.Contains("Built: 2026-10-18T12:00:00Z (Debug)", text, StringComparison.Ordinal);
        Assert.Contains(@"Executable: C:\app\AgentX.App.exe", text, StringComparison.Ordinal);
        Assert.Contains("PID: 1234", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_OmitsDirtyMarkerWhenClean()
    {
        var text = BuildInfo.DetailText("0.2.0", "Production", "abc", "main", "t", "Release", false, "x", 1);
        Assert.DoesNotContain("dirty", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GeneratedIdentity_IsInternallyConsistent()
    {
        Assert.True(Version.TryParse(BuildIdentity.ProductVersion, out _));
        Assert.False(string.IsNullOrWhiteSpace(BuildIdentity.DisplayVersion));
        // Locals (not consts) so neither branch folds away: the relation
        // must hold whatever this build stamped.
        var commit = BuildIdentity.CommitSha;
        var shortSha = BuildIdentity.ShortSha;
        if (commit == "unknown")
        {
            Assert.Equal("unknown", shortSha);
        }
        else
        {
            Assert.Equal(40, commit.Length);
            Assert.Equal(7, shortSha.Length);
            Assert.StartsWith(shortSha, commit, StringComparison.Ordinal);
        }

        Assert.Equal(BuildIdentity.Channel, BuildInfo.ChannelOf(BuildIdentity.PreviewSuffix, BuildIdentity.Configuration));
    }
}
