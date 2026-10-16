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
    public void Check_MissingRects_SkippedNeverFail()
    {
        var deviations = LayoutAudit.Check(new Dictionary<string, Rect>
        {
            ["cards"] = At(56, 60, 1100, 60),
        });
        Assert.Empty(deviations);
    }
}
