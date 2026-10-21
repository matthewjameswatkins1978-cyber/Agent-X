using AgentX.App;
using Xunit;

public sealed class ComposerPlaceholderTests
{
    [Fact]
    public void For_NoSelection_ReturnsDefault()
    {
        Assert.Equal("Ask Pi anything…", ComposerPlaceholder.For(null, null));
        Assert.Equal(ComposerPlaceholder.Default, ComposerPlaceholder.For("  ", "\t"));
    }

    [Fact]
    public void For_Worker_NamesDestination()
    {
        Assert.Equal("Message Agent ONE…", ComposerPlaceholder.For("Agent ONE", "Main"));
    }

    [Fact]
    public void For_Entry_NamesDestination()
    {
        Assert.Equal("Message Main…", ComposerPlaceholder.For(null, "Main"));
    }

    [Fact]
    public void For_TrimsLabels()
    {
        Assert.Equal("Message demo-proj…", ComposerPlaceholder.For("  demo-proj ", null));
    }
}
