using AgentX.App;
using Avalonia.Media;
using Xunit;

namespace AgentX.App.Tests;

public sealed class ActivityPulseTests
{
    [Fact]
    public void Idle_Is_Gray_Whatever_The_Tick()
    {
        var idle = Color.Parse(ActivityPulse.Idle);
        Assert.Equal(idle, ActivityPulse.ColorFor(0, busy: false));
        Assert.Equal(idle, ActivityPulse.ColorFor(999, busy: false));
    }

    [Fact]
    public void Busy_Hits_Both_Palette_Ends()
    {
        // sin = +1 at tick 5 → Cyan; sin = −1 at tick 15 → Green.
        Assert.Equal(Color.Parse(PiPalette.Cyan), ActivityPulse.ColorFor(5, busy: true));
        Assert.Equal(Color.Parse(PiPalette.Green), ActivityPulse.ColorFor(15, busy: true));
    }

    [Fact]
    public void Busy_Stays_Inside_The_Green_Cyan_Segment()
    {
        var green = Color.Parse(PiPalette.Green);
        var cyan = Color.Parse(PiPalette.Cyan);
        for (var tick = 0; tick < 40; tick++)
        {
            var c = ActivityPulse.ColorFor(tick, busy: true);
            Assert.InRange((int)c.R, System.Math.Min((int)green.R, (int)cyan.R), System.Math.Max((int)green.R, (int)cyan.R));
            Assert.InRange((int)c.G, System.Math.Min((int)green.G, (int)cyan.G), System.Math.Max((int)green.G, (int)cyan.G));
            Assert.InRange((int)c.B, System.Math.Min((int)green.B, (int)cyan.B), System.Math.Max((int)green.B, (int)cyan.B));
        }
    }

    [Fact]
    public void Same_Tick_Same_Color()
    {
        Assert.Equal(ActivityPulse.ColorFor(7, busy: true), ActivityPulse.ColorFor(7, busy: true));
    }

    [Fact]
    public void Breathe_Repeats_Every_Twenty_Ticks()
    {
        Assert.Equal(ActivityPulse.ColorFor(3, busy: true), ActivityPulse.ColorFor(23, busy: true));
    }
}
