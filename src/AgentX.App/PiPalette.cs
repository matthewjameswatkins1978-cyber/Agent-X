namespace AgentX.App;

/// <summary>
/// Pi dark-theme role hues as sRGB, interpreted from Pi 1.1.0's built-in
/// dark theme (OKHSL roles in dist/modes/interactive/theme/dark.json).
/// These are Agent X's sRGB readings of Pi's perceptual roles — same hues,
/// same lightness intent — not claimed bit-exact terminal output.
/// </summary>
public static class PiPalette
{
    public const string Text = "#E4E7EE";       // text 89%
    public const string Muted = "#9AA3B5";      // muted 67%
    public const string Dim = "#82889A";        // dim 56%
    public const string Violet = "#C792EA";     // violet (customMessageLabel, mdCode, bullets)
    public const string Blue = "#82AAFF";       // blue (user accents, links, keywords)
    public const string Green = "#7FDBA8";      // green (success, code blocks)
    public const string Red = "#F07178";        // red (errors)
    public const string Yellow = "#E5C07B";     // yellow (warnings, headings)
    public const string Cyan = "#6FC3DF";       // syntaxVariable (technical refs)
    public const string UserBg = "#232F4A";     // userMessageBg blueBg 24%
    public const string ToolPendingBg = "#22262E";  // toolPendingBg 24%
    public const string ToolSuccessBg = "#1D3A2F";  // toolSuccessBg 25%
    public const string ToolErrorBg = "#3D2422";    // toolErrorBg 25%
}
