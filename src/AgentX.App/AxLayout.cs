namespace AgentX.App;

/// <summary>
/// Shared layout geometry for the Agent X shell (Checkpoint B2 / Layout
/// Inspector mission): one set of datum lines, spacing tokens and sizes
/// that the XAML, the code-behind and the developer overlay all read.
/// Related edges share exact values instead of accumulating independent
/// margins. All units are device-independent pixels; Avalonia scales them
/// by the display RenderScaling. Pure and tested.
/// </summary>
public static class AxLayout
{
    /// Base grid unit: section gaps, card gaps and rail rhythm are multiples.
    public const double Grid = 8;

    /// Outer window margins (the root grid).
    public const double MarginLeft = 12;
    public const double MarginTop = 10;
    public const double MarginRight = 12;
    public const double MarginBottom = 8;

    /// Left rail column width; the header brand cell matches it so the
    /// rabbit centres exactly over the rail icons.
    public const double RailWidth = 48;

    /// Rail destination buttons and their icons.
    public const double NavButton = 40;
    public const double NavIcon = 18;

    /// Gutter between the rail column and the content column.
    public const double ContentGutter = 8;

    /// Card strip gaps and feed row spacing base.
    public const double CardGap = 8;

    /// Content column reading width cap (Overview/Settings).
    public const double ContentMaxWidth = 900;

    /// Composer inner padding and input/send gap.
    public const double ComposerPadding = 8;

    /// Send button footprint.
    public const double SendButton = 32;

    /// Footer top breathing room above the divider.
    public const double FooterTopPadding = 6;

    /// Centre of the rail column from the rail's own left edge: rail
    /// icons and the header rabbit share this datum.
    public static double RailCentreX => RailWidth / 2;

    /// Icon inset inside a rail button (centred both axes).
    public static double NavIconInset => (NavButton - NavIcon) / 2;

    /// Content-column left edge in window coordinates: outer margin +
    /// rail + gutter. Cards, divider, pages and footer all start here.
    public static double ContentLeft => MarginLeft + RailWidth + ContentGutter;
}
