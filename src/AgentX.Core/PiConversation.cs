using System.Text;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>One restored history entry. Text is the full session text; display may truncate.</summary>
public sealed record ConversationMessage(string Role, string Text)
{
    public string DisplayText(int maxChars = 2000)
        => Text.Length <= maxChars
            ? Text
            : Text[..maxChars] + $"… [truncated for display; all {Text.Length:N0} chars preserved in session]";
}

/// <summary>
/// Outcome of one prompt run. Idle (Pi has no automatic work left) is not the
/// same as healthy, and healthy is not the same as externally verified: only
/// a settled, non-aborted run with no observed errors counts as healthy.
/// Whether the task output passes acceptance checks is a separate question
/// answered outside this receipt.
/// </summary>
public sealed record PiRunReceipt(
    bool Settled,
    bool Aborted,
    int ErrorCount,
    int AssistantChars,
    int MessageCount)
{
    public bool RunHealthy => Settled && !Aborted && ErrorCount == 0;

    public string Display => !Settled
        ? "Not settled — Pi may still have automatic work pending."
        : Aborted
            ? "Aborted — task did not complete."
            : ErrorCount > 0
                ? $"Settled with {ErrorCount} problem{(ErrorCount == 1 ? "" : "s")} — review before trusting this answer."
                : "Healthy — settled cleanly with no observed errors. (Output correctness needs separate acceptance checks.)";
}

/// <summary>
/// GUI-facing conversation over a Pi RPC client: loads Pi-native history on
/// connect, streams new prompts, reports compact tool/error activity, and
/// issues a per-run health receipt. No UI references; the host marshals
/// events to its own thread.
/// </summary>
public sealed class PiConversation : IAsyncDisposable
{
    private readonly PiRpcClient _client;
    private readonly List<ConversationMessage> _history = new();
    private readonly Lock _historyGate = new();
    private bool _disposed;

    public PiConversation(PiRpcClient? client = null)
    {
        _client = client ?? new PiRpcClient(PiRpcOptions.Persistent(PiSessionOptions.Default()));
        _client.EventReceived += OnClientEvent;
    }

    /// <summary>Latest compact activity (tool runs, errors, retries). Null when quiet.</summary>
    public event EventHandler<string>? ActivityChanged;

    public bool IsConnected => _client.IsRunning;

    public PiRunReceipt? LastReceipt { get; private set; }

    public IReadOnlyList<ConversationMessage> History
    {
        get
        {
            lock (_historyGate)
            {
                return _history.ToArray();
            }
        }
    }

    public async Task<PiEngineState> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
        return await _client.GetStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _client.GetMessagesAsync(cancellationToken).ConfigureAwait(false);
        var parsed = ParseHistory(response);
        lock (_historyGate)
        {
            _history.Clear();
            _history.AddRange(parsed);
        }
    }

    /// <summary>
    /// Send a prompt and stream assistant text. Returns the streamed text plus
    /// a run-health receipt. Cancellation throws; call RefreshHistoryAsync
    /// afterwards to reconcile partially persisted work.
    /// </summary>
    public async Task<(string Text, PiRunReceipt Receipt)> SendAsync(
        string message,
        IProgress<string>? textProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        var errors = 0;

        void Counter(object? sender, PiRpcEventArgs args) => CountRunError(args, ref errors);
        _client.EventReceived += Counter;
        try
        {
            var (text, aborted) = await _client.PromptAndWaitAsync(message, textProgress, cancellationToken).ConfigureAwait(false);
            var state = await _client.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var receipt = new PiRunReceipt(
                Settled: true,
                Aborted: aborted,
                ErrorCount: errors,
                AssistantChars: text.Length,
                MessageCount: state.MessageCount);
            LastReceipt = receipt;
            await RefreshHistoryAsync(CancellationToken.None).ConfigureAwait(false);
            return (text, receipt);
        }
        finally
        {
            _client.EventReceived -= Counter;
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
        => _client.AbortAsync(cancellationToken);

    private void OnClientEvent(object? sender, PiRpcEventArgs args)
    {
        var activity = DescribeActivity(args.Record);
        if (activity is not null)
        {
            ActivityChanged?.Invoke(this, activity);
        }
    }

    internal static void CountRunError(PiRpcEventArgs args, ref int errors)
    {
        try
        {
            var root = args.Record.RootElement;
            var type = PiRpcFraming.GetEventType(args.Record);
            switch (type)
            {
                case "tool_execution_end":
                    if (root.TryGetProperty("isError", out var isError)
                        && isError.ValueKind == JsonValueKind.True)
                    {
                        errors++;
                    }

                    break;
                case "extension_error":
                    errors++;
                    break;
                case "message_update":
                    if (root.TryGetProperty("assistantMessageEvent", out var nested)
                        && nested.TryGetProperty("type", out var nestedType)
                        && nestedType.GetString() == "error")
                    {
                        errors++;
                    }

                    break;
                case "message_end":
                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("stopReason", out var stop)
                        && stop.GetString() == "error")
                    {
                        errors++;
                    }

                    break;
                case "auto_retry_end":
                    if (root.TryGetProperty("success", out var success)
                        && success.ValueKind == JsonValueKind.False)
                    {
                        errors++;
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            // A malformed event must never break error accounting.
        }
    }

    internal static string? DescribeActivity(JsonDocument record)
    {
        try
        {
            var root = record.RootElement;
            return PiRpcFraming.GetEventType(record) switch
            {
                "agent_start" => "Pi is working…",
                "tool_execution_start" when root.TryGetProperty("toolName", out var name)
                    => $"Running tool: {name.GetString()}…",
                "tool_execution_end" when root.TryGetProperty("toolName", out var endName)
                    => root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True
                        ? $"Tool {endName.GetString()} failed."
                        : $"Tool {endName.GetString()} finished.",
                "auto_retry_start" => "Pi hit a transient error — retrying…",
                "compaction_start" => "Pi is compacting context…",
                "agent_settled" => null, // Receipt line reports this instead.
                _ => null,
            };
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parse a get_messages response into displayable history. Unknown roles
    /// and shapes are tolerated so future Pi versions cannot break restore.
    /// </summary>
    internal static List<ConversationMessage> ParseHistory(JsonDocument response)
    {
        var result = new List<ConversationMessage>();
        if (!response.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var message in messages.EnumerateArray())
        {
            var parsed = ParseMessage(message);
            if (parsed is not null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static ConversationMessage? ParseMessage(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("role", out var roleProp))
        {
            return null;
        }

        var role = roleProp.GetString() ?? "unknown";
        return role switch
        {
            "system" => null, // Prompt boilerplate; not conversation.
            "user" => new ConversationMessage("user", ExtractContentText(message)),
            "assistant" => new ConversationMessage("assistant", ExtractAssistantText(message)),
            "toolResult" => new ConversationMessage("tool", FormatToolResult(message)),
            "bashExecution" => new ConversationMessage("tool", FormatBashExecution(message)),
            "custom" => message.TryGetProperty("display", out var display)
                && display.ValueKind == JsonValueKind.True
                ? new ConversationMessage("system", ExtractContentText(message))
                : null,
            "branchSummary" or "compactionSummary" => new ConversationMessage(
                "system",
                "[context summary] " + (message.TryGetProperty("summary", out var summary) ? summary.GetString() ?? string.Empty : string.Empty)),
            _ => new ConversationMessage(role, ExtractContentText(message)),
        };
    }

    private static string ExtractAssistantText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        var text = new StringBuilder();
        var notes = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object
                || !block.TryGetProperty("type", out var type))
            {
                continue;
            }

            switch (type.GetString())
            {
                case "text":
                    text.Append(block.TryGetProperty("text", out var t) ? t.GetString() : null);
                    break;
                case "toolCall":
                    notes.Add($"[tool call: {(block.TryGetProperty("name", out var n) ? n.GetString() : "?")}]");
                    break;
                case "thinking":
                    break; // Restored on demand from the session, not inlined.
                case "image":
                    notes.Add("[image]");
                    break;
            }
        }

        foreach (var note in notes)
        {
            if (text.Length > 0)
            {
                text.Append('\n');
            }

            text.Append(note);
        }

        return text.ToString();
    }

    private static string ExtractContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blockType = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (blockType == "text" && block.TryGetProperty("text", out var part))
            {
                text.Append(part.GetString());
            }
            else if (blockType == "image")
            {
                text.Append("[image]");
            }
        }

        return text.ToString();
    }

    private static string FormatToolResult(JsonElement message)
    {
        var name = message.TryGetProperty("toolName", out var n) ? n.GetString() ?? "?" : "?";
        var failed = message.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        var body = ExtractContentText(message);
        var header = failed ? $"[tool {name} failed]" : $"[tool result: {name}]";
        return string.IsNullOrEmpty(body) ? header : header + "\n" + body;
    }

    private static string FormatBashExecution(JsonElement message)
    {
        var command = message.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var output = message.TryGetProperty("output", out var o) ? o.GetString() ?? string.Empty : string.Empty;
        return string.IsNullOrEmpty(output) ? $"Ran `{command}`" : $"Ran `{command}`\n{output}";
    }

    public static string FormatHistoryForDisplay(IReadOnlyList<ConversationMessage> history, int perMessageChars = 2000)
    {
        if (history.Count == 0)
        {
            return "No previous conversation in this session.";
        }

        var output = new StringBuilder();
        output.AppendLine($"--- Restored {history.Count} message(s) from this session ---");
        foreach (var entry in history)
        {
            var label = entry.Role switch
            {
                "user" => "You",
                "assistant" => "Pi",
                "tool" => "Tool",
                _ => entry.Role,
            };
            output.AppendLine($"[{label}] {entry.DisplayText(perMessageChars)}");
        }

        return output.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.EventReceived -= OnClientEvent;
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
