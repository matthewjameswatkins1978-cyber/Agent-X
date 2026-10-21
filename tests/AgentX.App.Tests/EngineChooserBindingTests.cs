using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>
/// Slice 1 review fixes: worker-bound choosers, list-and-set capability
/// gating, and the binding rule. Headless — a fake IWorkerEngineSettings
/// stands in for live Pi workers; no Pi process, network, or GUI.
/// </summary>
public sealed class EngineChooserBindingTests
{
    private sealed class FakeWorker : IWorkerEngineSettings
    {
        public bool IsConnected { get; set; } = true;

        public PiRpcCapabilities? Capabilities { get; set; } =
            new(true, true, true, true, true, true);

        public List<PiModelOption> Models { get; } =
        [
            new("anthropic", "claude-x", "Claude X"),
        ];

        public List<string> ThinkingLevels { get; } = ["low", "high"];

        public string? LastSelectedModel { get; private set; }
        public string? LastSelectedLevel { get; private set; }

        public Task<IReadOnlyList<PiModelOption>> GetModelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<PiModelOption>>(Models);

        public Task<PiEngineState> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        {
            LastSelectedModel = modelId;
            return Task.FromResult(new PiEngineState(modelId, modelId, "fake", "high", false, false, "sess", 1, 0));
        }

        public Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(ThinkingLevels);

        public Task<PiEngineState> SetThinkingLevelAsync(string level, CancellationToken cancellationToken)
        {
            LastSelectedLevel = level;
            return Task.FromResult(new PiEngineState("m", "m", "fake", level, false, false, "sess", 1, 0));
        }
    }

    // ---- binding rule ---------------------------------------------------

    [Fact]
    public void MayApplyTo_SameWorker_Allows()
        => Assert.True(EngineChooserPolicy.MayApplyTo("abc", "abc"));

    [Fact]
    public void MayApplyTo_DifferentWorker_Refuses()
        => Assert.False(EngineChooserPolicy.MayApplyTo("worker-a", "worker-b"));

    [Fact]
    public void MayApplyTo_NoCurrentWorker_Refuses()
        => Assert.False(EngineChooserPolicy.MayApplyTo("worker-a", null));

    // ---- model gating ----------------------------------------------------

    [Fact]
    public void Models_FullPair_Available()
        => Assert.Null(CoreEngineSettingsRuntime.DescribeModelsUnavailable(new FakeWorker()));

    [Fact]
    public void Models_ListOnly_HonestUnavailable()
    {
        var reason = CoreEngineSettingsRuntime.DescribeModelsUnavailable(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, true, false, true, true),
        });

        Assert.NotNull(reason);
        Assert.Contains("cannot apply", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Models_SetWithoutList_HonestUnavailable()
    {
        var reason = CoreEngineSettingsRuntime.DescribeModelsUnavailable(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, false, true, true, true),
        });

        Assert.NotNull(reason);
        Assert.Contains("nothing to choose", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Models_NeitherPair_NoSupport()
    {
        var reason = CoreEngineSettingsRuntime.DescribeModelsUnavailable(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, false, false, true, true),
        });

        Assert.NotNull(reason);
        Assert.Contains("model selection support", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- thinking gating --------------------------------------------------

    [Fact]
    public void Thinking_FullPair_Available()
        => Assert.Null(CoreEngineSettingsRuntime.DescribeThinkingUnavailable(new FakeWorker()));

    [Fact]
    public void Thinking_ListOnly_HonestUnavailable()
    {
        var reason = CoreEngineSettingsRuntime.DescribeThinkingUnavailable(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, true, true, true, false),
        });

        Assert.NotNull(reason);
        Assert.Contains("cannot apply", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Thinking_NeitherPair_NoSupport()
    {
        var reason = CoreEngineSettingsRuntime.DescribeThinkingUnavailable(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, true, true, false, false),
        });

        Assert.NotNull(reason);
        Assert.Contains("thinking-level selection support", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---- dimensions are independent ----------------------------------------

    [Fact]
    public void Dimensions_Independent_ModelsOnly()
    {
        var worker = new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(true, true, true, true, false, false),
        };
        var runtime = new CoreEngineSettingsRuntime(() => worker);

        Assert.True(runtime.CanConfigureModels);
        Assert.False(runtime.CanConfigureThinking);
        Assert.True(runtime.CanConfigure);
        Assert.NotEmpty(runtime.ThinkingUnavailableReason);
    }

    // ---- shared unavailable states ------------------------------------------

    [Fact]
    public void NullWorker_BothDimensionsReportNoWorker()
    {
        Assert.Equal("No worker is selected.",
            CoreEngineSettingsRuntime.DescribeModelsUnavailable(null));
        Assert.Equal("No worker is selected.",
            CoreEngineSettingsRuntime.DescribeThinkingUnavailable(null));
    }

    [Fact]
    public void DisconnectedWorker_BothDimensionsReportConnection()
    {
        var worker = new FakeWorker { IsConnected = false };

        Assert.Contains("not connected",
            CoreEngineSettingsRuntime.DescribeModelsUnavailable(worker),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not connected",
            CoreEngineSettingsRuntime.DescribeThinkingUnavailable(worker),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnprobedWorker_BothDimensionsReportProbe()
    {
        var worker = new FakeWorker { Capabilities = null };

        Assert.Contains("not probed",
            CoreEngineSettingsRuntime.DescribeModelsUnavailable(worker),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not probed",
            CoreEngineSettingsRuntime.DescribeThinkingUnavailable(worker),
            StringComparison.OrdinalIgnoreCase);
    }
}
