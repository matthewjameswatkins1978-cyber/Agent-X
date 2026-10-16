using Avalonia;

namespace AgentX.App;

/// <summary>One measured relationship: the expected value, the actual
/// value, and their absolute deviation in device-independent pixels.</summary>
public sealed record GuideDeviation(string Name, double Expected, double Actual)
{
    public double Delta => Math.Abs(Expected - Actual);
}

/// <summary>
/// Pure geometry behind the developer Layout Inspector (Checkpoint B2):
/// checks RELATIVE relationships between measured control rects (all in
/// one coordinate space, e.g. the overlay canvas), never absolute window
/// coordinates — so the same checks hold at 720px, 1200px and wider.
/// The overlay draws these; tests pin them with synthetic rects.
/// Tolerance is 0.5 DIP: genuine offsets show, fractional device-pixel
/// rounding does not fail the build.
/// </summary>
public static class LayoutAudit
{
    public const double Tolerance = 0.5;

    /// <summary>
    /// Check the shell's datum relationships. Missing rects are skipped
    /// (a hidden page contributes nothing); present rects are all checked.
    /// </summary>
    public static IReadOnlyList<GuideDeviation> Check(IReadOnlyDictionary<string, Rect> rects)
    {
        var deviations = new List<GuideDeviation>();

        // Content column: cards, dividers, feed, composer and footer share
        // both vertical edges.
        var column = new[] { "cards", "cardsDiv", "feed", "composer", "footer" };
        var lefts = column.Where(rects.ContainsKey).Select(k => rects[k].Left).ToList();
        if (lefts.Count > 1)
        {
            deviations.Add(Spread("content-left", lefts));
        }

        var rights = column.Where(rects.ContainsKey).Select(k => rects[k].Right).ToList();
        if (rights.Count > 1)
        {
            deviations.Add(Spread("content-right", rights));
        }

        // Rail datum: the rabbit centres over the rail icons.
        if (rects.TryGetValue("rabbit", out var rabbit) && rects.TryGetValue("nav", out var nav))
        {
            deviations.Add(new GuideDeviation(
                "rail-brand", nav.Center.X, rabbit.Center.X));
        }

        // Brand text starts on the content datum (cards edge as reference).
        if (rects.TryGetValue("brand", out var brand) && rects.TryGetValue("cards", out var cards))
        {
            deviations.Add(new GuideDeviation("brand-text", cards.Left, brand.Left));
        }

        // Composer: Send button optically centred on the input row.
        if (rects.TryGetValue("send", out var send) && rects.TryGetValue("input", out var input))
        {
            deviations.Add(new GuideDeviation("send-input", input.Center.Y, send.Center.Y));
        }

        // Header divider spans the full overlay width (rail included).
        if (rects.TryGetValue("headerDiv", out var header) && rects.TryGetValue("overlay", out var overlay))
        {
            deviations.Add(new GuideDeviation("header-left", overlay.Left, header.Left));
            deviations.Add(new GuideDeviation("header-right", overlay.Right, header.Right));
        }

        return deviations;
    }

    private static GuideDeviation Spread(string name, List<double> values)
    {
        var min = values.Min();
        var max = values.Max();
        return new GuideDeviation(name, min, max);
    }

    /// Clipping net for right-hand metadata: status, engine, session and
    /// action rects must end at or before the content column's right edge
    /// (ellipsis and trimming own the text; the layout never overflows).
    /// Only overshoot counts: contained rects read Δ0.0 whatever their
    /// inset, overflows read their exact overshoot. Missing rects (hidden
    /// pages) skip.
    public static IReadOnlyList<GuideDeviation> CheckClipping(
        IReadOnlyDictionary<string, Rect> rects, string bound, params string[] names)
    {
        var deviations = new List<GuideDeviation>();
        if (!rects.TryGetValue(bound, out var edge))
        {
            return deviations;
        }

        foreach (var name in names)
        {
            if (rects.TryGetValue(name, out var r))
            {
                deviations.Add(new GuideDeviation(
                    $"clip-{name}", edge.Right, r.Right <= edge.Right ? edge.Right : r.Right));
            }
        }

        return deviations;
    }

    /// Optical-ink check: the visible vector ink centre versus its slot
    /// centre, per axis. Deliberate glyph asymmetry (the search handle,
    /// the rabbit's ear mass) reads here as data, not failure — these are
    /// INFO lines, never PASS/FAIL. They justify an optical nudge inside
    /// the drawing, or document why none was made; shared column geometry
    /// is never bent to compensate for a symbol.
    public static (double Dx, double Dy) InkOffset(Rect slot, Rect ink)
        => (ink.Center.X - slot.Center.X, ink.Center.Y - slot.Center.Y);

    /// Viewport fill for capped panels (Overview/Settings): at and above
    /// the reading cap the content must span exactly the cap regardless of
    /// how much text it holds, so empty and populated states share one
    /// width. Below the cap, stretch rules plus scrollbar gutters apply
    /// and there is nothing exact to assert — returns null (skipped).
    public static GuideDeviation? CheckFill(Avalonia.Rect content, Avalonia.Rect viewport, double maxWidth)
    {
        if (viewport.Width < maxWidth)
        {
            return null;
        }

        return new GuideDeviation("panel-fill", maxWidth, content.Width);
    }
}
