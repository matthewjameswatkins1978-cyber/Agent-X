using System.Text;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Typed capture over Pi RPC session events. Subscribes to a client's raw
/// event stream, decodes each record synchronously (all strings are resolved
/// before the underlying document is released), redacts before storing or
/// broadcasting, and raises one <see cref="AgentXEvent"/> per record.
///
/// Capture contract (honest boundary): the tap sees STREAMED session events
/// automatically. Correlated poll responses (get_session_stats, direct bash,
/// prompt acceptance, get_state) bypass the subscriber inside
/// PiRpcConnection and are captured ONLY when the host passes them to
/// <see cref="RecordResponse(JsonDocument)"/>. The tap never claims
/// pollables it was not handed.
///
/// Never throws out of the tap: malformed records become
/// <see cref="TapError"/> and subscriber faults are isolated per handler.
/// Retention is bounded two ways: at most <see cref="MaxStoredEvents"/>
/// events and at most <see cref="MaxRetainedTextChars"/> of retained text;
/// overflow evicts oldest-first and is counted, never lost silently.
/// </summary>
public sealed class PiEventTap : IDisposable
{
    private const int MaxStoredEvents = 2000;
    private const int MaxRetainedTextChars = 512 * 1024;
    private const int MaxArgsChars = 4096;
    private const int MaxOutputChars = 16 * 1024;
    private const int MaxOutputPreview = 2000;
    private const int MaxDeltaChars = 4096;
    private const int MaxPartialChars = 2048;
    private const int MaxPartialPreview = 1000;
    private const int MaxBashHead = 1000;
    private const int MaxMessagePreview = 400;

    private readonly PiRpcClient? _client;
    private readonly PiRpcConnection? _connection;
    private readonly Queue<AgentXEvent> _events = new();
    private readonly object _gate = new();
    private long _droppedEvents;
    private long _retainedTextChars;
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

    public long RetainedTextChars
    {
        get
        {
            lock (_gate)
            {
                return _retainedTextChars;
            }
        }
    }

    public static AgentXEvent Parse(string rawLine, DateTime observedAtUtc)
    {
        JsonDocument record;
        try
        {
            record = JsonDocument.Parse(rawLine);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return new TapError(observedAtUtc, "unparseable", rawLine.Length);
        }

        using (record)
        {
            return DecodeDocument(record, observedAtUtc);
        }
    }

    /// <summary>
    /// Explicit pollable route: the host hands a correlated command response
    /// to the tap (the subscriber never sees these). Decodes StatsSnapshot,
    /// BashResult, PromptAccepted, and engine state through the same
    /// redacting path as streamed events.
    /// </summary>
    public void RecordResponse(JsonDocument response)
    {
        AgentXEvent decoded;
        try
        {
            decoded = DecodeDocument(response, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or JsonException)
        {
            decoded = new TapError(DateTime.UtcNow, "disposed-response", 0);
        }

        Publish(decoded);
    }

    internal static AgentXEvent DecodeDocument(JsonDocument document, DateTime observedAtUtc)
    {
        try
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new TapError(observedAtUtc, "non-object-root", 0);
            }

            return Decode(root, document, observedAtUtc);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return new TapError(observedAtUtc, "unexpected-shape", 0);
        }
    }

    internal static AgentXEvent Decode(JsonElement root, JsonDocument document, DateTime observedAtUtc)
    {
        var type = Str(root, "type") ?? string.Empty;
        return type switch
        {
            "agent_start" => new RunStarted(observedAtUtc),
            "agent_settled" => new RunSettled(observedAtUtc, Bool(root, "aborted")),
            "agent_end" => new AgentRunEnded(observedAtUtc, Bool(root, "willRetry"), MsgCount(root)),
            "turn_start" => new TurnStarted(observedAtUtc),
            "turn_end" => new TurnEnded(observedAtUtc, ToolResults(root)),
            "message_update" => DecodeUpdate(root, observedAtUtc),
            "message_end" => DecodeMessageEnd(root, observedAtUtc),
            "tool_execution_start" => DecodeToolStart(root, observedAtUtc),
            "tool_execution_update" => DecodeToolProgress(root, observedAtUtc),
            "tool_execution_end" => DecodeToolEnd(root, observedAtUtc),
            "bash_execution_update" => new BashChunk(
                observedAtUtc, PiRedaction.Id(Str(root, "id")),
                Head(Str(root, "delta"), MaxBashHead), Str(root, "delta")?.Length ?? 0),
            "response" => DecodeResponse(root, document, observedAtUtc),
            _ => DescribeUnknown(root, type.Length > 0 ? type : "unknown", observedAtUtc),
        };
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
            if (nestedType is "text_delta")
            {
                var delta = PiRedaction.Redact(Str(nested, "delta"));
                var cut = delta.Length > MaxDeltaChars;
                return new TextDelta(observedAtUtc, cut ? delta[..MaxDeltaChars] : delta, cut);
            }

            if (nestedType is "toolcall_start")
            {
                return new ToolCallAnnounced(
                    observedAtUtc, PiRedaction.Id(Str(nested, "id")), PiRedaction.Name(Str(nested, "toolName")));
            }

            // Thinking blocks arrive empty (encrypted signature only); text/tool blocks end
            // with authoritative content already covered by message_end.
            return DescribeUnknown(nested, "message_update." + PiRedaction.TypeToken(nestedType), observedAtUtc);
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return new UsageObserved(observedAtUtc, Num(usage, "input"), Num(usage, "output"), Num(usage, "totalTokens"));
        }

        // A nested toolcall_delta carries serialized argument bytes but no new
        // identity beyond the announced call; ToolStarted already reports args.
        return DescribeUnknown(root, "message_update", observedAtUtc);
    }

    private static AgentXEvent DecodeMessageEnd(JsonElement root, DateTime observedAtUtc)
    {
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && Str(message, "role") == "assistant")
        {
            // Retention window: stream blocks, keep the preview head, count the
            // true total. Peak transient is the largest single block, never the
            // whole message; retention never exceeds the preview cap.
            var (text, total, _) = CollectText(message, MaxMessagePreview);
            var preview = PiRedaction.Redact(text);
            return new AssistantMessage(observedAtUtc, total, preview);
        }

        return DescribeUnknown(root, "message_end", observedAtUtc);
    }

    private static AgentXEvent DecodeToolStart(JsonElement root, DateTime observedAtUtc)
    {
        var raw = RawOf(root, "args");
        var redacted = PiRedaction.Redact(raw);
        var cut = redacted.Length > MaxArgsChars;
        var kept = cut ? redacted[..MaxArgsChars] : redacted;
        return new ToolStarted(
            observedAtUtc, PiRedaction.Id(Str(root, "toolCallId")), PiRedaction.Name(Str(root, "toolName")),
            kept, cut, PiRedaction.Redact(ArgSummary(root)));
    }

    private static AgentXEvent DecodeToolProgress(JsonElement root, DateTime observedAtUtc)
    {
        // Latest snapshot wins: the wire does not say whether a partial
        // replaces or extends the previous one, so the tap never concatenates.
        var (text, total, capped) = CollectText(root, MaxPartialChars, "partialResult");
        var redacted = PiRedaction.Redact(text);
        var preview = Slice(redacted, MaxPartialPreview);
        return new ToolProgress(
            observedAtUtc, PiRedaction.Id(Str(root, "toolCallId")), PiRedaction.Name(Str(root, "toolName")),
            preview, total, capped, redacted);
    }

    private static AgentXEvent DecodeToolEnd(JsonElement root, DateTime observedAtUtc)
    {
        var callId = PiRedaction.Id(Str(root, "toolCallId"));
        var name = PiRedaction.Name(Str(root, "toolName"));
        var isError = Bool(root, "isError");
        long? duration = NumOpt(root, "durationMs");
        string text = string.Empty;
        int? exit = null;
        var wireTruncated = false;
        var collectCapped = false;
        var outputTotal = 0;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            var collected = CollectText(result, MaxOutputChars);
            text = collected.Kept;
            collectCapped = collected.Capped;
            outputTotal = collected.Total;
            if (result.TryGetProperty("structuredContent", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                // Key spellings differ between the tool result contract (exit_code)
                // and the direct RPC bash contract (exitCode); accept both.
                exit = NumOpt(structured, "exit_code") is long a ? (int)Math.Clamp(a, int.MinValue, int.MaxValue)
                    : NumOpt(structured, "exitCode") is long b ? (int)Math.Clamp(b, int.MinValue, int.MaxValue)
                    : null;
                wireTruncated = Bool(structured, "truncated");
            }
        }

        var redacted = PiRedaction.Redact(text);
        var tapCut = collectCapped || redacted.Length > MaxOutputChars;
        var kept = tapCut && redacted.Length > MaxOutputChars ? redacted[..MaxOutputChars] : redacted;
        var preview = Slice(kept, MaxOutputPreview);
        return new ToolEnded(observedAtUtc, callId, name, isError, duration, exit, wireTruncated, tapCut,
            outputTotal, Encoding.UTF8.GetByteCount(kept), preview, kept);
    }

    private static AgentXEvent DecodeResponse(JsonElement root, JsonDocument document, DateTime observedAtUtc)
    {
        var command = Str(root, "command") ?? string.Empty;
        if (command == "get_state")
        {
            // Engine state reuses the established typed model on the live document,
            // with every nested string redacted before the event is constructed.
            return new EngineStateObserved(observedAtUtc, RedactState(PiEngineState.FromResponse(document)));
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return DescribeUnknown(root, "response." + PiRedaction.TypeToken(command), observedAtUtc);
        }

        return command switch
        {
            "get_session_stats" => new StatsSnapshot(observedAtUtc, PiRedaction.Id(Str(data, "sessionId")),
                ToolCalls(data), Tokens(data), Cost(data), ContextPercent(data)),
            "prompt" => new PromptAccepted(observedAtUtc, PiRedaction.Disposition(Str(data, "disposition"))),
            "bash" => DecodeBash(data, observedAtUtc),
            _ => DescribeUnknown(root, "response." + PiRedaction.TypeToken(command), observedAtUtc),
        };
    }

    /// <summary>
    /// Fail-closed description of an unmodeled shape: field names and total
    /// size only. Raw values are never retained, so an unknown payload cannot
    /// smuggle a secret into the store, a subscriber, or Full trace.
    /// </summary>
    internal static OtherEvent DescribeUnknown(JsonElement element, string type, DateTime observedAtUtc)
    {
        var names = new List<string>();
        var total = element.GetRawText().Length;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Count < 24)
                {
                    names.Add(PiRedaction.FieldName(property.Name));
                }
            }
        }

        return new OtherEvent(observedAtUtc, PiRedaction.TypeToken(type), string.Join(",", names), total);
    }

    private static PiEngineState RedactState(PiEngineState state) => state with
    {
        ModelId = NullIfEmpty(PiRedaction.Model(state.ModelId)),
        ModelName = NullIfEmpty(PiRedaction.Text(state.ModelName)),
        Provider = NullIfEmpty(PiRedaction.Text(state.Provider)),
        SessionId = NullIfEmpty(PiRedaction.Id(state.SessionId)),
    };

    private static string? NullIfEmpty(string gated) => gated.Length == 0 ? null : gated;

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

    private static string Head(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = PiRedaction.Redact(text);
        return redacted.Length <= max ? redacted : redacted[..max];
    }

    /// <summary>Cut an already-redacted string without adding markers. Marking belongs to renders.</summary>
    private static string Slice(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Bounded head of a possibly large output with its true length: slice first, then redact the retained window.</summary>
    private static (string Head, int Total) OutputHead(string? output, int max)
    {
        if (string.IsNullOrEmpty(output))
        {
            return (string.Empty, 0);
        }

        var total = output.Length;
        var head = total <= max ? output : output[..max];
        return (PiRedaction.Redact(head), total);
    }

    private static AgentXEvent DecodeBash(JsonElement data, DateTime observedAtUtc)
    {
        var (head, total) = OutputHead(Str(data, "output"), MaxOutputPreview);
        return new BashResult(observedAtUtc,
            NumOpt(data, "exitCode") is long e ? (int)e : null,
            Bool(data, "truncated"), PiRedaction.Path(Str(data, "fullOutputPath")),
            head, total);
    }

    private static (string Kept, int Total, bool Capped) CollectText(JsonElement container, int cap, string field = "content")
    {
        if (!container.TryGetProperty(field, out var content))
        {
            return (string.Empty, 0, false);
        }

        // content: [{type:text,text:...}]. partialResult wraps the same array
        // in an object ({content:[...], details:{...}}); details stays
        // unmodeled and withheld.
        if (content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("content", out var inner)
            && inner.ValueKind == JsonValueKind.Array)
        {
            content = inner;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, 0, false);
        }

        // Count the true total while storing only up to cap: O(n) time,
        // O(cap) memory. Callers report both so truncation is explicit.
        var parts = new List<string>();
        var total = 0;
        var kept = 0;
        var capped = false;
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
                total += s.Length;
                var room = cap - kept;
                if (room <= 0)
                {
                    capped = true;
                    continue;
                }

                parts.Add(s.Length <= room ? s : s[..room]);
                if (s.Length > room)
                {
                    capped = true;
                }

                kept += Math.Min(s.Length, room);
            }
        }

        return (string.Concat(parts), total, capped);
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

    private void OnEvent(object? sender, PiRpcEventArgs args)
    {
        AgentXEvent decoded;
        try
        {
            decoded = DecodeDocument(args.Record, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or JsonException)
        {
            decoded = new TapError(DateTime.UtcNow, "disposed-record", 0);
        }

        Publish(decoded);
    }

    private void Publish(AgentXEvent decoded)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Bounded two ways: event count and retained text. Evict oldest
            // first (O(1) dequeue); every eviction is counted.
            _events.Enqueue(decoded);
            _retainedTextChars += decoded.RetainedChars();
            while ((_events.Count > MaxStoredEvents || _retainedTextChars > MaxRetainedTextChars) && _events.Count > 0)
            {
                _retainedTextChars -= _events.Dequeue().RetainedChars();
                _droppedEvents++;
            }

            if (_retainedTextChars < 0)
            {
                _retainedTextChars = 0;
            }
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
