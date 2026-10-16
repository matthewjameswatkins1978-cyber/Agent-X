using System.Diagnostics;

namespace AgentX.Core;

/// <summary>
/// Observed Git state of one project root. UI-free; the host renders it.
/// <see cref="IsGitRepo"/> false means the directory is not inside a Git
/// work tree — every other field is then null/false and nothing throws.
/// <see cref="StatusAvailable"/> false means the repo was confirmed but
/// <c>status</c> could not be read: <see cref="IsDirty"/> is then
/// meaningless and hosts must report unavailable, never clean.
/// </summary>
public sealed record ProjectGitState(
    string ProjectRoot,
    bool IsGitRepo,
    string? TopLevel,
    string? Branch,
    string? HeadSha,
    bool IsDirty,
    bool StatusAvailable,
    DateTime CheckedUtc);

/// <summary>
/// Core Git discovery for an existing project folder: canonical root plus
/// branch/HEAD/dirty observed via the native <c>git</c> CLI. No LibGit2Sharp,
/// no new dependency — the same <c>git -C</c> invocations the desktop shell
/// already uses, moved to a UI-free contract Agent TWO can bind directly.
/// Fail-closed: a non-repo yields <c>IsGitRepo: false</c>; a missing git
/// binary or timeout throws loudly instead of guessing.
/// </summary>
public static class ProjectGit
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Read the Git state of an existing project directory. Throws
    /// <see cref="ArgumentException"/> when the directory does not exist —
    /// a missing project must fail before any Pi launch, never silently run
    /// elsewhere (same rule as <see cref="AgentXWorkspace.EnsureDirectories"/>).
    /// </summary>
    public static ProjectGitState ReadState(string projectRoot, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var canonical = AgentXWorkspace.CanonicalizeProjectRoot(projectRoot);
        if (!Directory.Exists(canonical))
        {
            throw new ArgumentException($"Project directory does not exist: {projectRoot}", nameof(projectRoot));
        }

        var limit = timeout ?? DefaultTimeout;
        var top = RunGit(canonical, "rev-parse --show-toplevel", limit);
        if (top is null)
        {
            return new ProjectGitState(canonical, false, null, null, null, false, false, DateTime.UtcNow);
        }

        var branchRaw = RunGit(canonical, "rev-parse --abbrev-ref HEAD", limit);
        var headRaw = RunGit(canonical, "rev-parse HEAD", limit);
        var statusRaw = RunGit(canonical, "status --porcelain", limit);
        // A failed status query is unavailable, never clean: IsDirty below
        // is only meaningful when statusAvailable holds.
        var statusAvailable = statusRaw is not null;
        var dirty = statusRaw is not null && statusRaw.Length > 0;
        return new ProjectGitState(
            canonical,
            true,
            NormalizePath(top),
            ParseBranch(branchRaw),
            NormalizeSha(headRaw),
            dirty,
            statusAvailable,
            DateTime.UtcNow);
    }

    /// <summary>
    /// Parse <c>git rev-parse --abbrev-ref HEAD</c> output: a detached or
    /// unborn HEAD prints <c>HEAD</c>, which carries no branch name.
    /// Same semantics as the desktop shell parser (kept in sync by test).
    /// </summary>
    public static string? ParseBranch(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var branch = output.Trim();
        return branch.Length == 0 || branch == "HEAD" ? null : branch;
    }

    /// <summary>
    /// The single branch-label interpretation every host uses (notes,
    /// tooltips, footer): named branch, unborn HEAD (confirmed repo with
    /// no commits), or detached HEAD. GUI layers must not re-derive this.
    /// </summary>
    public static string DescribeBranch(ProjectGitState git)
        => git.Branch
            ?? (git.HeadSha is null ? "unborn HEAD (no commits yet)" : "detached HEAD");

    /// <summary>
    /// The single working-tree status label every host uses: confirmed
    /// clean, confirmed dirty, or unavailable when the status query failed.
    /// A failed query is never clean — <see cref="ProjectGitState.IsDirty"/>
    /// is meaningless unless <see cref="ProjectGitState.StatusAvailable"/>.
    /// </summary>
    public static string DescribeStatus(ProjectGitState git)
        => !git.StatusAvailable
            ? "status unavailable"
            : git.IsDirty ? "uncommitted changes" : "clean";

    private static string? NormalizePath(string? output)
        => string.IsNullOrWhiteSpace(output) ? null : output.Trim();

    private static string? NormalizeSha(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var sha = output.Trim();
        return sha.Length is 40 or 64 && sha.All(c => Uri.IsHexDigit(c)) ? sha : null;
    }

    /// <summary>Run one git command; null when git reports failure (not-a-repo, unborn HEAD, ...). Stdout/stderr drain asynchronously while the process runs, so a large output (e.g. big status porcelain) can never fill the pipe and deadlock the wait. Bounded by the timeout with kill + throw.</summary>
    private static string? RunGit(string workingDirectory, string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"-C \"{workingDirectory}\" {arguments}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start 'git' — is Git installed and on PATH?");
            }

            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, a) => { if (a.Data is not null) { output.AppendLine(a.Data); } };
            process.ErrorDataReceived += (_, a) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new TimeoutException($"'git {arguments}' timed out after {timeout.TotalSeconds:0}s in {workingDirectory}.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output.ToString().TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException("Failed to start 'git' — is Git installed and on PATH?", ex);
        }
    }
}

/// <summary>
/// Fresh local project creation: a generated folder name plus <c>git init</c>,
/// nothing else. No remote is added and no commit is created — the new HEAD
/// is unborn until the user (or Pi, under the user's authority in the project
/// root) writes and commits. Registration with <see cref="WorkspaceRegistry"/>
/// stays with the caller so creation and session binding never fuse silently.
/// </summary>
public static class NewProject
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Generated folder name: lowercase, filesystem-safe, time-ordered with a random tail.</summary>
    public static string GenerateName()
        => $"ax-proj-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";

    /// <summary>
    /// Create a fresh project directory under an existing parent and run
    /// <c>git init</c> inside it. <paramref name="nameHint"/> may supply a
    /// readable base (sanitized to [a-z0-9-]); null/blank yields a generated
    /// name. Collisions append <c>-2</c>, <c>-3</c>, … Returns the canonical
    /// project root. Throws loudly when the parent is missing or git fails;
    /// a failed directory is left in place for inspection, never deleted.
    /// </summary>
    public static string Create(string parentDirectory, string? nameHint = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var parent = Path.GetFullPath(parentDirectory.Trim());
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException($"Parent directory does not exist: {parentDirectory} — refusing to create it implicitly.");
        }

        var dir = ResolveUniqueDirectory(parent, nameHint);
        Directory.CreateDirectory(dir);
        try
        {
            RunGitInit(dir, timeout ?? DefaultTimeout);
        }
        catch
        {
            // Never delete user data on failure; the empty dir + message is the evidence.
            throw;
        }

        return AgentXWorkspace.CanonicalizeProjectRoot(dir);
    }

    private static string ResolveUniqueDirectory(string parent, string? nameHint)
    {
        var baseName = SanitizeName(nameHint) ?? GenerateName();
        var candidate = Path.Combine(parent, baseName);
        for (var i = 2; i < 1000 && (Directory.Exists(candidate) || File.Exists(candidate)); i++)
        {
            candidate = Path.Combine(parent, $"{baseName}-{i}");
        }

        if (Directory.Exists(candidate) || File.Exists(candidate))
        {
            // Pathological collision storm: fall back to a fresh generated name once.
            candidate = Path.Combine(parent, GenerateName());
            if (Directory.Exists(candidate) || File.Exists(candidate))
            {
                throw new IOException($"Could not find a free project name under '{parent}'.");
            }
        }

        return candidate;
    }

    internal static string? SanitizeName(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return null;
        }

        var lowered = hint.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        var lastWasDash = true;
        foreach (var c in lowered)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length is >= 2 and <= 64 && char.IsLetterOrDigit(slug[0]) && char.IsLetterOrDigit(slug[^1])
            ? slug
            : null;
    }

    private static void RunGitInit(string directory, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "init",
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start 'git init' — is Git installed and on PATH?");
            }

            var output = new System.Text.StringBuilder();
            var error = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, a) => { if (a.Data is not null) { output.AppendLine(a.Data); } };
            process.ErrorDataReceived += (_, a) => { if (a.Data is not null) { error.AppendLine(a.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                throw new TimeoutException($"'git init' timed out after {timeout.TotalSeconds:0}s in {directory}.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(Path.Combine(directory, ".git")))
            {
                throw new InvalidOperationException($"'git init' failed in {directory}: {output.ToString().Trim()} {error.ToString().Trim()}".Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException("Failed to start 'git init' — is Git installed and on PATH?", ex);
        }
    }
}
