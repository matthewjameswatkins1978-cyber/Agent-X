namespace AgentX.App;

/// <summary>
/// Composer placeholder text per selection (Checkpoint B1): the empty box
/// names its destination ("Message Agent ONE…"), matching the approved Feed
/// design. Unselected falls back to the neutral prompt. Pure so the wording
/// is tested, not eyeballed.
/// </summary>
public static class ComposerPlaceholder
{
    public const string Default = "Ask Pi anything…";

    public static string For(string? workerLabel, string? entryName)
    {
        if (!string.IsNullOrWhiteSpace(workerLabel))
        {
            return $"Message {workerLabel.Trim()}…";
        }

        if (!string.IsNullOrWhiteSpace(entryName))
        {
            return $"Message {entryName.Trim()}…";
        }

        return Default;
    }
}
