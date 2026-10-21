using AgentX.App;
using Avalonia;

namespace AgentX.App.Tests;

/// <summary>Layout-audit geometry: relative datum checks over synthetic
/// rects — aligned groups pass, real offsets are reported exactly.</summary>
public sealed class LayoutAuditTests
{
    private static Rect At(double x, double y, double w, double h) => new(x, y, w, h);

    [Fact]
    public void Check_AlignedShell_PassesWithinTolerance()
    {
        var rects = new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
            ["cardsDiv"] = At(56, 128, 1100, 1),
            ["feed"] = At(56, 136, 1100, 400),
            ["composer"] = At(56, 542, 1100, 60),
            ["footer"] = At(56, 608, 1100, 30),
            ["rabbit"] = At(20, 8, 20, 20),
            ["nav"] = At(21, 60, 18, 18),
            ["brand"] = At(56, 8, 200, 20),
            ["send"] = At(1100, 560, 32, 32),
            ["input"] = At(64, 560, 1028, 32),
            ["headerDiv"] = At(0, 50, 1156, 1),
            ["overlay"] = At(0, 0, 1156, 700),
        };
        var deviations = LayoutAudit.Check(rects);
        Assert.NotEmpty(deviations);
        Assert.All(deviations, d => Assert.True(
            d.Delta <= LayoutAudit.Tolerance, $"{d.Name} deviates by {d.Delta}"));
    }

    [Fact]
    public void CheckClipping_ContainedMetadata_Passes()
    {
        var rects = new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
            ["status"] = At(900, 140, 200, 16),
            ["engine"] = At(800, 600, 356, 16),
            ["add"] = At(1124, 60, 32, 40),
        };
        var deviations = LayoutAudit.CheckClipping(rects, "cards", "status", "engine", "session", "add");
        Assert.Equal(3, deviations.Count); // Missing "session" skips.
        Assert.All(deviations, d => Assert.True(d.Delta <= LayoutAudit.Tolerance, $"{d.Name} deviates by {d.Delta}"));
    }

    [Fact]
    public void CheckClipping_Overflow_ReportsExactOvershoot()
    {
        var rects = new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
            ["engine"] = At(800, 600, 360, 16),
        };
        var over = Assert.Single(LayoutAudit.CheckClipping(rects, "cards", "engine"));
        Assert.Equal("clip-engine", over.Name);
        Assert.Equal(4, over.Delta);
    }

    [Fact]
    public void InkOffset_CentredInk_ReadsZero()
    {
        Assert.Equal((0, 0), LayoutAudit.InkOffset(At(0, 0, 20, 20), At(4, 4, 12, 12)));
    }

    [Fact]
    public void InkOffset_OffCentreInk_ReadsExactAxes()
    {
        // Slot centre (10,10); ink centre (10.4,9.5): a right-heavy, high
        // symbol documents its nudge instead of bending the column.
        var (dx, dy) = LayoutAudit.InkOffset(At(0, 0, 20, 20), At(4.4, 3.5, 12, 12));
        Assert.Equal(0.4, dx, precision: 9);
        Assert.Equal(-0.5, dy, precision: 9);
    }

    [Fact]
    public void CheckRailInk_ComparesGlobalInkCentres()
    {
        // Nav ink at 100/104/108 (mean 104); rabbit ink at 107: the
        // verdict is the 3px global offset, wherever the slots sit.
        var rects = new Dictionary<string, Rect>
        {
            ["ink-rabbit"] = At(102, 8, 10, 10),
            ["ink-nav-overview"] = At(95, 60, 10, 10),
            ["ink-nav-feed"] = At(99, 110, 10, 10),
            ["ink-nav-settings"] = At(103, 500, 10, 10),
        };
        var verdict = LayoutAudit.CheckRailInk(rects);
        Assert.NotNull(verdict);
        Assert.Equal("rail-ink", verdict!.Name);
        Assert.Equal(3, verdict.Delta, precision: 9);
        // Missing rabbit or nav skips instead of inventing a verdict.
        Assert.Null(LayoutAudit.CheckRailInk(new Dictionary<string, Rect>()));
    }

    [Fact]
    public void Check_OffsetContentEdge_ReportsExactDelta()
    {
        var rects = new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
            ["feed"] = At(59, 136, 1097, 400),
            ["composer"] = At(56, 542, 1100, 60),
        };
        var deviations = LayoutAudit.Check(rects);
        var left = Assert.Single(deviations, d => d.Name == "content-left");
        Assert.Equal(3, left.Delta);
    }

    [Fact]
    public void Check_RabbitOffRail_FlagsCentres()
    {
        var rects = new Dictionary<string, Rect>
        {
            ["rabbit"] = At(10, 8, 20, 20),
            ["nav"] = At(20, 60, 18, 18),
        };
        var deviations = LayoutAudit.Check(rects);
        var datum = Assert.Single(deviations, d => d.Name == "rail-brand");
        // Rabbit centre 20 vs icon centre 29: the pre-B2 header offset.
        Assert.Equal(9, datum.Delta);
    }

    [Fact]
    public void CheckFill_EmptyAndPopulated_ShareOneWidth()
    {
        // Viewport 1156, cap 900: content spans the cap either way.
        var viewport = At(0, 0, 1156, 700);
        Assert.Equal(0, LayoutAudit.CheckFill(At(56, 0, 900, 200), viewport, 900)!.Delta);
        Assert.Equal(0, LayoutAudit.CheckFill(At(56, 0, 900, 600), viewport, 900)!.Delta);
    }

    [Fact]
    public void CheckFill_NarrowViewport_SkippedBelowCap()
    {
        var viewport = At(0, 0, 700, 700);
        Assert.Null(LayoutAudit.CheckFill(At(0, 0, 700, 200), viewport, 900));
    }

    [Fact]
    public void CheckFill_ShrunkContent_ReportsExactDelta()
    {
        var viewport = At(0, 0, 1156, 700);
        var deviation = LayoutAudit.CheckFill(At(56, 0, 450, 200), viewport, 900)!;
        Assert.Equal("panel-fill", deviation.Name);
        Assert.Equal(450, deviation.Delta);
    }

    [Fact]
    public void Check_MissingRects_SkippedNeverFail()
    {
        var deviations = LayoutAudit.Check(new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
        });
        Assert.Empty(deviations);
    }
}
