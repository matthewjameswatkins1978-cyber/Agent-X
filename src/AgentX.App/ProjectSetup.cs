using AgentX.Core;

namespace AgentX.App;

/// <summary>
/// UI-free project-setup policy for the Add flows. All Git/process truth
/// comes from Core (<see cref="ProjectGit"/>, <see cref="NewProject"/>);
/// this class only formats honest notes and matches workers to roots so
/// the GUI binds the contracts without guessing. Fully headless-testable.
/// </summary>
public static class ProjectSetupFacts
{
    /// <summary>Instruction files Pi discovers natively in a project root.</summary>
    public static IReadOnlyList<string> InstructionFiles(string projectRoot)
    {
        var found = new List<string>(2);
        foreach (var name in new[] { "AGENTS.md", "CLAUDE.md" })
        {
            try
            {
                if (File.Exists(Path.Combine(projectRoot, name)))
                {
                    found.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: report nothing rather than fail setup.
            }
        }

        return found;
    }

    /// <summary>Default parent for Quick Setup folders (created on demand, never implicitly elsewhere).</summary>
    public static string DefaultProjectsParent()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgentX Projects");

    /// <summary>
    /// Find the id of an existing worker for a project root (canonical,
    /// case-insensitive); null when none exists and the caller should
    /// create one. Never throws for a bad path — null means "no match".
    /// </summary>
    public static string? FindWorkerIdByRoot(
        IEnumerable<(string Id, string ProjectRoot)> workers, string path)
    {
        string canonical;
        try
        {
            canonical = AgentXWorkspace.CanonicalizeProjectRoot(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }

        foreach (var (id, root) in workers)
        {
            string other;
            try
            {
                other = AgentXWorkspace.CanonicalizeProjectRoot(root);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (string.Equals(canonical, other, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Honest multi-line note for the Add confirmation panel: Git facts
    /// (repo, branch, dirty, nested) plus instruction files. A non-repo is
    /// stated plainly — setup never initialises one silently.
    /// </summary>
    public static string Describe(ProjectGitState git, IReadOnlyList<string> instructionFiles)
    {
        var lines = new List<string>(3);
        if (!git.IsGitRepo)
        {
            lines.Add("Not a Git repository — connecting without Git history. Nothing will be initialised.");
        }
        else
        {
            var branch = git.Branch ?? "detached HEAD";
            var dirty = git.IsDirty ? "uncommitted changes" : "clean";
            lines.Add($"Git: {branch}, {dirty}.");
            if (git.TopLevel is not null
                && !string.Equals(
                    AgentXWorkspace.CanonicalizeProjectRoot(git.TopLevel),
                    AgentXWorkspace.CanonicalizeProjectRoot(git.ProjectRoot),
                    StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"Nested folder inside repository at {git.TopLevel} — Pi runs here, Git root stays above.");
            }
        }

        if (instructionFiles.Count > 0)
        {
            lines.Add($"Project instructions found ({string.Join(", ", instructionFiles)}) — Pi loads them natively.");
        }

        return string.Join(" ", lines);
    }
}
