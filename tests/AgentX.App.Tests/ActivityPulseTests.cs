using System.Text.Json;
using AgentX.Core;
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
    public void Ride_Hits_Every_Palette_Stop()
    {
        Assert.Equal(Color.Parse(PiPalette.Green), ActivityPulse.ColorFor(0, busy: true));
        Assert.Equal(Color.Parse(PiPalette.Cyan), ActivityPulse.ColorFor(10, busy: true));
        Assert.Equal(Color.Parse(PiPalette.Blue), ActivityPulse.ColorFor(20, busy: true));
        Assert.Equal(Color.Parse(PiPalette.Violet), ActivityPulse.ColorFor(30, busy: true));
        Assert.Equal(Color.Parse(PiPalette.Yellow), ActivityPulse.ColorFor(40, busy: true));
    }

    [Fact]
    public void Ride_Stays_Inside_The_Palette_Range()
    {
        var stops = new[]
        {
            Color.Parse(PiPalette.Green), Color.Parse(PiPalette.Cyan), Color.Parse(PiPalette.Blue),
            Color.Parse(PiPalette.Violet), Color.Parse(PiPalette.Yellow),
        };
        var lo = new int[3];
        var hi = new int[3];
        for (var c = 0; c < 3; c++)
        {
            var vals = c == 0 ? stops.Select(s => (int)s.R) : c == 1 ? stops.Select(s => (int)s.G) : stops.Select(s => (int)s.B);
            lo[c] = vals.Min();
            hi[c] = vals.Max();
        }

        for (var tick = 0; tick < 100; tick++)
        {
            var color = ActivityPulse.ColorFor(tick, busy: true);
            Assert.InRange((int)color.R, lo[0], hi[0]);
            Assert.InRange((int)color.G, lo[1], hi[1]);
            Assert.InRange((int)color.B, lo[2], hi[2]);
        }
    }

    [Fact]
    public void Same_Tick_Same_Color()
    {
        Assert.Equal(ActivityPulse.ColorFor(7, busy: true), ActivityPulse.ColorFor(7, busy: true));
    }

    [Fact]
    public void Ride_Repeats_Every_Fifty_Ticks()
    {
        Assert.Equal(ActivityPulse.ColorFor(3, busy: true), ActivityPulse.ColorFor(53, busy: true));
    }
}
