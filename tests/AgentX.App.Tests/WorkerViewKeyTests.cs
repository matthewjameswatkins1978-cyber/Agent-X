using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// B5 view-key contract: worker state is namespaced by persistent id, workspace
/// keys pass through untouched (no migration), prune allowlists union both sides.
/// Deterministic, headless.
/// </summary>
public sealed class WorkerViewKeyTests
{
    [Fact]
    public void ForWorker_NamespacesById()
    {
        Assert.Equal("worker/59ffa00c", WorkerViewKey.ForWorker("59ffa00c"));
    }

    [Fact]
    public void ForWorker_RejectsBlankId()
    {
        Assert.Throws<ArgumentException>(() => WorkerViewKey.ForWorker("  "));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("ax-b4-test")]
    [InlineData("59ffa00c")]
    [InlineData("worker")]
    [InlineData("WORKER/x")]
    [InlineData(null)]
    [InlineData("")]
    public void WorkspaceKeys_PassThroughUntouched(string? key)
    {
        Assert.False(WorkerViewKey.IsWorkerKey(key));
        Assert.Null(WorkerViewKey.WorkerIdOf(key));
    }

    [Fact]
    public void WorkerKey_RoundTrips()
    {
        var key = WorkerViewKey.ForWorker("abc12345");
        Assert.True(WorkerViewKey.IsWorkerKey(key));
        Assert.Equal("abc12345", WorkerViewKey.WorkerIdOf(key));
    }

    [Fact]
    public void DisplayLabel_PrefersStoredLabel_Trims()
    {
        Assert.Equal("Agent ONE", WorkerViewKey.DisplayLabel("59ffa00c", "  Agent ONE  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayLabel_FallsBackToShortId(string? label)
    {
        Assert.Equal("Agent 59ff", WorkerViewKey.DisplayLabel("59ffa00c", label));
    }

    [Theory]
    [InlineData("Worker 1", "Agent 1")]
    [InlineData("Worker 12", "Agent 12")]
    public void DisplayLabel_MapsAutoWorkerToAgent(string? label, string expected)
    {
        Assert.Equal(expected, WorkerViewKey.DisplayLabel("59ffa00c", label));
    }

    [Fact]
    public void Tooltip_CarriesSessionAndRoot()
    {
        var tip = WorkerViewKey.Tooltip("59ffa00c", "Agent ONE", "axw-59ffa00c", "D:/Work/one");
        Assert.Contains("Agent ONE", tip);
        Assert.Contains("axw-59ffa00c", tip);
        Assert.Contains("D:/Work/one", tip);
    }

    [Fact]
    public void MergeKnownKeys_UnionsBothSides()
    {
        var merged = WorkerViewKey.MergeKnownKeys(["legacy", "proj"], ["aa", "bb"]);
        Assert.Equal(4, merged.Length);
        Assert.Contains("legacy", merged);
        Assert.Contains("proj", merged);
        Assert.Contains("worker/aa", merged);
        Assert.Contains("worker/bb", merged);
    }

    [Fact]
    public void MergeKnownKeys_AbsentSidePrunesNothing()
    {
        Assert.Equal(["legacy"], WorkerViewKey.MergeKnownKeys(["legacy"], null));
        Assert.Equal(["worker/aa"], WorkerViewKey.MergeKnownKeys(null, ["aa"]));
        Assert.Empty(WorkerViewKey.MergeKnownKeys(null, null));
    }

    [Fact]
    public void MergeKnownKeys_SkipsBlankWorkerIds()
    {
        Assert.Equal(["worker/aa"], WorkerViewKey.MergeKnownKeys(null, ["aa", "  ", ""]));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void CanRemove_RefusesActiveWorkers(bool busy, bool connecting, bool expected)
    {
        Assert.Equal(expected, WorkerViewKey.CanRemove(busy, connecting));
    }
}
