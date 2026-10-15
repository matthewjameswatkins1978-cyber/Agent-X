using Avalonia;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>Vector icon identity for the B7 chrome (toolbar, rail, tabs).</summary>
public enum AxIconKind
{
    Connect,
    Disconnect,
    Reconnect,
    Stop,
    Copy,
    More,
    Live,
    Overview,
    Settings,
    Project,
    Add,
    Collapse,
    Remove,
    Rabbit,
    Search,
    Sliders,
    Grid,
    Chat,
    Branch,
}

/// <summary>
/// Bundled Lucide-style stroke geometries (24x24 viewBox, 2px round stroke),
/// licensed ISC (Lucide) + MIT (Feather-derived: check, terminal, trash, x —
/// see THIRD-PARTY-NOTICES.md). Only the icons the chrome actually uses are
/// bundled: no icon package, no emoji, no dead glyphs. Path data verified by
/// <c>AxDesignTests</c> via <see cref="Geometry.Parse"/>.
/// </summary>
public static class AxIcons
{
    // Rect (8,8,14,14,rx2) rewritten as path; circle (12,12,3) as arcs.
    public const string Connect = "M12 22v-5 M15 8V2 M17 8a1 1 0 0 1 1 1v4a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V9a1 1 0 0 1 1-1z M9 8V2";

    public const string Disconnect = "m19 5 3-3 m2 22 3-3 M6.3 20.3a2.4 2.4 0 0 0 3.4 0L12 18l-6-6-2.3 2.3a2.4 2.4 0 0 0 0 3.4Z M7.5 13.5 10 11 M10.5 16.5 13 14 m12 6 6 6 2.3-2.3a2.4 2.4 0 0 0 0-3.4l-2.6-2.6a2.4 2.4 0 0 0-3.4 0Z";

    public const string Reconnect = "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5";

    public const string Stop = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z";

    public const string Copy = "M10 8h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H10a2 2 0 0 1-2-2V10a2 2 0 0 1 2-2z M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2";

    public const string More = "M4 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M11 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0 M18 12a1 1 0 1 0 2 0a1 1 0 1 0-2 0";

    public const string Live = "M12 19h8 m4 17 6-6-6-6";

    public const string Overview = "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12 M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17";

    public const string Settings = "M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0 M9.671 4.136a2.34 2.34 0 0 1 4.659 0 2.34 2.34 0 0 0 3.319 1.915 2.34 2.34 0 0 1 2.33 4.033 2.34 2.34 0 0 0 0 3.831 2.34 2.34 0 0 1-2.33 4.033 2.34 2.34 0 0 0-3.319 1.915 2.34 2.34 0 0 1-4.659 0 2.34 2.34 0 0 0-3.32-1.915 2.34 2.34 0 0 1-2.33-4.033 2.34 2.34 0 0 0 0-3.831A2.34 2.34 0 0 1 6.35 6.051a2.34 2.34 0 0 0 3.319-1.915";

    public const string Project = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z";

    public const string Add = "M5 12h14 M12 5v14";

    public const string Collapse = "M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M9 3v18 m16 15-3-3 3-3";

    public const string Remove = "M10 11v6 M14 11v6 M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6 M3 6h18 M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2";

    public const string Rabbit = "M6 15a6 6 0 1 0 12 0 6 6 0 1 0-12 0 M9.5 9.5Q8.5 5 9 2.5 M14.5 9.5Q15.5 5 15 2.5";

    public const string Search = "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16z M21 21l-4.3-4.3";

    public const string Sliders = "M21 4h-7 M10 4H3 M21 12h-9 M8 12H3 M21 20h-5 M12 20H3 M14 2v4 M8 10v4 M16 18v4";

    public const string Grid = "M4 4h7v7H4z M13 4h7v7h-7z M4 13h7v7H4z M13 13h7v7h-7z";

    public const string Chat = "M7.9 20A9 9 0 1 0 4 16.1L2 22Z";

    public const string Branch = "M6 3v12 M18 9a9 9 0 0 1-9 9 M15 6a3 3 0 1 0 6 0 3 3 0 1 0-6 0 M3 18a3 3 0 1 0 6 0 3 3 0 1 0-6 0";

    public static string DataFor(AxIconKind kind) => kind switch
    {
        AxIconKind.Connect => Connect,
        AxIconKind.Disconnect => Disconnect,
        AxIconKind.Reconnect => Reconnect,
        AxIconKind.Stop => Stop,
        AxIconKind.Copy => Copy,
        AxIconKind.More => More,
        AxIconKind.Live => Live,
        AxIconKind.Overview => Overview,
        AxIconKind.Settings => Settings,
        AxIconKind.Project => Project,
        AxIconKind.Add => Add,
        AxIconKind.Collapse => Collapse,
        AxIconKind.Remove => Remove,
        AxIconKind.Rabbit => Rabbit,
        AxIconKind.Search => Search,
        AxIconKind.Sliders => Sliders,
        AxIconKind.Grid => Grid,
        AxIconKind.Chat => Chat,
        AxIconKind.Branch => Branch,
        _ => More,
    };
}

/// <summary>
/// One stroke icon in the B7 chrome. Renders the bundled Lucide-style
/// geometry with a 2px round stroke in the shared icon brush (which follows
/// the active treatment). <see cref="Filled"/> renders solid instead (used
/// for Stop and the overflow dots).
/// </summary>
public sealed class AxIcon : Avalonia.Controls.Shapes.Path
{
    public static readonly StyledProperty<AxIconKind> KindProperty =
        AvaloniaProperty.Register<AxIcon, AxIconKind>(nameof(Kind), AxIconKind.More);

    public static readonly StyledProperty<bool> FilledProperty =
        AvaloniaProperty.Register<AxIcon, bool>(nameof(Filled));

    static AxIcon()
    {
        KindProperty.Changed.AddClassHandler<AxIcon>((c, _) => c.Refresh());
        FilledProperty.Changed.AddClassHandler<AxIcon>((c, _) => c.Refresh());
    }

    public AxIcon()
    {
        Width = 16;
        Height = 16;
        Stretch = Stretch.Uniform;
        StrokeThickness = 2;
        StrokeLineCap = PenLineCap.Round;
        StrokeJoin = PenLineJoin.Round;
        Focusable = false;
        Refresh();
    }

    public AxIconKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public bool Filled
    {
        get => GetValue(FilledProperty);
        set => SetValue(FilledProperty, value);
    }

    private void Refresh()
    {
        try
        {
            Data = Geometry.Parse(AxIcons.DataFor(Kind));
        }
        catch
        {
            Data = null;
        }

        if (Filled)
        {
            Fill = AxTheme.IconBrush;
            Stroke = null;
        }
        else
        {
            Fill = null;
            Stroke = AxTheme.IconBrush;
        }
    }
}
