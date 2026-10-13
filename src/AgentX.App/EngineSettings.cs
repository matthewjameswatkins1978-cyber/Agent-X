namespace AgentX.App;

/// <summary>One engine-reported model (provider + id + display text).</summary>
public sealed record EngineModel(string Provider, string ModelId, string DisplayName);

/// <summary>
/// App-side boundary for engine settings that Agent ONE owns in Core: model
/// list/selection and thinking-level list/selection. The App calls ONLY
/// through this interface; when Core ships the verified operations, the
/// pending implementation below is replaced by a thin Core-bound one at the
/// single construction site in MainWindow. Nothing here invents a worker
/// manager or a competing protocol path.
/// </summary>
public interface IEngineSettingsRuntime
{
    bool CanConfigure { get; }

    string UnavailableReason { get; }

    Task<IReadOnlyList<EngineModel>> ListModelsAsync(CancellationToken cancellationToken);

    Task SelectModelAsync(string provider, string modelId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListThinkingLevelsAsync(CancellationToken cancellationToken);

    Task SelectThinkingLevelAsync(string level, CancellationToken cancellationToken);
}

/// <summary>
/// Stand-in while Core has no client operation for models/thinking. The Pi
/// wire defines get_available_models/set_model/set_thinking_level, but
/// AgentX.Core exposes none — that contract is Agent ONE's to ship.
/// Every mutating operation reports the same explicit reason, which the
/// footer surfaces instead of presenting selection as functional.
/// </summary>
public sealed class PendingEngineSettingsRuntime : IEngineSettingsRuntime
{
    public bool CanConfigure => false;

    public string UnavailableReason =>
        "Model and thinking selection need the Core engine operation (Agent ONE, pending) — not yet available.";

    public Task<IReadOnlyList<EngineModel>> ListModelsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task SelectModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task<IReadOnlyList<string>> ListThinkingLevelsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task SelectThinkingLevelAsync(string level, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);
}
