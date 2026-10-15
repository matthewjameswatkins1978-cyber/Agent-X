using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Live-feed observability bridge: routes typed <see cref="PiEventTap"/>
/// events into the single <see cref="ConversationFeed"/> history with
/// visibility-level filtering, tool correlation, and retained-content access.
///
/// Design rules (see docs/observability.md):
/// - One history only. User and assistant text travel exclusively through the
///   conversation path (deltas, reconcile); the bridge never writes text rows
///   and never duplicates the base tool start/end rows. It adds plain system
///   detail rows carrying duration, exit, previews, and markers — rendered by
///   the existing generic view mapping.
/// - Detailed is the default. Compact suppresses detail rows; Full adds
///   diagnostic rows. The level gates row creation from the change moment;
///   history is never rewritten.
/// - Everything stored here already passed decode-time redaction in the tap.
/// - Correlated poll responses never reach the subscriber; the host passes
///   them to <see cref="RecordResponse(JsonDocument)"/> (the approved
///   boundary). Stats update <see cref="LatestStats"/> for status binding;
///   no rows are fabricated for polls that never happened.
/// - Bounded: rows are small previews and the tap bounds the event store
///   (2000 events / 512KB text, counted eviction). Correlation needs no
///   bridge state: start/end rows carry the observed short call id and
///   retained lookup scans the bounded tap store. Handlers do no I/O and
///   never throw out.
/// </summary>
public sealed class FeedBridge : IDisposable
{
    private readonly ConversationFeed _feed;
    private readonly object _gate = new();
    private PiEventTap? _tap;
    private bool _disposed;

    public FeedBridge(ConversationFeed feed, VisibilityLevel level = VisibilityLevel.Detailed)
    {
        _feed = feed;
        Level = level;
    }

    /// <summary>Visibility filter for subsequently created detail rows. Detailed by default.</summary>
    public VisibilityLevel Level { get; set; }

    private StatsSnapshot? _latestStats;
    private EngineStateObserved? _latestEngine;

    /// <summary>Latest polled session statistics, for status-strip binding. Null until a host poll is recorded.</summary>
    public StatsSnapshot? LatestStats
    {
        get { lock (_gate) { return _latestStats; } }
        private set { lock (_gate) { _latestStats = value; } }
    }

    /// <summary>Latest polled engine state, for status-strip binding. Null until a host poll is recorded.</summary>
    public EngineStateObserved? LatestEngine
    {
        get { lock (_gate) { return _latestEngine; } }
        private set { lock (_gate) { _latestEngine = value; } }
    }

    /// <summary>Attach a tap to a live client. Detach before attaching another.</summary>
    public void Attach(PiRpcClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tap is not null)
            {
                throw new InvalidOperationException("Detach before attaching another client.");
            }

            _tap = new PiEventTap(client);
            _tap.Captured += OnCaptured;
        }
    }

    /// <summary>Attach at the connection layer (same record shape). Used by tests and low-level hosts.</summary>
    public void AttachConnection(PiRpcConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_tap is not null)
            {
                throw new InvalidOperationException("Detach before attaching another client.");
            }

            _tap = new PiEventTap(connection);
            _tap.Captured += OnCaptured;
        }
    }

    /// <summary>Detach then attach in one step: follows client replacements without host rewiring.</summary>
    public void Reattach(PiRpcClient client)
    {
        Detach();
        Attach(client);
    }

    /// <summary>Host poll route: record a correlated response the subscriber never sees.</summary>
    public void RecordResponse(JsonDocument response)
    {
        ArgumentNullException.ThrowIfNull(response);
        PiEventTap? tap;
        lock (_gate)
        {
            tap = _tap;
        }

        tap?.RecordResponse(response);
    }

    /// <summary>
    /// Safely retained tool output for a finished call, or null when unknown
    /// or never retained. The supported route to inspect folded content.
    /// </summary>
    public string? GetRetainedText(string callId)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return null;
        }

        PiEventTap? tap;
        lock (_gate)
        {
            tap = _tap;
        }

        if (tap is null)
        {
            return null;
        }

        string? fallback = null;
        foreach (var e in tap.Events)
        {
            if (e is ToolEnded ended && ended.CallId == callId && ended.RetainedText.Length > 0)
            {
                return ended.RetainedText;
            }

            if (fallback is null && e is ToolProgress progress && progress.CallId == callId && progress.RetainedText.Length > 0)
            {
                fallback = progress.RetainedText;
            }
        }

        return fallback;
    }

    private void OnCaptured(object? sender, AgentXEvent e)
    {
        try
        {
            Route(e);
        }
        catch
        {
            // A bridge fault must never break the tap, the connection, or the feed.
        }
    }

    private void Route(AgentXEvent e)
    {
        switch (e)
        {
            case ToolStarted started:
                if (Level >= VisibilityLevel.Detailed)
                {
                    _feed.AddSystemNotice(DetailRow(started));
                }

                break;
            case ToolProgress progress:
                if (Level >= VisibilityLevel.Full)
                {
                    _feed.AddSystemNotice(ProgressRow(progress));
                }

                break;
            case ToolEnded ended:
                if (Level >= VisibilityLevel.Detailed)
                {
                    _feed.AddSystemNotice(EndRow(ended, CorrelationSuffix(ended.CallId)));
                }

                break;
            case StatsSnapshot stats:
                LatestStats = stats;
                break;
            case EngineStateObserved engine:
                LatestEngine = engine;
                break;
            case TapError tapError when Level >= VisibilityLevel.Full:
                _feed.AddSystemNotice($"Tap skipped a {tapError.Chars}-char {tapError.Kind} record.");
                break;
            case OtherEvent other when Level >= VisibilityLevel.Full && other.EventType == "extension_error":
                // Values are withheld by the tap; the occurrence itself is the signal.
                _feed.AddError("An extension error occurred (details withheld by redaction).");
                break;
            default:
                // Text, turns, runs, usage, prompts, bash chunks/results, and
                // unmodeled shapes: the conversation path owns text and base
                // rows; the bridge claims nothing else. No second history.
                break;
        }
    }

    private static string DetailRow(ToolStarted started)
        => $"[detail {ToolCallAnnounced.ShortId(started.CallId)}] {started.ToolName} args: {started.ArgSummary}";

    private static string ProgressRow(ToolProgress progress)
        => $"[progress {ToolCallAnnounced.ShortId(progress.CallId)}] {progress.ToolName}: {progress.PartialPreview}";

    private static string EndRow(ToolEnded ended, string correlationSuffix)
        => $"[result {ToolCallAnnounced.ShortId(ended.CallId)}] {ended.Render(VisibilityLevel.Detailed) ?? string.Empty}{correlationSuffix}";

    /// <summary>
    /// Honest correlation suffix for an end row, derived from the bounded tap
    /// store at row-creation time (rows are never rewritten later). Claims
    /// "no observed start" ONLY when nothing was evicted — after eviction
    /// the start may simply be gone, so the row stays silent instead of
    /// guessing. Not a second log: a read-only query over tap events.
    /// </summary>
    private string CorrelationSuffix(string callId)
    {
        if (string.IsNullOrEmpty(callId))
        {
            return string.Empty;
        }

        PiEventTap? tap;
        lock (_gate)
        {
            tap = _tap;
        }

        if (tap is null)
        {
            return string.Empty;
        }

        var started = false;
        var ends = 0;
        foreach (var e in tap.Events)
        {
            if (e is ToolStarted s && s.CallId == callId)
            {
                started = true;
            }
            else if (e is ToolEnded other && other.CallId == callId)
            {
                ends++;
            }
        }

        // The current end is already stored (the tap enqueues before
        // broadcasting), so ends >= 1 here; more than one is a repeated end.
        if (!started && tap.DroppedEvents == 0)
        {
            return ends > 1 ? " (no observed start; repeated end)" : " (no observed start)";
        }

        return ends > 1 ? " (repeated end)" : string.Empty;
    }

    public void Detach()
    {
        PiEventTap? tap;
        lock (_gate)
        {
            tap = _tap;
            _tap = null;
        }

        if (tap is not null)
        {
            tap.Captured -= OnCaptured;
            tap.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Detach();
    }
}
