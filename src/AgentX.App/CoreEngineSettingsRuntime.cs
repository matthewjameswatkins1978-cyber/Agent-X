using AgentX.Core;

namespace AgentX.App;

/// <summary>
/// Narrow, per-worker view of the engine-settings surface that already
/// exists and is validated in Core: connection state, probed capabilities,
/// and the four confirmed <see cref="PiWorker"/> operations (list/select
/// model, list/select thinking level). One instance wraps ONE worker; it
/// never reaches across workers. Kept as an interface so
/// <see cref="CoreEngineSettingsRuntime"/> can be tested headlessly
/// without live Pi processes, while <see cref="PiWorkerEngineSettings"/>
/// provides the production binding by pure delegation.
/// </summary>
public interface IWorkerEngineSettings
{
    /// <summary>True when the wrapped worker holds a live Pi connection.</summary>
    bool IsConnected { get; }

    /// <summary>Probed capabilities of that connection; null until probed.</summary>
    PiRpcCapabilities? Capabilities { get; }

    /// <summary>Selectable models of THIS worker's connection (capability-gated in Core).</summary>
    Task<IReadOnlyList<PiModelOption>> GetModelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Switch THIS worker's model: Core validates against the live list and
    /// confirms via engine state before the task completes. Accepted-but-
    /// not-applied responses throw there, never surface as success here.
    /// </summary>
    Task<PiEngineState> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken);

    /// <summary>Thinking levels of THIS worker's current model (capability-gated in Core).</summary>
    Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Set THIS worker's thinking level: Core pre-validates against the live
    /// per-model list and confirms via engine state (Pinned Pi silently
    /// accepts unknown levels, so the confirmation is mandatory).
    /// </summary>
    Task<PiEngineState> SetThinkingLevelAsync(string level, CancellationToken cancellationToken);
}

/// <summary>
/// Production binding: straight delegation from one <see cref="PiWorker"/>
/// to its confirmed-state engine operations. No caching, no interpretation —
/// every error (not connected, capability missing, list mismatch,
/// confirmation failure) is Core's own typed exception passing through
/// unchanged so the UI reflects real state rather than optimism.
/// </summary>
public sealed class PiWorkerEngineSettings : IWorkerEngineSettings
{
    private readonly PiWorker _worker;

    public PiWorkerEngineSettings(PiWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        _worker = worker;
    }

    public bool IsConnected => _worker.IsConnected;

    public PiRpcCapabilities? Capabilities => _worker.Capabilities;

    public Task<IReadOnlyList<PiModelOption>> GetModelsAsync(CancellationToken cancellationToken)
        => _worker.GetModelsAsync(cancellationToken);

    public Task<PiEngineState> SetModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        => _worker.SetModelAsync(provider, modelId, cancellationToken);

    public Task<IReadOnlyList<string>> GetThinkingLevelsAsync(CancellationToken cancellationToken)
        => _worker.GetThinkingLevelsAsync(cancellationToken);

    public Task<PiEngineState> SetThinkingLevelAsync(string level, CancellationToken cancellationToken)
        => _worker.SetThinkingLevelAsync(level, cancellationToken);
}

/// <summary>
/// Core-bound implementation of <see cref="IEngineSettingsRuntime"/> over
/// the existing validated <see cref="PiWorker"/> operations (issue #23,
/// Core half). The runtime holds no worker: every call resolves the
/// CURRENT worker through the supplied provider and touches only that
/// instance, so selection can never leak across workers. Availability and
/// reasons are computed live at each call, never cached.
///
/// <para><b>Integration point (deliberately not activated here):</b>
/// MainWindow's single construction site</para>
/// <code>
/// private readonly IEngineSettingsRuntime _settingsRuntime = new PendingEngineSettingsRuntime();
/// </code>
/// <para>swaps to</para>
/// <code>
/// private readonly IEngineSettingsRuntime _settingsRuntime =
///     new CoreEngineSettingsRuntime(
///         () => SelectedWorker is { } w ? new PiWorkerEngineSettings(w) : null);
/// </code>
/// <para>together with Muse's native chooser that renders the lists from
/// <see cref="ListModelsAsync"/>/<see cref="ListThinkingLevelsAsync"/>.
/// Activation is kept out of this PR on purpose: <see cref="CanConfigure"/>
/// would flip to true while the current flyout renders a "Choose:" header
/// with no choices — a misleading, nonfunctional picker. The chooser and
/// its activation belong to Muse's GUI slice.</para>
/// </summary>
public sealed class CoreEngineSettingsRuntime : IEngineSettingsRuntime
{
    private readonly Func<IWorkerEngineSettings?> _currentWorker;

    /// <param name="currentWorker">
    /// Resolves the worker to act on at call time (typically the selected
    /// tab). Returning null means "no worker selected".
    /// </param>
    public CoreEngineSettingsRuntime(Func<IWorkerEngineSettings?> currentWorker)
    {
        _currentWorker = currentWorker ?? throw new ArgumentNullException(nameof(currentWorker));
    }

    public bool CanConfigure => DescribeUnavailable() is null;

    public string UnavailableReason => DescribeUnavailable() ?? string.Empty;

    public bool CanConfigureModels => DescribeModelsUnavailable(_currentWorker()) is null;

    public string ModelUnavailableReason => DescribeModelsUnavailable(_currentWorker()) ?? string.Empty;

    public bool CanConfigureThinking => DescribeThinkingUnavailable(_currentWorker()) is null;

    public string ThinkingUnavailableReason => DescribeThinkingUnavailable(_currentWorker()) ?? string.Empty;

    private IWorkerEngineSettings? Current() => _currentWorker();

    /// <summary>Live availability: null when configurable, else the honest reason.</summary>
    private string? DescribeUnavailable()
    {
        IWorkerEngineSettings? worker = _currentWorker();
        if (worker is null)
        {
            return "No worker is selected.";
        }

        if (!worker.IsConnected)
        {
            return "The selected worker is not connected to Pi.";
        }

        PiRpcCapabilities? caps = worker.Capabilities;
        if (caps is null)
        {
            return "The selected worker's capabilities are not probed yet; connect first.";
        }

        if (!caps.AvailableModels && !caps.SetModel
            && !caps.AvailableThinkingLevels && !caps.SetThinkingLevel)
        {
            return "This Pi connection does not report model or thinking selection support.";
        }

        return null;
    }

    /// <summary>
    /// Per-dimension availability (list-AND-set pairing): a chooser must
    /// never offer choices that fail on selection. List-only connections
    /// report an honest unavailable reason instead of selectable options.
    /// Static so the GUI can evaluate a CAPTURED worker (chooser binding)
    /// with the same rules the live properties apply to the current one.
    /// </summary>
    public static string? DescribeModelsUnavailable(IWorkerEngineSettings? worker)
    {
        if (worker is null)
        {
            return "No worker is selected.";
        }

        if (!worker.IsConnected)
        {
            return "The selected worker is not connected to Pi.";
        }

        PiRpcCapabilities? caps = worker.Capabilities;
        if (caps is null)
        {
            return "The selected worker's capabilities are not probed yet; connect first.";
        }

        if (caps.AvailableModels && caps.SetModel)
        {
            return null;
        }

        if (caps.AvailableModels && !caps.SetModel)
        {
            return "This Pi connection can list models but cannot apply a model change.";
        }

        if (!caps.AvailableModels && caps.SetModel)
        {
            return "This Pi connection cannot list models, so there is nothing to choose from.";
        }

        return "This Pi connection does not report model selection support.";
    }

    /// <summary>Thinking half of the list-and-set pairing (see above).</summary>
    public static string? DescribeThinkingUnavailable(IWorkerEngineSettings? worker)
    {
        if (worker is null)
        {
            return "No worker is selected.";
        }

        if (!worker.IsConnected)
        {
            return "The selected worker is not connected to Pi.";
        }

        PiRpcCapabilities? caps = worker.Capabilities;
        if (caps is null)
        {
            return "The selected worker's capabilities are not probed yet; connect first.";
        }

        if (caps.AvailableThinkingLevels && caps.SetThinkingLevel)
        {
            return null;
        }

        if (caps.AvailableThinkingLevels && !caps.SetThinkingLevel)
        {
            return "This Pi connection can list thinking levels but cannot apply a change.";
        }

        if (!caps.AvailableThinkingLevels && caps.SetThinkingLevel)
        {
            return "This Pi connection cannot list thinking levels, so there is nothing to choose from.";
        }

        return "This Pi connection does not report thinking-level selection support.";
    }

    /// <summary>
    /// Resolve once per operation: a selection change mid-call can never
    /// retarget an in-flight operation onto a different worker. A null
    /// provider result is this runtime's own case; every other failure
    /// (not connected, capability missing, validation, confirmation) is
    /// raised by <see cref="PiWorker"/> itself with its typed messages.
    /// </summary>
    private IWorkerEngineSettings RequireWorker(string operation)
        => _currentWorker()
            ?? throw new InvalidOperationException($"Cannot {operation}: no worker is selected.");

    public async Task<IReadOnlyList<EngineModel>> ListModelsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PiModelOption> models =
            await RequireWorker(nameof(ListModelsAsync)).GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return models
            .Select(m => new EngineModel(m.Provider, m.Id, m.Name))
            .ToList();
    }

    public Task SelectModelAsync(string provider, string modelId, CancellationToken cancellationToken)
        => RequireWorker(nameof(SelectModelAsync)).SetModelAsync(provider, modelId, cancellationToken);

    public Task<IReadOnlyList<string>> ListThinkingLevelsAsync(CancellationToken cancellationToken)
        => RequireWorker(nameof(ListThinkingLevelsAsync)).GetThinkingLevelsAsync(cancellationToken);

    public Task SelectThinkingLevelAsync(string level, CancellationToken cancellationToken)
        => RequireWorker(nameof(SelectThinkingLevelAsync)).SetThinkingLevelAsync(level, cancellationToken);
}

/// <summary>
/// Search filter for the model picker: substring match across provider,
/// display name and engine id — the metadata Pi actually reports. Pricing
/// and availability are never invented; an empty query matches everything.
/// </summary>
public static class EngineModelFilter
{
    public static bool Matches(PiModelOption option, string query)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var q = query.Trim();
        return option.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
            || option.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || option.Id.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Chooser binding rule: a model/thinking chooser is bound to the worker
/// whose options it loaded. A choice may apply only while that same worker
/// is still selected; after a tab switch the displayed choice must never
/// land on another worker. The GUI captures the worker id at open, hides
/// open choosers on selection change, and re-verifies this rule at both
/// list-render and selection time.
/// </summary>
public static class EngineChooserPolicy
{
    public static bool MayApplyTo(string capturedWorkerId, string? currentWorkerId)
        => currentWorkerId is not null
            && string.Equals(capturedWorkerId, currentWorkerId, StringComparison.Ordinal);
}
