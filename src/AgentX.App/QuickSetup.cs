using System.Diagnostics;

namespace AgentX.App;

/// <summary>
/// Quick Setup helper: generated project name, new folder, optional local
/// `git init`, ready to register through the existing Core path
/// (<see cref="AgentX.Core.WorkspaceRegistry.GetOrRegister"/> +
/// <see cref="AgentX.Core.PiWorkerManager.CreateWorker"/>). No engine, no
/// protocol, no mock session — just filesystem + git, so both Add routes
/// converge on the same real Pi session experience.
/// </summary>
public static class QuickSetup
{
    private static readonly string[] Adjectives =
        ["harbor", "signal", "meadow", "ember", "willow", "cobalt", "juniper", "otter"];

    private static readonly string[] Nouns =
        ["desk", "garden", "ledger", "beacon", "atelier", "station", "notebook", "workshop"];

    /// <summary>Generate a filesystem-safe project name like "harbor-desk-42".</summary>
    public static string GenerateName(Random? rng = null)
    {
        rng ??= Random.Shared;
        var adj = Adjectives[rng.Next(Adjectives.Length)];
        var noun = Nouns[rng.Next(Nouns.Length)];
        var suffix = rng.Next(10, 100);
        return $"{adj}-{noun}-{suffix}";
    }

    /// <summary>Validate a Quick Setup name (letters, digits, dash only, 1–64 chars).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return false;
        }

        return name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');
    }

    /// <summary>Create the project folder; throws with typed IO messages on failure.</summary>
    public static string EnsureProjectFolder(string parentDir, string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException($"'{name}' is not a valid project name (letters, digits, dash, underscore; max 64).", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(parentDir) || !Directory.Exists(parentDir))
        {
            throw new DirectoryNotFoundException($"Parent folder does not exist: {parentDir}");
        }

        var path = Path.Combine(parentDir, name);
        if (Directory.Exists(path))
        {
            throw new IOException($"Folder already exists: {path} — pick another name or use Existing Project.");
        }

        return Directory.CreateDirectory(path).FullName;
    }

    public sealed record GitInitResult(bool Created, string Output);

    /// <summary>
    /// Run `git init` in the new folder when git is available. Never throws
    /// for a missing git: returns Created=false with the honest reason so
    /// the UI can state "folder ready, git unavailable" instead of failing.
    /// </summary>
    public static async Task<GitInitResult> TryGitInitAsync(string projectDir, CancellationToken cancellationToken = default)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init",
                WorkingDirectory = projectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                return new GitInitResult(true, stdout.Trim());
            }

            return new GitInitResult(false, string.IsNullOrWhiteSpace(stderr) ? $"git init exited {proc.ExitCode}" : stderr.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            return new GitInitResult(false, $"git unavailable: {ex.Message}");
        }
    }

    /// <summary>Default parent for Quick Setup folders (user's Documents/AgentX Projects).</summary>
    public static string DefaultParent()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AgentX Projects");
}
