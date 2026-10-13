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
}
