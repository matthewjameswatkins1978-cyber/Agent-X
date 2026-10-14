namespace AgentX.App;

/// <summary>One engine-reported model (provider + id + display text).</summary>
public sealed record EngineModel(string Provider, string ModelId, string DisplayName);

/// <summary>
/// App-side boundary for engine settings that Agent ONE owns in Core: model
/// list/selection and thinking-level list/selection. The App calls ONLY
/// through this interface. Core ships CoreEngineSettingsRuntime as the
/// thin Core-bound implementation over PiWorker; swapping it in at the
/// single construction site in MainWindow is activation, deliberately
/// deferred to the native chooser slice (issue #23, Muse) so the footer
/// never offers a picker that cannot choose. Nothing here invents a worker
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
/// Stand-in while the Core-bound runtime is not yet activated. Core now
/// ships CoreEngineSettingsRuntime over the validated PiWorker operations;
/// activation waits for the native chooser (issue #23, Muse) so this
/// surface never presents selection as functional before a real picker
/// exists. Every mutating operation reports the same explicit reason,
/// which the footer surfaces instead of pretending selection works.
/// </summary>
public sealed class PendingEngineSettingsRuntime : IEngineSettingsRuntime
{
    public bool CanConfigure => false;

    public string UnavailableReason =>
        "Model and thinking selection activate with the native chooser (issue #23); the Core adapter is ready but not yet wired.";

    public Task<IReadOnlyList<EngineModel>> ListModelsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task SelectModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task<IReadOnlyList<string>> ListThinkingLevelsAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);

    public Task SelectThinkingLevelAsync(string level, CancellationToken cancellationToken)
        => throw new NotSupportedException(UnavailableReason);
}
