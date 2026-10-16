using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>Slice 2: project-setup facts are honest and headless. No Pi involved.</summary>
public sealed class ProjectSetupTests
{
    [Fact]
    public void Describe_NonRepo_StatesItPlainly()
    {
        var git = new ProjectGitState("/tmp/x", false, null, null, null, false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("Not a Git repository", note, StringComparison.Ordinal);
        Assert.Contains("Nothing will be initialised", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_CleanRepo_NamesBranch()
    {
        var git = new ProjectGitState("/r", true, "/r", "main", new string('a', 40), false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("main", note, StringComparison.Ordinal);
        Assert.Contains("clean", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DirtyDetached_NamesBoth()
    {
        var git = new ProjectGitState("/r", true, "/r", null, new string('b', 40), true, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("detached HEAD", note, StringComparison.Ordinal);
        Assert.Contains("uncommitted changes", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_NestedFolder_NamesRepositoryRoot()
    {
        var git = new ProjectGitState("/r/sub", true, "/r", "dev", new string('c', 40), false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("Nested folder", note, StringComparison.Ordinal);
        Assert.Contains("/r", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_Instructions_Named()
    {
        var git = new ProjectGitState("/r", true, "/r", "main", null, false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, ["AGENTS.md"]);

        Assert.Contains("AGENTS.md", note, StringComparison.Ordinal);
        Assert.Contains("natively", note, StringComparison.Ordinal);
    }

    [Fact]
    public void InstructionFiles_FindsPresentOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ax-instr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "AGENTS.md"), "# rules");
            var found = ProjectSetupFacts.InstructionFiles(dir);
            Assert.Equal(["AGENTS.md"], found);
            Assert.Empty(ProjectSetupFacts.InstructionFiles(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FindWorkerIdByRoot_MatchesCanonically()
    {
        var workers = new[] { ("w1", @"C:\Code\Proj"), ("w2", @"C:\Code\Other") };

        Assert.Equal("w1", ProjectSetupFacts.FindWorkerIdByRoot(workers, @"c:\code\proj\"));
        Assert.Null(ProjectSetupFacts.FindWorkerIdByRoot(workers, @"C:\Code\Elsewhere"));
        Assert.Null(ProjectSetupFacts.FindWorkerIdByRoot(workers, ""));
    }

    [Fact]
    public void Describe_UnbornHead_IsNotDetached()
    {
        var git = new ProjectGitState("/r", true, "/r", null, null, false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("unborn HEAD", note, StringComparison.Ordinal);
        Assert.DoesNotContain("detached", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Describe_DetachedWithSha_NamesDetached()
    {
        var git = new ProjectGitState("/r", true, "/r", null, new string('d', 40), false, DateTime.UtcNow);

        var note = ProjectSetupFacts.Describe(git, []);

        Assert.Contains("detached HEAD", note, StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestName_IsMemorableAndUsableAsHint()
    {
        var rng = new Random(7);
        var parent = Path.Combine(Path.GetTempPath(), "ax-suggest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                var hint = ProjectSetupFacts.SuggestName(rng);
                Assert.Matches("^[a-z]+-[a-z]+$", hint);
                var created = NewProject.Create(parent, hint);
                Assert.True(Directory.Exists(Path.Combine(created, ".git")));
            }
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void DescribeSetupFailure_NamesParentAndRecovery()
    {
        var note = ProjectSetupFacts.DescribeSetupFailure("/parent", "my hint", new InvalidOperationException("git gone"));

        Assert.Contains("/parent", note, StringComparison.Ordinal);
        Assert.Contains("my hint", note, StringComparison.Ordinal);
        Assert.Contains("nothing registered", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("leftover", note, StringComparison.OrdinalIgnoreCase);
    }
}
