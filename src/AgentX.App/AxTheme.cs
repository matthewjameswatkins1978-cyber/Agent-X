using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// B7 runtime theme: applies one <see cref="AxTokens"/> set to shared,
/// long-lived brushes so every surface — AXAML DynamicResources, the
/// <see cref="AxIcon"/> stroke brush and the <see cref="MarkdownView"/>
/// inline brushes — follows a treatment switch without rebuilding controls.
/// Brush instances are created once and mutated (Color setters propagate),
/// so a switch also refreshes already-rendered conversation inlines.
/// Also owns the native dark title bar and the display-label mapping
/// (Main / Agent N), both display-only: storage identities never change.
/// </summary>
public static class AxTheme
{
    public static AxTreatment CurrentTreatment { get; private set; } = AxTreatment.PiFaithful;

    public static AxTokens Current => AxTreatments.For(CurrentTreatment);

    // Shared mutable brushes: created once, Color mutated on Apply.
    public static SolidColorBrush BgBrush { get; } = New("#101318");
    public static SolidColorBrush SurfaceBrush { get; } = New("#161B22");
    public static SolidColorBrush Surface2Brush { get; } = New("#1C232E");
    public static SolidColorBrush BorderBrush { get; } = New("#343B47");
    public static SolidColorBrush BorderStrongBrush { get; } = New("#4A5364");
    public static SolidColorBrush TextBrush { get; } = New("#E4E7EE");
    public static SolidColorBrush MutedBrush { get; } = New("#9AA3B5");
    public static SolidColorBrush DimBrush { get; } = New("#82889A");
    public static SolidColorBrush AccentBrush { get; } = New("#82AAFF");
    public static SolidColorBrush IconBrush { get; } = New("#9AA3B5");
    public static SolidColorBrush UserBgBrush { get; } = New("#232F4A");
    public static SolidColorBrush UserEdgeBrush { get; } = New("#82AAFF");
    public static SolidColorBrush ToolPendingBgBrush { get; } = New("#22262E");
    public static SolidColorBrush ToolSuccessBgBrush { get; } = New("#1D3A2F");
    public static SolidColorBrush ToolErrorBgBrush { get; } = New("#3D2422");

    // Pi semantic hues are treatment-independent (evidence never changes
    // meaning): one shared instance each, matching PiPalette / dark.json.
    public static SolidColorBrush BlueBrush { get; } = New(PiPalette.Blue);
    public static SolidColorBrush GreenBrush { get; } = New(PiPalette.Green);
    public static SolidColorBrush RedBrush { get; } = New(PiPalette.Red);
    public static SolidColorBrush YellowBrush { get; } = New(PiPalette.Yellow);
    public static SolidColorBrush VioletBrush { get; } = New(PiPalette.Violet);
    public static SolidColorBrush CyanBrush { get; } = New(PiPalette.Cyan);

    public static FontFamily MonoFont { get; } = new("Cascadia Code,Consolas,monospace");

    private static SolidColorBrush New(string hex) => new(Color.Parse(hex));

    private static void Set(SolidColorBrush brush, string hex)
    {
        var color = Color.Parse(hex);
        if (brush.Color != color)
        {
            brush.Color = color;
        }
    }

    /// <summary>
    /// Apply a treatment: mutate the shared brushes and (re)register the
    /// Ax.* resource keys. Idempotent; safe to call before App resources
    /// exist (brushes still update live controls).
    /// </summary>
    public static void Apply(AxTreatment treatment)
    {
        CurrentTreatment = treatment;
        var t = AxTreatments.For(treatment);
        Set(BgBrush, t.Bg);
        Set(SurfaceBrush, t.Surface);
        Set(Surface2Brush, t.Surface2);
        Set(BorderBrush, t.Border);
        Set(BorderStrongBrush, t.BorderStrong);
        Set(TextBrush, t.Text);
        Set(MutedBrush, t.Muted);
        Set(DimBrush, t.Dim);
        Set(AccentBrush, t.Accent);
        Set(IconBrush, t.Muted);
        Set(UserBgBrush, t.UserBg);
        Set(UserEdgeBrush, t.UserEdge);
        Set(ToolPendingBgBrush, t.ToolPendingBg);
        Set(ToolSuccessBgBrush, t.ToolSuccessBg);
        Set(ToolErrorBgBrush, t.ToolErrorBg);

        if (Application.Current is { } app)
        {
            app.Resources["Ax.Bg"] = BgBrush;
            app.Resources["Ax.Surface"] = SurfaceBrush;
            app.Resources["Ax.Surface2"] = Surface2Brush;
            app.Resources["Ax.Border"] = BorderBrush;
            app.Resources["Ax.BorderStrong"] = BorderStrongBrush;
            app.Resources["Ax.Text"] = TextBrush;
            app.Resources["Ax.Muted"] = MutedBrush;
            app.Resources["Ax.Dim"] = DimBrush;
            app.Resources["Ax.Accent"] = AccentBrush;
            app.Resources["Ax.Icon"] = IconBrush;
            app.Resources["Ax.UserBg"] = UserBgBrush;
            app.Resources["Ax.UserEdge"] = UserEdgeBrush;
            app.Resources["Ax.ToolPendingBg"] = ToolPendingBgBrush;
            app.Resources["Ax.ToolSuccessBg"] = ToolSuccessBgBrush;
            app.Resources["Ax.ToolErrorBg"] = ToolErrorBgBrush;
            app.Resources["Ax.Blue"] = BlueBrush;
            app.Resources["Ax.Green"] = GreenBrush;
            app.Resources["Ax.Red"] = RedBrush;
            app.Resources["Ax.Yellow"] = YellowBrush;
            app.Resources["Ax.Violet"] = VioletBrush;
            app.Resources["Ax.Cyan"] = CyanBrush;
            app.Resources["Ax.MonoFont"] = MonoFont;
            // CornerRadius is a struct resource: DynamicResource bindings to
            // CornerRadius properties need the converted value, not a double.
            app.Resources["Ax.Radius"] = new CornerRadius(t.Radius);
        }
    }

    /// <summary>
    /// Display label for a workspace tab: the legacy session shows as
    /// "Main"; project workspaces keep their catalog label. Display-only —
    /// keys, session ids and storage are untouched (full identity stays in
    /// the tab tooltip).
    /// </summary>
    public static string WorkspaceTabLabel(bool isLegacy, string label)
        => isLegacy ? "Main" : label;

    /// <summary>
    /// Display label for a worker tab: stored labels are kept; Core's
    /// auto-generated "Worker N" displays as "Agent N"; blanks fall back to
    /// a stable short-id form. Display-only.
    /// </summary>
    public static string WorkerTabLabel(string id, string? label)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            var trimmed = label.Trim();
            const string auto = "Worker ";
            if (trimmed.StartsWith(auto, StringComparison.Ordinal) &&
                trimmed.Length > auto.Length)
            {
                return "Agent " + trimmed[auto.Length..].Trim();
            }

            return trimmed;
        }

        return $"Agent {id[..Math.Min(4, id.Length)]}";
    }

    // --- Native dark title bar (Windows only, no custom chrome). ---

    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);

    /// COLORREF 0x00BBGGRR from an sRGB hex string.
    private static int ColorToBgr(string hex)
    {
        var c = Color.Parse(hex);
        return c.R | (c.G << 8) | (c.B << 16);
    }

    /// <summary>
    /// Ask DWM for a dark native title bar so the frame matches the dark
    /// treatments. Native chrome is untouched: move, resize, snap, maximise,
    /// minimise, keyboard and close behave exactly as before. Best-effort:
    /// any failure is swallowed (the app stays fully usable with a system
    /// title bar). Must run after the window has a native handle.
    /// </summary>
    public static void EnsureDarkTitleBar(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var handle = window.TryGetPlatformHandle();
            if (handle is null)
            {
                return;
            }

            var useDark = 1;
            DwmSetWindowAttribute(handle.Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
            // Win11+: explicit caption/text colours coordinate the native
            // frame with the dark treatments even when "show accent colour
            // on title bars" is on (prevalence overrides the dark-mode wash
            // but not an explicit caption colour). Verified live on Win11.
            if (Environment.OSVersion.Version.Build >= 22000)
            {
                var caption = ColorToBgr(Current.Bg);
                DwmSetWindowAttribute(handle.Handle, DwmwaCaptionColor, ref caption, sizeof(int));
                var text = ColorToBgr(Current.Text);
                DwmSetWindowAttribute(handle.Handle, DwmwaTextColor, ref text, sizeof(int));
            }
        }
        catch
        {
            // Cosmetic only: never fail startup or behaviour for the frame.
        }
    }
}
