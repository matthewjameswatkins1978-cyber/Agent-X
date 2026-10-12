using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Typed capture over Pi RPC session events. Subscribes to a client's raw
/// event stream, decodes each record synchronously (all strings are resolved
/// before the underlying document is released), and raises one
/// <see cref="AgentXEvent"/> per record. Never throws out of the tap:
/// malformed records become <see cref="TapError"/> and subscriber faults are
/// isolated per handler. The store is bounded; overflow is counted, not lost
/// silently.
/// </summary>
public sealed class PiEventTap : IDisposable
{
    private const int MaxStoredEvents = 5000;
    private const int MaxCollectedChars = 4000;
    private const int PreviewChars = 300;

    private readonly PiRpcClient? _client;
    private readonly PiRpcConnection? _connection;
    private readonly List<AgentXEvent> _events = new();
    private readonly object _gate = new();
    private long _droppedEvents;
    private bool _disposed;

    public PiEventTap(PiRpcClient client)
    {
        _client = client;
        _client.EventReceived += OnEvent;
    }

    /// <summary>Attach at the connection layer (same record shape). Used by tests and low-level hosts.</summary>
    public PiEventTap(PiRpcConnection connection)
    {
        _connection = connection;
        _connection.EventReceived += OnEvent;
    }

    public event EventHandler<AgentXEvent>? Captured;

    public IReadOnlyList<AgentXEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public long DroppedEvents
    {
        get
        {
            lock (_gate)
            {
                return _droppedEvents;
            }
        }
    }

    public static AgentXEvent Parse(string rawLine, DateTime observedAtUtc)
    {
        try
        {
            using var record = JsonDocument.Parse(rawLine);
            return Decode(record.RootElement, rawLine, observedAtUtc);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new TapError(observedAtUtc, AgentXEvent.PreviewForTap(rawLine));
        }
    }

    internal static AgentXEvent Decode(JsonElement root, string rawLine, DateTime observedAtUtc)
    {
        var type = Str(root, "type") ?? string.Empty;
        try
        {
            return type switch
            {
                "agent_start" => new RunStarted(observedAtUtc),
                "agent_settled" => new RunSettled(observedAtUtc, Bool(root, "aborted")),
                "agent_end" => new AgentRunEnded(observedAtUtc, Bool(root, "willRetry"), MsgCount(root)),
                "turn_start" => new TurnStarted(observedAtUtc),
                "turn_end" => new TurnEnded(observedAtUtc, ToolResults(root)),
                "message_update" => DecodeUpdate(root, observedAtUtc),
                "message_end" => DecodeMessageEnd(root, observedAtUtc, rawLine),
                "tool_execution_start" => new ToolStarted(
                    observedAtUtc, Str(root, "toolCallId") ?? string.Empty, Str(root, "toolName") ?? string.Empty,
                    RawOf(root, "args"), ArgSummary(root)),
                "tool_execution_update" => new ToolProgress(
                    observedAtUtc, Str(root, "toolCallId") ?? string.Empty, Str(root, "toolName") ?? string.Empty,
                    RawOf(root, "partialResult").Length),
                "tool_execution_end" => DecodeToolEnd(root, observedAtUtc),
                "bash_execution_update" => new BashChunk(
                    observedAtUtc, Str(root, "id") ?? string.Empty, Str(root, "delta")?.Length ?? 0),
                "response" => DecodeResponse(root, observedAtUtc, rawLine),
                _ => new OtherEvent(observedAtUtc, type.Length > 0 ? type : "unknown", Capped(rawLine)),
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return new TapError(observedAtUtc, Capped(rawLine));
        }
    }

    private static AgentXEvent DecodeUpdate(JsonElement root, DateTime observedAtUtc)
    {
        // Usage rides on every delta but stays zero on this provider; it is
        // documented in docs/observability.md, not stored per delta. A
        // UsageObserved event fires only for usage without a nested event.
        if (root.TryGetProperty("assistantMessageEvent", out var nested)
            && nested.ValueKind == JsonValueKind.Object
            && Str(nested, "type") is string nestedType)
        {
            return nestedType switch
            {
                "text_delta" => new TextDelta(observedAtUtc, Str(nested, "delta") ?? string.Empty),
                "toolcall_start" => new ToolCallAnnounced(
                    observedAtUtc, Str(nested, "id") ?? string.Empty, Str(nested, "toolName") ?? string.Empty),
                // Thinking blocks arrive empty (encrypted signature only); text/tool blocks end
                // with authoritative content already covered by message_end. Drop the rest.
                _ => new OtherEvent(observedAtUtc, "message_update." + nestedType, Capped(RawOf(nested))),
            };
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return new UsageObserved(observedAtUtc, Num(usage, "input"), Num(usage, "output"), Num(usage, "totalTokens"));
        }

        // A nested toolcall_delta carries serialized argument bytes but no new
        // identity beyond the announced call; ToolStarted already reports args.
        return new OtherEvent(observedAtUtc, "message_update", Capped(RawOf(root)));
    }

    private static AgentXEvent DecodeMessageEnd(JsonElement root, DateTime observedAtUtc, string rawLine)
    {
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && Str(message, "role") == "assistant")
        {
            var text = CollectText(message);
            return new AssistantMessage(observedAtUtc, text.Length, AgentXEvent.PreviewForTap(text, PreviewChars));
        }

        return new OtherEvent(observedAtUtc, "message_end", Capped(rawLine));
    }

    private static AgentXEvent DecodeToolEnd(JsonElement root, DateTime observedAtUtc)
    {
        var callId = Str(root, "toolCallId") ?? string.Empty;
        var name = Str(root, "toolName") ?? string.Empty;
        var isError = Bool(root, "isError");
        long? duration = NumOpt(root, "durationMs");
        string text = string.Empty;
        int? exit = null;
        var truncated = false;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            text = CollectText(result);
            if (result.TryGetProperty("structuredContent", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                // Key spellings differ between the tool result contract (exit_code)
                // and the direct RPC bash contract (exitCode); accept both.
                exit = NumOpt(structured, "exit_code") is long a ? (int)Math.Clamp(a, int.MinValue, int.MaxValue)
                    : NumOpt(structured, "exitCode") is long b ? (int)Math.Clamp(b, int.MinValue, int.MaxValue)
                    : null;
                truncated = Bool(structured, "truncated");
            }
        }

        return new ToolEnded(observedAtUtc, callId, name, isError, duration, exit, truncated,
            text.Length, AgentXEvent.PreviewForTap(text, PreviewChars));
    }

    private static AgentXEvent DecodeResponse(JsonElement root, DateTime observedAtUtc, string rawLine)
    {
        var command = Str(root, "command") ?? string.Empty;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return new OtherEvent(observedAtUtc, "response." + command, Capped(rawLine));
        }

        return command switch
        {
            "get_session_stats" => new StatsSnapshot(observedAtUtc, Str(data, "sessionId"),
                ToolCalls(data), Tokens(data), Cost(data), ContextPercent(data)),
            "prompt" => new PromptAccepted(observedAtUtc, Str(data, "disposition") ?? string.Empty),
            "bash" => new BashResult(observedAtUtc,
                NumOpt(data, "exitCode") is long e ? (int)e : null,
                Bool(data, "truncated"), Str(data, "fullOutputPath"),
                AgentXEvent.PreviewForTap(Str(data, "output") ?? string.Empty, PreviewChars)),
            _ => new OtherEvent(observedAtUtc, "response." + command, Capped(rawLine)),
        };
    }

    /// <summary>One-line argument summary. Known scalar fields first; otherwise a field count. Never invents values.</summary>
    internal static string ArgSummary(JsonElement root)
    {
        if (!root.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object)
        {
            return "no args";
        }

        if (args.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
        {
            return "command: " + AgentXEvent.PreviewForTap(command.GetString() ?? string.Empty, 120);
        }

        if (args.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String)
        {
            return "path: " + (path.GetString() ?? string.Empty);
        }

        var count = 0;
        foreach (var _ in args.EnumerateObject())
        {
            count++;
        }

        return count == 0 ? "no args" : $"{count} args";
    }

    private static string CollectText(JsonElement container)
    {
        // content: [{type:text,text:...}] on messages and tool results.
        if (!container.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var total = 0;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Str(block, "type") == "text" && block.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                var s = text.GetString() ?? string.Empty;
                var room = MaxCollectedChars - total;
                if (room <= 0)
                {
                    break;
                }

                parts.Add(s.Length <= room ? s : s[..room]);
                total += Math.Min(s.Length, room);
            }
        }

        return string.Concat(parts);
    }

    private static int MsgCount(JsonElement root)
        => root.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;

    private static int ToolResults(JsonElement root)
        => root.TryGetProperty("toolResults", out var t) && t.ValueKind == JsonValueKind.Array ? t.GetArrayLength() : 0;

    private static int ToolCalls(JsonElement data)
    {
        if (data.TryGetProperty("toolCalls", out var tc)
            && tc.ValueKind == JsonValueKind.Number
            && tc.TryGetInt32(out var v))
        {
            return v;
        }

        return 0;
    }

    private static long Tokens(JsonElement data)
    {
        if (data.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            return Num(tokens, "total");
        }

        return 0;
    }

    private static double Cost(JsonElement data)
        => data.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : 0;

    private static double ContextPercent(JsonElement data)
    {
        if (data.TryGetProperty("contextUsage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("percent", out var percent) && percent.ValueKind == JsonValueKind.Number)
        {
            return percent.GetDouble();
        }

        return 0;
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static long Num(JsonElement element, string name)
        => NumOpt(element, name) ?? 0;

    private static long? NumOpt(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;

    private static string RawOf(JsonElement element, string name)
        => element.TryGetProperty(name, out var p) ? p.GetRawText() : string.Empty;

    private static string RawOf(JsonElement element) => element.GetRawText();

    private static string Capped(string raw, int max = PreviewChars)
        => raw.Length <= max ? raw : raw[..max] + "…";

    private void OnEvent(object? sender, PiRpcEventArgs args)
    {
        AgentXEvent decoded;
        try
        {
            decoded = Decode(args.Record.RootElement, args.RawLine, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or JsonException)
        {
            decoded = new TapError(DateTime.UtcNow, Capped(args.RawLine));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_events.Count >= MaxStoredEvents)
            {
                _events.RemoveAt(0);
                _droppedEvents++;
            }

            _events.Add(decoded);
        }

        foreach (var handler in Captured?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<AgentXEvent>)handler)(this, decoded);
            }
            catch
            {
                // One bad subscriber must not break the tap or the connection.
            }
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

        if (_client is not null)
        {
            _client.EventReceived -= OnEvent;
        }

        if (_connection is not null)
        {
            _connection.EventReceived -= OnEvent;
        }
    }
}
