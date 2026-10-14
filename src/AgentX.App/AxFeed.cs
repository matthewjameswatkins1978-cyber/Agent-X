namespace AgentX.App;

/// <summary>
/// Feed typography appearances (Beautiful Feed mission, issues #24/#25):
/// persistent, selectable, and independent of the B7 colour treatments.
/// Studio pairs Geist Sans prose and chrome with Geist Mono technical text;
/// Operator sets the whole feed in Monaspace Neon with Monaspace Argon for
/// commentary italics. Same content and Pi semantic colours in both — only
/// the voice changes. Pure data; UI-free.
/// </summary>
public enum AxFeedAppearance
{
    Studio,
    Operator,
}

public static class AxFeed
{
    public static string[] Names => ["Studio", "Operator"];

    public static AxFeedAppearance Parse(string? name) => name switch
    {
        "Operator" => AxFeedAppearance.Operator,
        _ => AxFeedAppearance.Studio,
    };

    public static string DisplayName(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => "Operator",
        _ => "Studio",
    };

    // Bundled-font folder collections (Assets/Fonts/*, SIL OFL — see
    // THIRD-PARTY-NOTICES.md). Folder + family name lets Avalonia match the
    // Regular/Italic/Bold/BoldItalic faces it finds there; rendering is
    // verified by screenshots, never assumed.
    public const string GeistDir = "avares://AgentX.App/Assets/Fonts/Geist";
    public const string GeistMonoDir = "avares://AgentX.App/Assets/Fonts/GeistMono";
    public const string NeonDir = "avares://AgentX.App/Assets/Fonts/MonaspaceNeon";
    public const string ArgonDir = "avares://AgentX.App/Assets/Fonts/MonaspaceArgon";

    /// Chrome (rail, tabs, toolbar, footer, settings) is Geist Sans in both
    /// appearances: typography changes the feed voice, never app identity.
    public const string ChromeFamily = "Geist";

    /// Conversational prose family for the appearance.
    public static string ProseFamily(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => "Monaspace Neon",
        _ => "Geist",
    };

    /// Commands, code, paths and technical output for the appearance.
    public static string MonoFamily(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => "Monaspace Neon",
        _ => "Geist Mono",
    };

    /// Commentary italics (quotes, agent asides) for the appearance.
    public static string VoiceFamily(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => "Monaspace Argon",
        _ => "Geist",
    };

    public static string ChromeSource => $"{GeistDir}#{ChromeFamily}";

    public static string ProseSource(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => $"{NeonDir}#{ProseFamily(appearance)}",
        _ => $"{GeistDir}#{ProseFamily(appearance)}",
    };

    public static string MonoSource(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => $"{NeonDir}#{MonoFamily(appearance)}",
        _ => $"{GeistMonoDir}#{MonoFamily(appearance)}",
    };

    public static string VoiceSource(AxFeedAppearance appearance) => appearance switch
    {
        AxFeedAppearance.Operator => $"{ArgonDir}#{VoiceFamily(appearance)}",
        _ => $"{GeistDir}#{VoiceFamily(appearance)}",
    };

    /// Adjustable feed sizes (mission: begin at 14px, verify 13–16).
    /// Unknown or out-of-range values clamp; NaN falls back to the default.
    public static double[] Sizes => [13, 14, 15, 16];

    public const double DefaultSize = 14;

    public static double ClampSize(double size)
        => double.IsNaN(size) ? DefaultSize : Math.Clamp(size, Sizes[0], Sizes[^1]);

    /// Technical text sits one step under prose, never below 11px.
    public static double MonoSize(double proseSize) => Math.Max(11, ClampSize(proseSize) - 1);

    /// Comfortable long-form spacing: one-and-a-half line rhythm.
    public static double LineHeight(double proseSize) => ClampSize(proseSize) * 1.5;
}
