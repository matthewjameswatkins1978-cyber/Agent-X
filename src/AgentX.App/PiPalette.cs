namespace AgentX.App;

/// <summary>
/// Pi dark-theme role hues as sRGB, converted from Pi 1.3.0's built-in dark
/// theme (dist/modes/interactive/theme/dark.json) with Pi's own converter
/// (@earendil-works/pi-tui parseColor/colorToHex): the hues Pi CLI shows on
/// a truecolor terminal. Re-verify after any Pi upgrade by running the vars
/// through that converter again. Backgrounds stay deliberate app chrome.
/// </summary>
public static class PiPalette
{
    public const string Text = "#DEE0E1";       // text
    public const string Muted = "#9DA5A9";      // muted
    public const string Dim = "#7E888E";        // dim
    public const string Violet = "#A798D7";     // accent, mdCode, bullets, type
    public const string Blue = "#69ADD0";       // links, keywords
    public const string Green = "#68B78D";      // success, code blocks, numbers
    public const string Red = "#EA7F81";        // errors
    public const string Yellow = "#CD9A22";     // warnings, headings, functions
    public const string Cyan = "#5DB3BA";       // syntaxVariable (technical refs)
    public const string String = "#DE8D5A";     // syntaxString (string literals)
    public const string UserBg = "#232F4A";     // app chrome (Pi CLI blueBg is #213B49)     // userMessageBg blueBg 24%
    public const string ToolPendingBg = "#22262E";  // app chrome (Pi CLI is #34383A)  // toolPendingBg 24%
    public const string ToolSuccessBg = "#1D3A2F";  // app chrome (Pi CLI is #254131)  // toolSuccessBg 25%
    public const string ToolErrorBg = "#3D2422";  // app chrome (Pi CLI is #5B282A)    // toolErrorBg 25%
}
