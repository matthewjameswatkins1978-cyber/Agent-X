namespace AgentX.App;

/// <summary>
/// B7 visual treatment: three professional appearances sharing one layout
/// and behaviour. The treatment changes only the token set applied by
/// <see cref="AxTheme"/> — never structure, routing or feed semantics.
/// </summary>
public enum AxTreatment
{
    /// <summary>Closest to the Pi terminal: graphite, strong type, Pi semantic accents.</summary>
    PiFaithful = 0,

    /// <summary>Polished dark developer surface with restrained teal highlights.</summary>
    MidnightTeal = 1,

    /// <summary>Near-monochrome chrome; Pi semantic hues reserved for events and evidence.</summary>
    TechnicalMinimal = 2,
}

/// <summary>
/// One treatment's visual tokens: dark surfaces, text, accent and Pi-faithful
/// conversation roles. Hex sRGB. Semantic event hues (blue/green/red/violet/
/// yellow/cyan) stay identical across treatments — only the chrome around
/// them changes, so evidence never changes meaning between treatments.
/// </summary>
public sealed record AxTokens(
    string Name,
    string Bg,
    string Surface,
    string Surface2,
    string Border,
    string BorderStrong,
    string Text,
    string Muted,
    string Dim,
    string Accent,
    string UserBg,
    string UserEdge,
    string ToolPendingBg,
    string ToolSuccessBg,
    string ToolErrorBg,
    double Radius,
    double TabHeight,
    double RailWidth,
    double ConversationPad,
    double RowGap);

/// <summary>Token sets for the three B7 treatments. Pure data; UI-free.</summary>
public static class AxTreatments
{
    public static readonly AxTokens PiFaithful = new(
        Name: "Pi Faithful",
        Bg: "#101318",
        Surface: "#161B22",
        Surface2: "#1C232E",
        Border: "#343B47",
        BorderStrong: "#4A5364",
        Text: "#E4E7EE",
        Muted: "#9AA3B5",
        Dim: "#82889A",
        Accent: "#82AAFF",
        UserBg: "#232F4A",
        UserEdge: "#82AAFF",
        ToolPendingBg: "#22262E",
        ToolSuccessBg: "#1D3A2F",
        ToolErrorBg: "#3D2422",
        Radius: 4,
        TabHeight: 30,
        RailWidth: 52,
        ConversationPad: 10,
        RowGap: 8);

    public static readonly AxTokens MidnightTeal = new(
        Name: "Midnight Teal",
        Bg: "#0C1315",
        Surface: "#111B1E",
        Surface2: "#162528",
        Border: "#234144",
        BorderStrong: "#33585C",
        Text: "#E3E9EA",
        Muted: "#93A5A8",
        Dim: "#77888B",
        Accent: "#4FD1C5",
        UserBg: "#16303A",
        UserEdge: "#4FD1C5",
        ToolPendingBg: "#151E20",
        ToolSuccessBg: "#13332A",
        ToolErrorBg: "#38211F",
        Radius: 6,
        TabHeight: 30,
        RailWidth: 52,
        ConversationPad: 12,
        RowGap: 8);

    public static readonly AxTokens TechnicalMinimal = new(
        Name: "Technical Minimal",
        Bg: "#0F0F0F",
        Surface: "#161616",
        Surface2: "#1C1C1C",
        Border: "#2A2A2A",
        BorderStrong: "#3D3D3D",
        Text: "#DCDCDC",
        Muted: "#9C9C9C",
        Dim: "#7C7C7C",
        Accent: "#9A9A9A",
        UserBg: "#1A2330",
        UserEdge: "#5B6B82",
        ToolPendingBg: "#181818",
        ToolSuccessBg: "#16241F",
        ToolErrorBg: "#2A1B19",
        Radius: 4,
        TabHeight: 28,
        RailWidth: 48,
        ConversationPad: 10,
        RowGap: 6);

    public static AxTokens For(AxTreatment treatment) => treatment switch
    {
        AxTreatment.MidnightTeal => MidnightTeal,
        AxTreatment.TechnicalMinimal => TechnicalMinimal,
        _ => PiFaithful,
    };

    public static string[] Names => ["Pi Faithful", "Midnight Teal", "Technical Minimal"];

    public static AxTreatment Parse(string? name) => name switch
    {
        "Midnight Teal" => AxTreatment.MidnightTeal,
        "Technical Minimal" => AxTreatment.TechnicalMinimal,
        _ => AxTreatment.PiFaithful,
    };

    public static string DisplayName(AxTreatment treatment) => For(treatment).Name;
}
