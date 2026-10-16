using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>Pi-first Core vertical slice: open-with-Git-state + fresh-project creation.</summary>
public sealed class ProjectLifecycleTests
{
    private static string TempRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-lc-test-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public void ReadState_NonRepo_ReturnsNotRepo_WithoutThrowing()
    {
        var root = TempRoot("nonrepo");
        try
        {
            var project = Path.Combine(root, "plain");
            Directory.CreateDirectory(project);
            var state = ProjectGit.ReadState(project);
            Assert.False(state.IsGitRepo);
            Assert.Null(state.Branch);
            Assert.Null(state.HeadSha);
            Assert.False(state.IsDirty);
            Assert.Equal(AgentXWorkspace.CanonicalizeProjectRoot(project), state.ProjectRoot);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadState_MissingDirectory_ThrowsLoudly()
    {
        var root = TempRoot("missing");
        try
        {
            Assert.Throws<ArgumentException>(
                () => ProjectGit.ReadState(Path.Combine(root, "does-not-exist")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadState_GitRepo_ReportsBranchHeadAndDirt()
    {
        var root = TempRoot("repo");
        try
        {
            var project = NewProject.Create(root, "lifecycle-repo");
            var before = ProjectGit.ReadState(project);
            Assert.True(before.IsGitRepo);
            Assert.Null(before.HeadSha); // Unborn HEAD: no commit yet.
            Assert.False(before.IsDirty); // Fresh init: nothing to report.

            File.WriteAllText(Path.Combine(project, "notes.txt"), "hello");
            var dirty = ProjectGit.ReadState(project);
            Assert.True(dirty.IsGitRepo);
            Assert.True(dirty.IsDirty); // Untracked file counts as dirty.
            Assert.Equal(project, dirty.ProjectRoot);
            Assert.NotNull(dirty.TopLevel);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseBranch_MatchesShellSemantics()
    {
        Assert.Equal("main", ProjectGit.ParseBranch("main\n"));
        Assert.Equal("feature/pi-first-core", ProjectGit.ParseBranch("feature/pi-first-core"));
        Assert.Null(ProjectGit.ParseBranch("HEAD\n")); // Detached or unborn.
        Assert.Null(ProjectGit.ParseBranch("HEAD"));
        Assert.Null(ProjectGit.ParseBranch(null));
        Assert.Null(ProjectGit.ParseBranch("   "));
    }

    [Fact]
    public void Create_GeneratesName_InitializesGit_NoRemoteNoCommit()
    {
        var root = TempRoot("create");
        try
        {
            var project = NewProject.Create(root);
            Assert.True(Directory.Exists(Path.Combine(project, ".git")));
            Assert.StartsWith("ax-proj-", Path.GetFileName(project), StringComparison.Ordinal);

            // No remote was added.
            var remotesDir = Path.Combine(project, ".git", "refs", "remotes");
            Assert.False(Directory.Exists(remotesDir) && Directory.GetFileSystemEntries(remotesDir).Length > 0);

            // No commit was created: HEAD is unborn.
            var state = ProjectGit.ReadState(project);
            Assert.True(state.IsGitRepo);
            Assert.Null(state.HeadSha);

            // And the workspace identity binds the new root as Pi's cwd.
            var workspace = AgentXWorkspace.ForProject(root, project);
            Assert.Equal(project, workspace.WorkingDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_NameHint_Sanitized_UniqueOnCollision()
    {
        var root = TempRoot("hint");
        try
        {
            var first = NewProject.Create(root, "My Demo Project!");
            Assert.EndsWith("my-demo-project", first, StringComparison.OrdinalIgnoreCase);
            var second = NewProject.Create(root, "My Demo Project!");
            Assert.NotEqual(first, second);
            Assert.StartsWith(first + "-", second, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Create_MissingParent_Throws_WithoutCreating()
    {
        var root = TempRoot("noparent");
        try
        {
            var parent = Path.Combine(root, "no-such-parent");
            Assert.Throws<DirectoryNotFoundException>(() => NewProject.Create(parent));
            Assert.False(Directory.Exists(parent));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SanitizeName_RejectsUnusableHints()
    {
        Assert.Null(NewProject.SanitizeName(null));
        Assert.Null(NewProject.SanitizeName("  "));
        Assert.Null(NewProject.SanitizeName("///"));
        Assert.Null(NewProject.SanitizeName("x")); // Too short to be a useful name.
        Assert.Equal("my-demo-project", NewProject.SanitizeName("My Demo Project!"));
    }
}
