using System.Text;
using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// ViewPrefs data-preservation regressions (Lucy B3 P1-2). All file work
/// uses disposable temp directories; failure scenarios are deterministic
/// (literal corrupt bytes, read-only files), never timing-dependent.
/// </summary>
public sealed class ViewPrefsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ax-viewprefs-" + Guid.NewGuid().ToString("N"));

    public ViewPrefsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string PrefsPath => ViewPrefs.PrefsPath(_root);

    [Fact]
    public void MissingFile_ReturnsDefaultsWithoutRecovery()
    {
        var prefs = ViewPrefs.Load(_root);
        Assert.False(prefs.IsRecovery);
        Assert.Equal(string.Empty, prefs.RecoveryMessage);
        Assert.False(prefs.RailExpanded);
        Assert.False(prefs.FlowMode);
        Assert.Empty(prefs.Drafts);
    }

    [Fact]
    public void FlowMode_RoundTrips()
    {
        var prefs = new ViewPrefs { FlowMode = true };
        Assert.True(prefs.Save(_root, knownKeys: null, pruneDrafts: false));
        Assert.True(ViewPrefs.Load(_root).FlowMode);
    }

    [Fact]
    public void ValidPreferences_RoundTrip()
    {
        var prefs = new ViewPrefs { RailExpanded = true };
        prefs.Drafts["alpha"] = "draft one";
        prefs.Drafts["beta"] = "draft two";
        Assert.True(prefs.Save(_root, ["alpha", "beta"], pruneDrafts: true));

        var reloaded = ViewPrefs.Load(_root);
        Assert.False(reloaded.IsRecovery);
        Assert.True(reloaded.RailExpanded);
        Assert.Equal("draft one", reloaded.Drafts["alpha"]);
        Assert.Equal("draft two", reloaded.Drafts["beta"]);
    }

    [Fact]
    public void CorruptFile_PreservesBytesExposesRecoveryAndRefusesOverwrite()
    {
        var corrupt = "CORRUPT{{{"u8.ToArray();
        File.WriteAllBytes(PrefsPath, corrupt);

        var prefs = ViewPrefs.Load(_root);
        Assert.True(prefs.IsRecovery);
        Assert.NotEmpty(prefs.RecoveryReason ?? string.Empty);
        Assert.NotEmpty(prefs.RecoveryMessage);
        Assert.NotNull(prefs.CorruptBackupPath);
        Assert.True(File.Exists(prefs.CorruptBackupPath));
        Assert.Equal(corrupt, File.ReadAllBytes(prefs.CorruptBackupPath));

        // Ordinary saves are refused: the corrupt original stays identical.
        Assert.False(prefs.Save(_root, null, pruneDrafts: false));
        Assert.Equal(corrupt, File.ReadAllBytes(PrefsPath));
        Assert.False(prefs.Save(_root, ["anything"], pruneDrafts: true));
        Assert.Equal(corrupt, File.ReadAllBytes(PrefsPath));
    }

    [Fact]
    public void NullJson_IsRecovery()
    {
        File.WriteAllText(PrefsPath, "null");
        var prefs = ViewPrefs.Load(_root);
        Assert.True(prefs.IsRecovery);
    }

    [Fact]
    public void NullDrafts_IsValid()
    {
        File.WriteAllText(PrefsPath, """{"RailExpanded":true,"Drafts":null}""");
        var prefs = ViewPrefs.Load(_root);
        Assert.False(prefs.IsRecovery);
        Assert.True(prefs.RailExpanded);
        Assert.NotNull(prefs.Drafts);
        Assert.Empty(prefs.Drafts);
    }

    [Fact]
    public void NoPrune_WhenCatalogDegraded_Prune_WhenHealthy()
    {
        var prefs = new ViewPrefs();
        prefs.Drafts["gone-project"] = "keep me";
        prefs.Drafts["here-project"] = "keep me too";

        // Degraded catalog (recovery mode): unknown keys must survive.
        Assert.True(prefs.Save(_root, ["here-project"], pruneDrafts: false));
        var kept = ViewPrefs.Load(_root);
        Assert.Equal("keep me", kept.Drafts["gone-project"]);
        Assert.Equal("keep me too", kept.Drafts["here-project"]);

        // Healthy catalog: unknown keys are pruned, known keys kept.
        Assert.True(prefs.Save(_root, ["here-project"], pruneDrafts: true));
        var pruned = ViewPrefs.Load(_root);
        Assert.False(pruned.Drafts.ContainsKey("gone-project"));
        Assert.Equal("keep me too", pruned.Drafts["here-project"]);
    }

    [Fact]
    public void FailedWrite_PreservesInMemoryDraftsAndOriginalBytes()
    {
        var prefs = new ViewPrefs();
        prefs.Drafts["a"] = "unsent work";
        Assert.True(prefs.Save(_root, ["a"], pruneDrafts: true));
        var original = File.ReadAllBytes(PrefsPath);

        // Read-only target: temp write succeeds, atomic move fails.
        File.SetAttributes(PrefsPath, FileAttributes.ReadOnly);
        try
        {
            prefs.Drafts["a"] = "newer unsent work";
            var ex = Assert.Throws<UnauthorizedAccessException>(
                () => prefs.Save(_root, ["a"], pruneDrafts: true));
            Assert.NotNull(ex);
        }
        finally
        {
            File.SetAttributes(PrefsPath, FileAttributes.Normal);
        }

        // The only remaining copy — memory — is intact, as is the original.
        Assert.Equal("newer unsent work", prefs.Drafts["a"]);
        Assert.Equal(original, File.ReadAllBytes(PrefsPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void DirectoryAtPrefsPath_IsRecoveryWithoutBackup()
    {
        // A directory where the file belongs: reads are impossible and no
        // save could ever replace it — explicit recovery, no backup.
        Directory.CreateDirectory(PrefsPath);
        var prefs = ViewPrefs.Load(_root);
        Assert.True(prefs.IsRecovery);
        Assert.Null(prefs.CorruptBackupPath);
        Assert.NotEmpty(prefs.RecoveryMessage);
    }
}
