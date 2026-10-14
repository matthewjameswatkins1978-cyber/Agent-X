using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentX.App;

/// <summary>
/// App-side view preferences — NOT engine prefs. Covers the navigation rail
/// expanded state and per-workspace composer drafts. Stored as JSON at
/// %LocalAppData%/AgentX/view-prefs.json, written by the App only.
/// Fail-closed: a missing or corrupt file yields defaults (the corrupt file
/// is left alone, never deleted). Saves are atomic-ish (temp + move) and
/// best-effort; a failure surfaces as a status message and never touches
/// the workspace registry, sessions or engine preferences.
/// </summary>
public sealed class ViewPrefs
{
    public bool RailExpanded { get; set; }

    /// B7 appearance treatment display name ("Pi Faithful" default).
    /// Unknown values fall back on load; Core preferences are untouched.
    public string Treatment { get; set; } = "Pi Faithful";

    public Dictionary<string, string> Drafts { get; set; } = new();

    /// True when the file existed but could not be parsed or read: the
    /// original bytes are preserved under <see cref="CorruptBackupPath"/>
    /// (when readable at all) and persistence is DISABLED until the file
    /// is removed or repaired. Never serialized.
    [JsonIgnore]
    public bool IsRecovery { get; private set; }

    [JsonIgnore]
    public string? RecoveryReason { get; private set; }

    [JsonIgnore]
    public string? CorruptBackupPath { get; private set; }

    /// Human-readable recovery status for the footer (empty when healthy).
    [JsonIgnore]
    public string RecoveryMessage => IsRecovery
        ? $"View preferences unreadable ({RecoveryReason}) — using defaults; original preserved at {CorruptBackupPath ?? "(backup failed)"}. Changes are memory-only until the file is removed or repaired."
        : string.Empty;

    public static string PrefsPath(string managedRoot) => Path.Combine(managedRoot, "view-prefs.json");

    public static ViewPrefs Load(string managedRoot)
    {
        var path = PrefsPath(managedRoot);
        if (!File.Exists(path))
        {
            // A directory where the file belongs is itself a corrupt state:
            // saves could never replace it, so say so explicitly.
            if (Directory.Exists(path))
            {
                return Recovery(managedRoot, "a directory blocks the preferences path", backupBytes: null);
            }

            return new ViewPrefs();
        }

        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Recovery(managedRoot, $"could not be read: {ex.Message}", backupBytes: null);
        }

        try
        {
            var prefs = JsonSerializer.Deserialize<ViewPrefs>(raw);
            if (prefs is null)
            {
                return Recovery(managedRoot, "content is not a preferences object", raw);
            }

            prefs.Drafts ??= new Dictionary<string, string>();
            prefs.Treatment = AxTreatments.DisplayName(AxTreatments.Parse(prefs.Treatment));
            return prefs;
        }
        catch (JsonException ex)
        {
            return Recovery(managedRoot, $"invalid JSON: {ex.Message}", raw);
        }
    }

    /// <summary>
    /// Recovery instance: defaults plus an explicit reason, with the
    /// original bytes preserved to a timestamped backup when readable.
    /// Ordinary saves refuse to touch the original while this is active.
    /// </summary>
    private static ViewPrefs Recovery(string managedRoot, string reason, byte[]? backupBytes)
    {
        var recovered = new ViewPrefs();
        string? backupPath = null;
        if (backupBytes is not null)
        {
            try
            {
                backupPath = Path.Combine(managedRoot,
                    $"view-prefs.corrupt.{DateTime.UtcNow:yyyyMMddTHHmmss}.bak");
                File.WriteAllBytes(backupPath, backupBytes);
            }
            catch
            {
                backupPath = null;
            }
        }

        recovered.IsRecovery = true;
        recovered.RecoveryReason = reason;
        recovered.CorruptBackupPath = backupPath;
        return recovered;
    }

    /// Persist preferences. Returns false WITHOUT WRITING when this
    /// instance is in recovery: the corrupt original stays byte-identical
    /// and in-memory drafts keep working for the session. Throws on IO
    /// failure — and never mutates the in-memory drafts first: pruning
    /// applies to a serialization copy, so a failed write cannot destroy
    /// the only remaining drafts.
    /// <param name="pruneDrafts">Only true when the workspace catalog is
    /// healthy: a degraded/recovery catalog must never prune drafts for
    /// temporarily unavailable projects.</param>
    public bool Save(string managedRoot, IEnumerable<string>? knownKeys, bool pruneDrafts)
    {
        if (IsRecovery)
        {
            return false;
        }

        var drafts = Drafts;
        if (pruneDrafts && knownKeys is not null)
        {
            var keep = new HashSet<string>(knownKeys, StringComparer.Ordinal);
            drafts = Drafts
                .Where(pair => keep.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        Directory.CreateDirectory(managedRoot);
        var path = PrefsPath(managedRoot);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(new ViewPrefs
            {
                RailExpanded = RailExpanded,
                Treatment = Treatment,
                Drafts = drafts,
            }));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
}
