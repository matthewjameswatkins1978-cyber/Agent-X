using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Genuine user preferences: only settings the GUI actually applies. Stored
/// as JSON in the managed root (<c>preferences.json</c>). A corrupt file
/// falls back to defaults after preserving the original as <c>.bak</c> —
/// preferences are disposable UI state, never session identity, so failing
/// closed would only punish the user for a cosmetic file. Unknown JSON
/// members (e.g. the retired <c>VerboseMap</c> flag) are ignored, so older
/// files migrate silently to the default visibility.
/// </summary>
public sealed record AgentXPreferences(
    bool AutoScroll = true,
    string Visibility = "Detailed",
    string Theme = "System")
{
    public const string FileName = "preferences.json";

    public static readonly string[] KnownThemes = ["System", "Light", "Dark"];

    public static readonly string[] KnownVisibility = ["Compact", "Detailed", "Full"];

    public static string PreferencesPath(string managedRoot) => Path.Combine(managedRoot, FileName);

    public AgentXPreferences Normalized()
        => this with
        {
            Theme = KnownThemes.Contains(Theme) ? Theme : "System",
            Visibility = KnownVisibility.Contains(Visibility) ? Visibility : "Detailed",
        };

    public static (AgentXPreferences Preferences, string? Note) Load(string managedRoot)
    {
        var path = PreferencesPath(managedRoot);
        if (!File.Exists(path))
        {
            return (new AgentXPreferences(), null);
        }

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<AgentXPreferences>(json);
            if (parsed is null)
            {
                throw new InvalidDataException("Preferences deserialized to null.");
            }

            return (parsed.Normalized(), null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            try
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            catch (Exception)
            {
                // Best effort: defaults still apply below.
            }

            return (new AgentXPreferences(), $"Preferences unreadable ({ex.GetType().Name}); defaults applied, original kept as .bak.");
        }
    }

    public void Save(string managedRoot)
    {
        Directory.CreateDirectory(managedRoot);
        var path = PreferencesPath(managedRoot);
        var json = JsonSerializer.Serialize(Normalized(), new JsonSerializerOptions { WriteIndented = true });
        var temp = path + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}
