using System.Text.Json;

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

    public Dictionary<string, string> Drafts { get; set; } = new();

    public static string PrefsPath(string managedRoot) => Path.Combine(managedRoot, "view-prefs.json");

    public static ViewPrefs Load(string managedRoot)
    {
        try
        {
            var path = PrefsPath(managedRoot);
            if (!File.Exists(path))
            {
                return new ViewPrefs();
            }

            var prefs = JsonSerializer.Deserialize<ViewPrefs>(File.ReadAllText(path));
            var loaded = prefs ?? new ViewPrefs();
            loaded.Drafts ??= new Dictionary<string, string>();
            return loaded;
        }
        catch
        {
            return new ViewPrefs();
        }
    }

    public void Save(string managedRoot, IEnumerable<string>? knownKeys = null)
    {
        try
        {
            Directory.CreateDirectory(managedRoot);
            if (knownKeys is not null)
            {
                var keep = new HashSet<string>(knownKeys, StringComparer.Ordinal);
                foreach (var key in Drafts.Keys.ToArray())
                {
                    if (!keep.Contains(key))
                    {
                        Drafts.Remove(key);
                    }
                }
            }

            var path = PrefsPath(managedRoot);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Caller surfaces this as status; view prefs must never crash.
            throw;
        }
    }
}
