using System.Text.Json;

namespace AgentX.Core;

/// <summary>How much of the real work Agent X shows.</summary>
public enum VisibilityLevel
{
    /// <summary>Significant progress and results only.</summary>
    Compact,
    /// <summary>Actual operations displayed live. The default.</summary>
    Detailed,
    /// <summary>Deeper diagnostic evidence and available protocol details.</summary>
    Full,
}

/// <summary>
/// One typed observation decoded from a Pi RPC record.
/// ObservedAtUtc is the client's receipt time: tool events carry no wire
/// timestamps, so operation timing is DurationMs (tool-reported, monotonic)
/// plus this stamp, never a claimed Pi-side clock.
///
/// Every retained string is redacted at decode time (see
/// <see cref="PiRedaction"/>): no event property, subscriber payload, or
/// render level can carry an unredacted secret.
/// </summary>
public abstract record AgentXEvent(DateTime ObservedAtUtc, string EventType)
{
    /// <summary>Display line for the level, or null when hidden at that level.</summary>
    public abstract string? Render(VisibilityLevel level);

    /// <summary>Retained text size on this event, for the tap's content budget.</summary>
    internal virtual int RetainedChars() => 0;

    protected static string Preview(string? text, int max)
    {
        return PreviewForTap(text, max);
    }

    internal static string PreviewForTap(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var flat = text.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    protected static string TruncationMarker(bool wireTruncated, bool tapTruncated)
        => wireTruncated || tapTruncated ? "…[truncated]" : string.Empty;
}

/// <summary>A low-level agent run started.</summary>
public sealed record RunStarted(DateTime ObservedAtUtc) : AgentXEvent(ObservedAtUtc, "agent_start")
{
    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => "Pi is working…",
        _ => "Run started.",
    };
}

/// <summary>Pi will not continue automatically.</summary>
public sealed record RunSettled(DateTime ObservedAtUtc, bool Aborted) : AgentXEvent(ObservedAtUtc, "agent_settled")
{
    public override string? Render(VisibilityLevel level) => Aborted ? "Run aborted." : "Run settled.";
}

/// <summary>One low-level run ended; automatic work may still follow.</summary>
public sealed record AgentRunEnded(DateTime ObservedAtUtc, bool WillRetry, int MessageCount)
    : AgentXEvent(ObservedAtUtc, "agent_end")
{
    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => WillRetry ? "Run ended — retry pending." : "Run ended.",
        _ => $"Run ended (messages: {MessageCount}, willRetry: {WillRetry}).",
    };
}

/// <summary>One assistant turn started.</summary>
public sealed record TurnStarted(DateTime ObservedAtUtc) : AgentXEvent(ObservedAtUtc, "turn_start")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full ? "Turn started." : null;
}

/// <summary>One assistant turn finished with its tool results.</summary>
public sealed record TurnEnded(DateTime ObservedAtUtc, int ToolResultCount)
    : AgentXEvent(ObservedAtUtc, "turn_end")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Turn ended (tool results: {ToolResultCount})."
        : null;
}

/// <summary>Live assistant text delta, redacted and bounded.</summary>
public sealed record TextDelta(DateTime ObservedAtUtc, string Delta, bool TapTruncated)
    : AgentXEvent(ObservedAtUtc, "message_update.text_delta")
{
    internal override int RetainedChars() => Delta.Length;

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => Delta + TruncationMarker(false, TapTruncated),
        _ => Delta + TruncationMarker(false, TapTruncated),
    };
}

/// <summary>A tool call was announced with its id and name.</summary>
public sealed record ToolCallAnnounced(DateTime ObservedAtUtc, string CallId, string ToolName)
    : AgentXEvent(ObservedAtUtc, "message_update.toolcall_start")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Tool call announced: {ToolName} ({ShortId(CallId)})."
        : null;

    internal static string ShortId(string callId) => callId.Length <= 18 ? callId : callId[..18] + "…";
}

/// <summary>Tool execution started with redacted arguments and a one-line summary.</summary>
public sealed record ToolStarted(DateTime ObservedAtUtc, string CallId, string ToolName, string ArgsJson, bool ArgsTruncated, string ArgSummary)
    : AgentXEvent(ObservedAtUtc, "tool_execution_start")
{
    internal override int RetainedChars() => ArgsJson.Length + ArgSummary.Length;

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => $"Run {ToolName}: {ArgSummary}{TruncationMarker(false, ArgsTruncated)}",
        _ => $"Run {ToolName} [{ToolCallAnnounced.ShortId(CallId)}]: {ArgSummary}{TruncationMarker(false, ArgsTruncated)}",
    };
}

/// <summary>
/// A tool reported a partial result mid-execution. The tap keeps the latest
/// bounded snapshot per event (partials may replace or append depending on
/// the tool's contract, which the wire does not state — so the tap never
/// concatenates across updates).
/// </summary>
public sealed record ToolProgress(DateTime ObservedAtUtc, string CallId, string ToolName, string PartialPreview, int PartialChars, bool PartialTruncated)
    : AgentXEvent(ObservedAtUtc, "tool_execution_update")
{
    internal override int RetainedChars() => PartialPreview.Length;

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => $"Partial from {ToolName}: {PartialPreview}{TruncationMarker(false, PartialTruncated)}",
        _ => $"Partial from {ToolName} [{ToolCallAnnounced.ShortId(CallId)}] ({PartialChars} chars): {PartialPreview}{TruncationMarker(false, PartialTruncated)}",
    };
}

/// <summary>
/// Tool execution finished. ExitCode comes from the tool's structured result
/// (exit_code or exitCode). An unknown exit is reported as unknown — never as
/// confirmed success — and is distinct from IsError, which reports whether
/// the tool itself failed. A command can exit nonzero with IsError=false.
/// OutputChars/RetainedBytes describe what the tap actually kept; the wire
/// Truncated flag and the tap's own TapTruncated flag are reported
/// separately so silent evidence loss is impossible.
/// </summary>
public sealed record ToolEnded(
    DateTime ObservedAtUtc, string CallId, string ToolName, bool IsError,
    long? DurationMs, int? ExitCode, bool WireTruncated, bool TapTruncated,
    int OutputChars, int RetainedBytes, string OutputPreview)
    : AgentXEvent(ObservedAtUtc, "tool_execution_end")
{
    internal override int RetainedChars() => OutputPreview.Length;

    public override string? Render(VisibilityLevel level)
    {
        var status = IsError ? "failed"
            : ExitCode is int code && code != 0 ? $"exit {code}"
            : ExitCode is null ? "completed, exit unknown"
            : "ok";
        var timing = DurationMs is long ms ? $" in {ms} ms" : string.Empty;
        var marker = TruncationMarker(WireTruncated, TapTruncated);
        return level switch
        {
            VisibilityLevel.Compact => $"Tool {ToolName}: {status}.{timing}",
            VisibilityLevel.Detailed => $"Tool {ToolName}: {status}.{timing} {OutputPreview}{marker}",
            _ => $"Tool {ToolName} [{ToolCallAnnounced.ShortId(CallId)}]: {status}.{timing} " +
                 $"(retained {OutputChars} chars / {RetainedBytes} bytes, wire-truncated: {WireTruncated}, tap-truncated: {TapTruncated}) " +
                 $"{OutputPreview}{marker}",
        };
    }
}

/// <summary>Completed assistant text with its true length.</summary>
public sealed record AssistantMessage(DateTime ObservedAtUtc, int Chars, string PreviewText)
    : AgentXEvent(ObservedAtUtc, "message_end.assistant")
{
    internal override int RetainedChars() => PreviewText.Length;

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => PreviewText,
        VisibilityLevel.Detailed => PreviewText,
        _ => $"Assistant message ({Chars} chars): {PreviewText}",
    };
}

/// <summary>Provider-reported usage snapshot. Only stored when observed without a nested event.</summary>
public sealed record UsageObserved(DateTime ObservedAtUtc, long Input, long Output, long Total)
    : AgentXEvent(ObservedAtUtc, "message_update.usage")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Usage update (in: {Input}, out: {Output}, total: {Total})."
        : null;
}

/// <summary>Session totals polled via get_session_stats. The only honest token/cost source observed.</summary>
public sealed record StatsSnapshot(
    DateTime ObservedAtUtc, string? SessionId, int ToolCalls, long TotalTokens, double Cost, double ContextPercent)
    : AgentXEvent(ObservedAtUtc, "response.get_session_stats")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Compact
        ? null
        : $"Session: {ToolCalls} tool calls, {TotalTokens} tokens, ${Cost:F4}, context {ContextPercent:F1}%.";
}

/// <summary>A prompt was accepted. Acceptance is not completion.</summary>
public sealed record PromptAccepted(DateTime ObservedAtUtc, string Disposition)
    : AgentXEvent(ObservedAtUtc, "response.prompt")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Prompt accepted ({Disposition})."
        : null;
}

/// <summary>One streaming chunk of a direct RPC bash command: bounded head plus true size.</summary>
public sealed record BashChunk(DateTime ObservedAtUtc, string CommandId, string HeadPreview, int DeltaChars)
    : AgentXEvent(ObservedAtUtc, "bash_execution_update")
{
    internal override int RetainedChars() => HeadPreview.Length;

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => HeadPreview,
        _ => $"Bash chunk ({DeltaChars} chars): {HeadPreview}",
    };
}

/// <summary>
/// A direct RPC bash command finished. Large outputs truncate in the
/// response; FullOutputPath points at the spilled bytes when present.
/// </summary>
public sealed record BashResult(
    DateTime ObservedAtUtc, int? ExitCode, bool Truncated, string? FullOutputPath, string OutputPreview)
    : AgentXEvent(ObservedAtUtc, "response.bash")
{
    internal override int RetainedChars() => OutputPreview.Length + (FullOutputPath?.Length ?? 0);

    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => $"Bash done (exit: {(ExitCode?.ToString() ?? "unknown")}). {OutputPreview}{TruncationMarker(Truncated, false)}",
        _ => $"Bash done (exit: {(ExitCode?.ToString() ?? "unknown")}, truncated: {Truncated}, " +
             $"spill: {FullOutputPath ?? "none"}). {OutputPreview}{TruncationMarker(Truncated, false)}",
    };
}

/// <summary>
/// Anything the tap does not model yet. Fail-closed: only metadata is kept
/// (event type, top-level field names, total size) — never raw values — so
/// an unknown shape cannot smuggle a secret into the store, a subscriber, or
/// Full trace. Covers queue, retry, compaction, session-info, entry,
/// extension, and future event shapes.
/// </summary>
public sealed record OtherEvent(DateTime ObservedAtUtc, string EventType, string FieldNames, int TotalChars)
    : AgentXEvent(ObservedAtUtc, EventType)
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"{EventType} [details withheld: {TotalChars} chars, fields: {FieldNames}]."
        : null;
}

/// <summary>
/// A record the tap could not parse. Fail-closed: only the failure kind and
/// size are kept — never the raw fragment, which may itself be sensitive.
/// The connection is never broken by a bad record.
/// </summary>
public sealed record TapError(DateTime ObservedAtUtc, string Kind, int Chars)
    : AgentXEvent(ObservedAtUtc, "tap_error")
{
    public override string? Render(VisibilityLevel level) => $"Tap skipped a {Chars}-char {Kind} record.";
}

/// <summary>Engine state polled via get_state, reusing the established typed model. Captured only via RecordResponse.</summary>
public sealed record EngineStateObserved(DateTime ObservedAtUtc, PiEngineState State)
    : AgentXEvent(ObservedAtUtc, "response.get_state")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Engine: {State.ModelId ?? "unknown model"} (streaming: {State.IsStreaming}, messages: {State.MessageCount})."
        : null;
}
