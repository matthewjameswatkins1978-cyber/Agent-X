using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>Quick Setup helper: name validity, folder creation, git-init honesty. No Pi involved.</summary>
public sealed class QuickSetupTests
{
    [Fact]
    public void GenerateName_IsValid()
    {
        var rng = new Random(42);
        for (var i = 0; i < 25; i++)
        {
            Assert.True(QuickSetup.IsValidName(QuickSetup.GenerateName(rng)));
        }
    }

    [Fact]
    public void InvalidNames_Rejected()
    {
        Assert.False(QuickSetup.IsValidName(null));
        Assert.False(QuickSetup.IsValidName(""));
        Assert.False(QuickSetup.IsValidName("has space"));
        Assert.False(QuickSetup.IsValidName("has/slash"));
        Assert.False(QuickSetup.IsValidName(new string('a', 65)));
        Assert.True(QuickSetup.IsValidName("harbor-desk-42"));
    }

    [Fact]
    public void EnsureProjectFolder_CreatesAndRefusesDuplicates()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ax-quick-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            var path = QuickSetup.EnsureProjectFolder(parent, "harbor-desk-42");
            Assert.True(Directory.Exists(path));
            Assert.Throws<IOException>(() => QuickSetup.EnsureProjectFolder(parent, "harbor-desk-42"));
            Assert.Throws<ArgumentException>(() => QuickSetup.EnsureProjectFolder(parent, "bad name!"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task TryGitInit_ReturnsHonestResult()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ax-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var result = await QuickSetup.TryGitInitAsync(dir);
            // Either git ran or the absence is reported — never throws, never lies.
            Assert.False(string.IsNullOrWhiteSpace(result.Output));
            if (result.Created)
            {
                Assert.True(Directory.Exists(Path.Combine(dir, ".git")));
            }
            else
            {
                Assert.Contains("git", result.Output, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
