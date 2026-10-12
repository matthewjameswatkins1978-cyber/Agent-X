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
/// </summary>
public abstract record AgentXEvent(DateTime ObservedAtUtc, string EventType)
{
    /// <summary>Display line for the level, or null when hidden at that level.</summary>
    public abstract string? Render(VisibilityLevel level);

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

/// <summary>Live assistant text delta. Usage on deltas is provider-reported; this provider streams zeros.</summary>
public sealed record TextDelta(DateTime ObservedAtUtc, string Delta) : AgentXEvent(ObservedAtUtc, "message_update.text_delta")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Compact ? null : Delta;
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

/// <summary>Tool execution started with verbatim arguments.</summary>
public sealed record ToolStarted(DateTime ObservedAtUtc, string CallId, string ToolName, string ArgsJson, string ArgSummary)
    : AgentXEvent(ObservedAtUtc, "tool_execution_start")
{
    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => $"Run {ToolName}: {ArgSummary}",
        _ => $"Run {ToolName} [{ToolCallAnnounced.ShortId(CallId)}]: {ArgSummary}",
    };
}

/// <summary>A tool reported a partial result mid-execution.</summary>
public sealed record ToolProgress(DateTime ObservedAtUtc, string CallId, string ToolName, int PartialBytes)
    : AgentXEvent(ObservedAtUtc, "tool_execution_update")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Partial result from {ToolName} ({PartialBytes} bytes)."
        : null;
}

/// <summary>
/// Tool execution finished. ExitCode comes from the tool's structured result
/// (exit_code or exitCode); it is NOT the same as IsError, which reports
/// whether the tool itself failed. A command can exit nonzero with
/// IsError=false — the model may even wrap failures so the tool succeeds.
/// </summary>
public sealed record ToolEnded(
    DateTime ObservedAtUtc, string CallId, string ToolName, bool IsError,
    long? DurationMs, int? ExitCode, bool Truncated, int OutputBytes, string OutputPreview)
    : AgentXEvent(ObservedAtUtc, "tool_execution_end")
{
    public override string? Render(VisibilityLevel level)
    {
        var status = IsError ? "failed"
            : ExitCode is int code && code != 0 ? $"exit {code}"
            : "ok";
        var timing = DurationMs is long ms ? $" in {ms} ms" : string.Empty;
        return level switch
        {
            VisibilityLevel.Compact => $"Tool {ToolName}: {status}.{timing}",
            VisibilityLevel.Detailed => $"Tool {ToolName}: {status}.{timing} {OutputPreview}",
            _ => $"Tool {ToolName} [{ToolCallAnnounced.ShortId(CallId)}]: {status}.{timing} " +
                 $"(bytes: {OutputBytes}, truncated: {Truncated}, exit: {(ExitCode?.ToString() ?? "n/a")}) {OutputPreview}",
        };
    }
}

/// <summary>Completed assistant text with its true length.</summary>
public sealed record AssistantMessage(DateTime ObservedAtUtc, int Chars, string PreviewText)
    : AgentXEvent(ObservedAtUtc, "message_end.assistant")
{
    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => PreviewText,
        VisibilityLevel.Detailed => PreviewText,
        _ => $"Assistant message ({Chars} chars): {PreviewText}",
    };
}

/// <summary>Provider-reported usage snapshot. Zero totals are recorded, not hidden: this provider streams zeros.</summary>
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

/// <summary>One streaming chunk of a direct RPC bash command.</summary>
public sealed record BashChunk(DateTime ObservedAtUtc, string CommandId, int DeltaChars)
    : AgentXEvent(ObservedAtUtc, "bash_execution_update")
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full
        ? $"Bash chunk ({DeltaChars} chars)."
        : null;
}

/// <summary>
/// A direct RPC bash command finished. Large outputs truncate in the
/// response; FullOutputPath points at the spilled bytes when present.
/// </summary>
public sealed record BashResult(
    DateTime ObservedAtUtc, int? ExitCode, bool Truncated, string? FullOutputPath, string OutputPreview)
    : AgentXEvent(ObservedAtUtc, "response.bash")
{
    public override string? Render(VisibilityLevel level) => level switch
    {
        VisibilityLevel.Compact => null,
        VisibilityLevel.Detailed => $"Bash done (exit: {(ExitCode?.ToString() ?? "n/a")}). {OutputPreview}",
        _ => $"Bash done (exit: {(ExitCode?.ToString() ?? "n/a")}, truncated: {Truncated}, " +
             $"spill: {FullOutputPath ?? "none"}). {OutputPreview}",
    };
}

/// <summary>
/// Anything the tap does not model yet, kept verbatim (capped) so Full trace
/// never silently drops protocol evidence. Covers queue, retry, compaction,
/// session-info, entry, extension, and future event shapes.
/// </summary>
public sealed record OtherEvent(DateTime ObservedAtUtc, string EventType, string RawPreview)
    : AgentXEvent(ObservedAtUtc, EventType)
{
    public override string? Render(VisibilityLevel level) => level == VisibilityLevel.Full ? $"{EventType}: {RawPreview}" : null;
}

/// <summary>A record the tap could not parse. The connection is never broken by a bad record.</summary>
public sealed record TapError(DateTime ObservedAtUtc, string Detail) : AgentXEvent(ObservedAtUtc, "tap_error")
{
    public override string? Render(VisibilityLevel level) => $"Tap could not parse a record ({Detail}).";
}
