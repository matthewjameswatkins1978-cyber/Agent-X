using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// Live-activity colour ride: while an agent run is genuinely live the dot
/// travels the Pi palette (green → cyan → blue → violet → yellow → green)
/// on a slow loop, still gray the moment work settles. Pure and
/// deterministic so the motion law is unit-tested; the window advances the
/// tick from the flush cadence (10 Hz) only while busy flags hold, so
/// motion exists if and only if work is happening. Dedicated brush
/// instances only — theme brushes are shared and must never be mutated.
/// </summary>
public static class ActivityPulse
{
    public const string Idle = "#6B7280";

    private static readonly Color Green = Color.Parse(PiPalette.Green);
    private static readonly Color Cyan = Color.Parse(PiPalette.Cyan);
    private static readonly Color Blue = Color.Parse(PiPalette.Blue);
    private static readonly Color Violet = Color.Parse(PiPalette.Violet);
    private static readonly Color Yellow = Color.Parse(PiPalette.Yellow);

    private static readonly Color[] Stops = [Green, Cyan, Blue, Violet, Yellow];

    /// <param name="tick">Flush-tick count; advances only while busy.</param>
    /// <param name="busy">Any worker run or workspace run live.</param>
    public static Color ColorFor(long tick, bool busy)
    {
        if (!busy)
        {
            return Color.Parse(Idle);
        }

        // Five 1s legs, smooth-eased between neighbouring stops.
        var position = (tick % 50) / 10.0;
        var index = (int)position % Stops.Length;
        var t = position - (int)position;
        var eased = t * t * (3 - 2 * t);
        return Lerp(Stops[index], Stops[(index + 1) % Stops.Length], eased);
    }

    internal static Color Lerp(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)(x + (y - x) * t);
        return new Color(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }
}
