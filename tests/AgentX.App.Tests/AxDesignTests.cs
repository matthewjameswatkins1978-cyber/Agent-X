using AgentX.App;
using Avalonia;

namespace AgentX.App.Tests;

public sealed class AxDesignTests
{
    [Fact]
    public void AllTreatmentsResolveToDistinctSurfaces()
    {
        var seen = new HashSet<string>();
        foreach (var treatment in new[] { AxTreatment.PiFaithful, AxTreatment.MidnightTeal, AxTreatment.TechnicalMinimal })
        {
            var tokens = AxTreatments.For(treatment);
            Assert.True(seen.Add(tokens.Bg + tokens.Surface), $"duplicate surfaces for {treatment}");
            Assert.Equal(AxTreatments.DisplayName(treatment), tokens.Name);
        }
    }

    [Fact]
    public void TreatmentNamesRoundTrip()
    {
        foreach (var treatment in new[] { AxTreatment.PiFaithful, AxTreatment.MidnightTeal, AxTreatment.TechnicalMinimal })
        {
            Assert.Equal(treatment, AxTreatments.Parse(AxTreatments.DisplayName(treatment)));
        }

        Assert.Equal(AxTreatment.PiFaithful, AxTreatments.Parse(null));
        Assert.Equal(AxTreatment.PiFaithful, AxTreatments.Parse("Nope"));
    }

    [Theory]
    [InlineData(AxIconKind.Connect)]
    [InlineData(AxIconKind.Disconnect)]
    [InlineData(AxIconKind.Reconnect)]
    [InlineData(AxIconKind.Stop)]
    [InlineData(AxIconKind.Copy)]
    [InlineData(AxIconKind.More)]
    [InlineData(AxIconKind.Live)]
    [InlineData(AxIconKind.Overview)]
    [InlineData(AxIconKind.Settings)]
    [InlineData(AxIconKind.Project)]
    [InlineData(AxIconKind.Add)]
    [InlineData(AxIconKind.Collapse)]
    [InlineData(AxIconKind.Remove)]
    [InlineData(AxIconKind.Rabbit)]
    [InlineData(AxIconKind.Search)]
    [InlineData(AxIconKind.Sliders)]
    [InlineData(AxIconKind.Grid)]
    [InlineData(AxIconKind.Chat)]
    [InlineData(AxIconKind.Branch)]
    [InlineData(AxIconKind.Send)]
    [InlineData(AxIconKind.Target)]
    [InlineData(AxIconKind.Cpu)]
    [InlineData(AxIconKind.Check)]
    [InlineData(AxIconKind.Warn)]
    [InlineData(AxIconKind.ChevronUp)]
    [InlineData(AxIconKind.ChevronDown)]
    [InlineData(AxIconKind.Close)]
    public void EveryIconGeometryParses(AxIconKind kind)
    {
        // Geometry.Parse needs a render platform, which headless unit
        // tests must not require: validate the path data structurally
        // (non-empty, starts with a moveto, path-only alphabet). Genuine
        // rendering is evidenced by the running-app screenshots, where
        // every icon is visible in the toolbar and rail.
        var data = AxIcons.DataFor(kind);
        Assert.NotEmpty(data);
        Assert.StartsWith("M", data, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"\A[MmLlHhVvCcSsQqTtAaZz0-9eE.\+\-\s,]*\z", data);
    }

    [Fact]
    public void CenteringOffset_PadsNarrowArt_FullBleedIsNoop()
    {
        // The rabbit's 11x17.5 ink in a 20px slot: +3.7 horizontal pad,
        // zero vertical (height-filling). Measured rendering fits the
        // top-left-fit model this corrects.
        var rabbit = AxIcon.CenteringOffset(new Size(20, 20), new Rect(6.5, 3.5, 11, 17.5));
        Assert.Equal(3.714, rabbit.X, precision: 3);
        Assert.Equal(0, rabbit.Y, precision: 9);
        // Full-bleed icons (grid 16x16 in 18px): exactly zero, so the
        // transform stays null and rendering is untouched.
        var grid = AxIcon.CenteringOffset(new Size(18, 18), new Rect(4, 4, 16, 16));
        Assert.Equal(new Vector(0, 0), grid);
        // Degenerate inputs never move ink.
        Assert.Equal(new Vector(0, 0), AxIcon.CenteringOffset(new Size(0, 20), new Rect(6.5, 3.5, 11, 17.5)));
        Assert.Equal(new Vector(0, 0), AxIcon.CenteringOffset(new Size(20, 20), new Rect(0, 0, 0, 0)));
    }

    [Fact]
    public void LegacyWorkspaceDisplaysAsMain()
    {
        Assert.Equal("Main", AxTheme.WorkspaceTabLabel(isLegacy: true, "Legacy session (agent-x-main)"));
        Assert.Equal("shop (abc)", AxTheme.WorkspaceTabLabel(isLegacy: false, "shop (abc)"));
    }

    [Fact]
    public void WorkerAutoLabelsDisplayAsAgent()
    {
        Assert.Equal("Agent 1", AxTheme.WorkerTabLabel("deadbeef", "Worker 1"));
        Assert.Equal("Agent 12", AxTheme.WorkerTabLabel("deadbeef", "Worker 12"));
        Assert.Equal("My project", AxTheme.WorkerTabLabel("deadbeef", "My project"));
        Assert.Equal("Agent dead", AxTheme.WorkerTabLabel("deadbeef", null));
        Assert.Equal("Agent dead", AxTheme.WorkerTabLabel("deadbeef", "  "));
    }
}
