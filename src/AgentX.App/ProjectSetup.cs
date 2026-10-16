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

    private static readonly string[] NameAdjectives =
        ["harbor", "signal", "meadow", "ember", "willow", "cobalt", "juniper", "otter"];

    private static readonly string[] NameNouns =
        ["desk", "garden", "ledger", "beacon", "atelier", "station", "notebook", "workshop"];

    /// <summary>
    /// Memorable name hint for Quick Setup (presentation default only).
    /// Core <see cref="NewProject.Create"/> still owns sanitizing,
    /// uniqueness (-2, -3, …) and creation — this never touches the disk.
    /// </summary>
    public static string SuggestName(Random? rng = null)
    {
        rng ??= Random.Shared;
        return $"{NameAdjectives[rng.Next(NameAdjectives.Length)]}-{NameNouns[rng.Next(NameNouns.Length)]}";
    }

    /// <summary>
    /// Honest Quick Setup failure note: names the tried parent/hint and
    /// warns a leftover folder may remain for inspection (Core never
    /// deletes user data on failure) with safe recovery directions.
    /// </summary>
    public static string DescribeSetupFailure(string parentDirectory, string? nameHint, Exception ex)
    {
        var tried = string.IsNullOrWhiteSpace(nameHint)
            ? $"under {parentDirectory}"
            : $"'{nameHint.Trim()}' under {parentDirectory}";
        return $"Quick setup failed for {tried} — nothing registered ({ex.Message}). A leftover folder may remain; delete it or retry with a different name.";
    }

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
            var branch = git.Branch
                ?? (git.HeadSha is null ? "unborn HEAD (no commits yet)" : "detached HEAD");
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
