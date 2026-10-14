using AgentX.App;
using AgentX.Core;

namespace AgentX.App.Tests;

/// <summary>
/// #23 Core half: CoreEngineSettingsRuntime over the existing validated
/// PiWorker operations. Deterministic and headless — a fake
/// IWorkerEngineSettings stands in for the live worker, so no Pi process,
/// network or GUI is involved. Covers: model/thinking selection pass-
/// through, honest unavailable states, failure propagation, and worker
/// isolation (operations resolve one worker per call and never reach a
/// second one).
/// </summary>
public sealed class CoreEngineSettingsRuntimeTests
{
    private sealed class FakeWorker : IWorkerEngineSettings
    {
        public bool IsConnected { get; set; } = true;

        public PiRpcCapabilities? Capabilities { get; set; } =
            new(true, true, true, true, true, true);

        public List<PiModelOption> Models { get; } =
        [
            new("anthropic", "claude-x", "Claude X"),
            new("openai", "gpt-y", "GPT Y"),
        ];

        public List<string> ThinkingLevels { get; } = ["none", "low", "high"];

        public string? LastSelectedProvider { get; private set; }
        public string? LastSelectedModel { get; private set; }
        public string? LastSelectedLevel { get; private set; }
        public int ModelListCalls { get; private set; }
        public int ThinkingListCalls { get; private set; }

        public Exception? FailNext { get; set; }

        public Task<IReadOnlyList<PiModelOption>> GetModelsAsync(CancellationToken cancellationToken)
        {
            RequireConnected();
            ModelListCalls++;
            if (FailNext is { } fail)
            {
                FailNext = null;
                throw fail;
            }

            return Task.FromResult<IReadOnlyList<PiModelOption>>(Models);
        }

        public Task<PiEngineState> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        {
            RequireConnected();
            if (FailNext is { } fail)
            {
                FailNext = null;
                throw fail;
            }

            if (!Models.Any(m => m.Provider == provider && m.Id == modelId))
            {
                throw new ArgumentException($"'{provider}/{modelId}' is not in this connection's available model list.", nameof(modelId));
            }

            LastSelectedProvider = provider;
            LastSelectedModel = modelId;
            return Task.FromResult(State(modelId));
        }

        public Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken)
        {
            RequireConnected();
            ThinkingListCalls++;
            if (FailNext is { } fail)
            {
                FailNext = null;
                throw fail;
            }

            return Task.FromResult<IReadOnlyList<string>>(ThinkingLevels);
        }

        public Task<PiEngineState> SetThinkingLevelAsync(string level, CancellationToken cancellationToken)
        {
            RequireConnected();
            if (FailNext is { } fail)
            {
                FailNext = null;
                throw fail;
            }

            if (!ThinkingLevels.Contains(level, StringComparer.OrdinalIgnoreCase))
            {
                // Pinned Pi silently accepts unknown levels; Core (and this
                // fake) refuse them instead of reporting a false success.
                throw new ArgumentException($"'{level}' is not one of this model's levels.", nameof(level));
            }

            LastSelectedLevel = level;
            return Task.FromResult(State("claude-x"));
        }

        /// <summary>Mirrors PiWorker's contract: connection failures are Core's to raise.</summary>
        private void RequireConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Worker is not connected. Call ConnectAsync first.");
            }
        }

        private static PiEngineState State(string modelId) =>
            new(modelId, modelId, "fake", "high", false, false, "sess", 1, 0);
    }

    private static CoreEngineSettingsRuntime RuntimeFor(IWorkerEngineSettings? worker) =>
        new(() => worker);

    // ---- selection ------------------------------------------------------

    [Fact]
    public async Task ListModels_MapsPiOptionsToEngineModels()
    {
        var runtime = RuntimeFor(new FakeWorker());

        var models = await runtime.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        Assert.Equal(new EngineModel("anthropic", "claude-x", "Claude X"), models[0]);
        Assert.Equal(new EngineModel("openai", "gpt-y", "GPT Y"), models[1]);
    }

    [Fact]
    public async Task ListThinkingLevels_ReturnsLiveLevels()
    {
        var runtime = RuntimeFor(new FakeWorker());

        var levels = await runtime.ListThinkingLevelsAsync(CancellationToken.None);

        Assert.Equal(["none", "low", "high"], levels);
    }

    [Fact]
    public async Task SelectModel_UsesExactProviderAndId()
    {
        var worker = new FakeWorker();
        var runtime = RuntimeFor(worker);

        await runtime.SelectModelAsync("openai", "gpt-y", CancellationToken.None);

        Assert.Equal("openai", worker.LastSelectedProvider);
        Assert.Equal("gpt-y", worker.LastSelectedModel);
    }

    [Fact]
    public async Task SelectThinkingLevel_UsesRequestedLevel()
    {
        var worker = new FakeWorker();
        var runtime = RuntimeFor(worker);

        await runtime.SelectThinkingLevelAsync("high", CancellationToken.None);

        Assert.Equal("high", worker.LastSelectedLevel);
    }

    // ---- unavailable workers -------------------------------------------

    [Fact]
    public void NoWorker_CannotConfigure_WithExplicitReason()
    {
        var runtime = RuntimeFor(null);

        Assert.False(runtime.CanConfigure);
        Assert.Equal("No worker is selected.", runtime.UnavailableReason);
    }

    [Fact]
    public async Task NoWorker_OperationsRefuseLoudly()
    {
        var runtime = RuntimeFor(null);

        var list = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ListModelsAsync(CancellationToken.None));
        var select = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectModelAsync("openai", "gpt-y", CancellationToken.None));
        var levels = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ListThinkingLevelsAsync(CancellationToken.None));
        var thinking = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectThinkingLevelAsync("high", CancellationToken.None));

        Assert.Contains("no worker is selected", list.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no worker is selected", select.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no worker is selected", levels.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no worker is selected", thinking.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisconnectedWorker_CannotConfigure_WithConnectionReason()
    {
        var runtime = RuntimeFor(new FakeWorker { IsConnected = false });

        Assert.False(runtime.CanConfigure);
        Assert.Equal("The selected worker is not connected to Pi.", runtime.UnavailableReason);
    }

    [Fact]
    public void UnprobedCapabilities_CannotConfigure_WithProbeReason()
    {
        var runtime = RuntimeFor(new FakeWorker { Capabilities = null });

        Assert.False(runtime.CanConfigure);
        Assert.Equal(
            "The selected worker's capabilities are not probed yet; connect first.",
            runtime.UnavailableReason);
    }

    [Fact]
    public void UnsupportedCapabilities_CannotConfigure_WithSupportReason()
    {
        var runtime = RuntimeFor(new FakeWorker
        {
            Capabilities = new PiRpcCapabilities(false, false, false, false, false, false),
        });

        Assert.False(runtime.CanConfigure);
        Assert.Equal(
            "This Pi connection does not report model or thinking selection support.",
            runtime.UnavailableReason);
    }

    [Fact]
    public async Task AvailabilityIsLive_DisconnectedLater_FailsLoudly()
    {
        // Reasons are computed per call, never cached: a worker that drops
        // its connection after a successful probe must refuse immediately.
        var worker = new FakeWorker();
        var runtime = RuntimeFor(worker);

        Assert.True(runtime.CanConfigure);
        worker.IsConnected = false;
        Assert.False(runtime.CanConfigure);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ListModelsAsync(CancellationToken.None));
    }

    // ---- failures -------------------------------------------------------

    [Fact]
    public async Task WorkerFailure_PropagatesUnchanged()
    {
        var worker = new FakeWorker
        {
            FailNext = new PiRpcCommandException("set_model", "provider rejected the request"),
        };
        var runtime = RuntimeFor(worker);

        var thrown = await Assert.ThrowsAsync<PiRpcCommandException>(
            () => runtime.SelectModelAsync("openai", "gpt-y", CancellationToken.None));

        Assert.Equal("set_model", thrown.Command);
        Assert.Contains("provider rejected the request", thrown.Message, StringComparison.Ordinal);
        // No optimistic success: the worker never recorded the selection.
        Assert.Null(worker.LastSelectedModel);
    }

    [Fact]
    public async Task UnknownModel_RefusesInsteadOfOptimisticSuccess()
    {
        var worker = new FakeWorker();
        var runtime = RuntimeFor(worker);

        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.SelectModelAsync("openai", "not-a-model", CancellationToken.None));

        Assert.Null(worker.LastSelectedModel);
    }

    [Fact]
    public async Task UnknownThinkingLevel_RefusesInsteadOfOptimisticSuccess()
    {
        var worker = new FakeWorker();
        var runtime = RuntimeFor(worker);

        await Assert.ThrowsAsync<ArgumentException>(
            () => runtime.SelectThinkingLevelAsync("extreme", CancellationToken.None));

        Assert.Null(worker.LastSelectedLevel);
    }

    // ---- worker isolation ----------------------------------------------

    [Fact]
    public async Task EachOperationResolvesItsOwnWorker_CurrentWorkerWins()
    {
        // Simulates tab switching between operations: every call resolves
        // the provider anew, so operations land on whichever worker is
        // current at that moment — never on a cached earlier one.
        IWorkerEngineSettings? current = new FakeWorker();
        var runtime = new CoreEngineSettingsRuntime(() => current);

        var first = Assert.IsType<FakeWorker>(current);
        await runtime.SelectModelAsync("openai", "gpt-y", CancellationToken.None);
        Assert.Equal("gpt-y", first.LastSelectedModel);

        var second = new FakeWorker();
        current = second;

        await runtime.SelectModelAsync("anthropic", "claude-x", CancellationToken.None);

        Assert.Equal("claude-x", second.LastSelectedModel);
        // The first worker saw only its own operation, not the later one.
        Assert.Equal("gpt-y", first.LastSelectedModel);
        Assert.Equal(0, first.ModelListCalls);
        Assert.Equal(0, second.ThinkingListCalls);
    }

    [Fact]
    public async Task ListingNeverTouchesASecondWorker()
    {
        var currentWorker = new FakeWorker();
        var otherWorker = new FakeWorker();
        var runtime = RuntimeFor(currentWorker);

        await runtime.ListModelsAsync(CancellationToken.None);
        await runtime.ListThinkingLevelsAsync(CancellationToken.None);

        Assert.Equal(1, currentWorker.ModelListCalls);
        Assert.Equal(1, currentWorker.ThinkingListCalls);
        Assert.Equal(0, otherWorker.ModelListCalls);
        Assert.Equal(0, otherWorker.ThinkingListCalls);
    }

    [Fact]
    public async Task NullMidSession_ReasonReflectsIt_AndOperationsRefuse()
    {
        // Closing/deselecting a worker between calls: the reason updates
        // live and operations refuse rather than acting on a stale worker.
        IWorkerEngineSettings? current = new FakeWorker();
        var runtime = new CoreEngineSettingsRuntime(() => current);

        Assert.True(runtime.CanConfigure);

        current = null;

        Assert.False(runtime.CanConfigure);
        Assert.Equal("No worker is selected.", runtime.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.SelectThinkingLevelAsync("high", CancellationToken.None));
    }
}
