using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// Live-activity pulse: a subtle palette breathe shown while an agent run
/// is genuinely live, still the moment it settles. Pure and deterministic
/// so the motion law is unit-tested; the window advances the tick from the
/// flush cadence (10 Hz) only while busy flags hold, so motion exists if
/// and only if work is happening. Dedicated brush instances only — theme
/// brushes are shared and must never be mutated per-tick.
/// </summary>
public static class ActivityPulse
{
    public const string Idle = "#6B7280";

    private static readonly Color Green = Color.Parse(PiPalette.Green);
    private static readonly Color Cyan = Color.Parse(PiPalette.Cyan);

    /// <param name="tick">Flush-tick count; advances only while busy.</param>
    /// <param name="busy">Any worker run or workspace run live.</param>
    public static Color ColorFor(long tick, bool busy)
    {
        if (!busy)
        {
            return Color.Parse(Idle);
        }

        // One 2s breathe per 20 ticks: sine eases Green → Cyan → Green.
        var phase = (tick % 20) / 20.0 * 2 * Math.PI;
        var t = (System.Math.Sin(phase) + 1) / 2;
        return Lerp(Green, Cyan, t);
    }

    internal static Color Lerp(Color a, Color b, double t)
    {
        byte Mix(byte x, byte y) => (byte)(x + (y - x) * t);
        return new Color(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }
}
