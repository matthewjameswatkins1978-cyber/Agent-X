namespace AgentX.Core;

/// <summary>
/// Composer input preparation: the single choke point between the prompt box
/// and Pi. Returns null for null/empty/whitespace-only input (the host shows
/// "prompt is empty"); otherwise returns the text byte-for-byte — no Trim,
/// no normalization, newlines and significant whitespace preserved exactly
/// as pasted or typed. Tested against a multiline fixture.
/// </summary>
public static class PromptInput
{
    public static string? Prepare(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text;
}
