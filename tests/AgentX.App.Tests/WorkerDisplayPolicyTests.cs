using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>
/// B5 review corrections: selection-scoped display with no cross-identity
/// fallback (engine attribution, working indicator, removal repair).
/// Deterministic, headless.
/// </summary>
public sealed class WorkerDisplayPolicyTests
{
    private static PiEngineState Confirmed() => new(
        "meta", "meta/mega", "meta", "high", false, false, "axw-aa", 12, 0, "D:/s/axw-aa.jsonl");

    [Fact]
    public void EngineForWorker_NullMeansUnknownNeverLegacy()
    {
        var (provider, model, thinking, messages) = WorkerDisplayPolicy.EngineForWorker(null);
        Assert.Null(provider);
        Assert.Null(model);
        Assert.Null(thinking);
        Assert.Null(messages);
    }

    [Fact]
    public void EngineForWorker_ConfirmedStateReportedVerbatim()
    {
        var (provider, model, thinking, messages) = WorkerDisplayPolicy.EngineForWorker(Confirmed());
        Assert.Equal("meta", provider);
        Assert.Equal("meta/mega", model);
        Assert.Equal("high", thinking);
        Assert.Equal(12, messages);
    }

    [Fact]
    public void EngineForWorker_FallsBackToModelId()
    {
        var state = Confirmed() with { ModelName = null, ModelId = "meta/mini" };
        Assert.Equal("meta/mini", WorkerDisplayPolicy.EngineForWorker(state).Model);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShowWorking_FollowsViewedRunOnly(bool viewedIsBusy, bool expected)
    {
        Assert.Equal(expected, WorkerDisplayPolicy.ShowWorking(viewedIsBusy));
    }

    [Fact]
    public void RepairTarget_WorkspaceViewRepairsNothing()
    {
        Assert.Null(WorkerDisplayPolicy.RepairTarget(null, ["a", "b"], "a"));
    }

    [Fact]
    public void RepairTarget_SurvivingViewReselectedUntouched()
    {
        Assert.Equal("b", WorkerDisplayPolicy.RepairTarget("b", ["a", "b"], "a"));
    }

    [Fact]
    public void RepairTarget_VanishedViewFallsToManagerSelection()
    {
        Assert.Equal("a", WorkerDisplayPolicy.RepairTarget("gone", ["a"], "a"));
    }

    [Fact]
    public void RepairTarget_NoSurvivorsFallsToLegacy()
    {
        Assert.Null(WorkerDisplayPolicy.RepairTarget("gone", [], null));
    }

    [Fact]
    public void StripOrder_LunaRepro_WorkerSurvivesWorkspaceRegistrations()
    {
        // project-a worker created first, then project-b and project-c
        // registered: every population stays visible, catalog first.
        var order = WorkerDisplayPolicy.StripOrder(
            ["legacy", "proj-b", "proj-c"], ["worker-a"]);
        Assert.Equal(["legacy", "proj-b", "proj-c", "worker-a"], order);
    }

    [Fact]
    public void StripOrder_MixedPopulation_KeepsBothOrders()
    {
        var order = WorkerDisplayPolicy.StripOrder(
            ["legacy", "b", "c"], ["w1", "w2"]);
        Assert.Equal(["legacy", "b", "c", "w1", "w2"], order);
    }

    [Fact]
    public void StripOrder_DuplicateKeyKeptOnceCatalogWins()
    {
        var order = WorkerDisplayPolicy.StripOrder(["legacy", "x"], ["x", "w"]);
        Assert.Equal(["legacy", "x", "w"], order);
    }

    [Fact]
    public void StripOrder_EmptyWorkersIsCatalogOnly()
    {
        var order = WorkerDisplayPolicy.StripOrder(["legacy", "b"], []);
        Assert.Equal(["legacy", "b"], order);
    }

    [Theory]
    [InlineData(null, "a", true)]
    [InlineData("a", "a", false)]
    [InlineData("a", "b", true)]
    [InlineData("a", null, false)]
    public void ShouldReveal_OnlyOnSelectionChange(string? last, string? selected, bool expected)
    {
        Assert.Equal(expected, WorkerDisplayPolicy.ShouldReveal(last, selected));
    }
}
