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
/// Structured tool lifecycle for the conversation feed. <see cref="Path"/>
/// is only set for edit/write calls with an explicit path argument (a read
/// path is not a change); for bash/powershell <see cref="Summary"/> holds
/// the shell command. Never fabricated: unknown shapes yield nulls.
/// </summary>
public sealed record PiToolActivity(
    string ToolName,
    string? Summary,
    string? Path,
    bool IsError,
    bool IsStart)
{
    public bool IsMutating =>
        ToolName.Equals("edit", StringComparison.OrdinalIgnoreCase)
        || ToolName.Equals("write", StringComparison.OrdinalIgnoreCase);
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
    private readonly Func<PiRpcClient> _clientFactory;
    private PiRpcClient _client;
    private readonly List<ConversationMessage> _history = new();
    private readonly Lock _historyGate = new();
    private readonly Lock _clientGate = new();
    private bool _disposed;
    private int _reconnectCount;
    private FeedBridge? _bridge;

    [Obsolete("Pass a client factory (Func<PiRpcClient>) so ReconnectAsync can replace the process. Single-client reconnect is unsupported.")]
    public PiConversation(PiRpcClient? client = null)
        : this(RequireFactory(client))
    {
    }

    private static Func<PiRpcClient> RequireFactory(PiRpcClient? client)
        => client is null
            ? () => new PiRpcClient(PiRpcOptions.Persistent(PiSessionOptions.Default()))
            : throw new ArgumentException(
                "A fixed client cannot be replaced on reconnect; pass a factory that builds an equivalent client.",
                nameof(client));

    /// <summary>
    /// Conversation whose Pi process can be replaced on reconnect: the
    /// factory must build an equivalent client (same session options) on
    /// every call. Reconnecting swaps the process while the session —
    /// Pi-native and persisted — stays put.
    /// </summary>
    public PiConversation(Func<PiRpcClient> clientFactory)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _client = _clientFactory();
        Attach(_client);
    }

    /// <summary>Latest compact activity (tool runs, errors, retries). Null when quiet.</summary>
    public event EventHandler<string>? ActivityChanged;

    /// <summary>
    /// Structured tool activity for the conversation feed: tool name, a short
    /// human summary, an observed file path for edit/write calls (null
    /// otherwise), and the full shell command for bash/powershell (in
    /// <see cref="PiToolActivity.Summary"/>). Raised on the RPC reader
    /// thread; the subscriber must marshal to its own thread.
    /// </summary>
    public event EventHandler<PiToolActivity>? ToolActivityChanged;

    /// <summary>
    /// Forwarded when the RPC transport dies (stdout EOF or stream error).
    /// The conversation keeps its restored history; call
    /// <see cref=\"ReconnectAsync\"/> to attach a fresh process to the same
    /// persisted session. Raised on the transport thread.
    /// </summary>
    public event EventHandler<Exception>? ConnectionLost;

    public bool IsConnected
    {
        get
        {
            lock (_clientGate)
            {
                return !_disposed && _client.IsRunning;
            }
        }
    }

    /// <summary>Successful reconnects since construction. Diagnostic only.</summary>
    public int ReconnectCount
    {
        get
        {
            lock (_clientGate)
            {
                return _reconnectCount;
            }
        }
    }

    /// <summary>
    /// Attach a fresh Pi process to the same persisted session after a
    /// transport failure (or a GUI close). Retires the previous writer FIRST
    /// (detach + dispose) and only then connects the replacement, so two
    /// live Pi processes never share one session — even briefly. Builds the
    /// replacement before retiring, so a factory failure leaves the old
    /// client untouched. Restored history is kept until the refresh lands,
    /// so a failed reconnect never wipes what was already known, and the
    /// conversation stays disconnected for a host retry. Never reports
    /// interruption as success: no receipt is issued here — the interrupted
    /// run keeps whatever the host recorded, and restored history speaks
    /// for itself.
    /// </summary>
    public async Task<PiEngineState> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        PiRpcClient previous;
        PiRpcClient replacement;
        lock (_clientGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _client;
            replacement = _clientFactory();
        }

        // Single-writer rule: the old process is gone before the new one
        // starts. History still holds the pre-reconnect restore throughout.
        Detach(previous);
        await previous.DisposeAsync().ConfigureAwait(false);

        try
        {
            await replacement.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        bool swapped;
        lock (_clientGate)
        {
            swapped = !_disposed;
            if (swapped)
            {
                _client = replacement;
                Attach(replacement);
            }
        }

        if (!swapped)
        {
            await replacement.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(PiConversation));
        }

        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
        var state = await Client.GetStateAsync(cancellationToken).ConfigureAwait(false);
        lock (_clientGate)
        {
            _reconnectCount++;
        }

        return state;
    }

    private PiRpcClient Client
    {
        get
        {
            lock (_clientGate)
            {
                return _client;
            }
        }
    }

    private void Attach(PiRpcClient client)
    {
        client.EventReceived += OnClientEvent;
        client.TransportFailed += OnTransportFailed;
        _bridge?.Reattach(client);
    }

    /// <summary>
    /// Attach a live-feed bridge: its tap follows every client this conversation
    /// uses, including replacements after reconnect, without host rewiring.
    /// Call once, typically right after construction.
    /// </summary>
    public void AttachBridge(FeedBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        lock (_clientGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Replacing the bridge detaches the previous one FIRST so no
            // orphaned tap keeps routing to a superseded feed. Field access
            // (not the Client property) avoids re-entering this lock.
            if (!ReferenceEquals(_bridge, bridge))
            {
                _bridge?.Detach();
                _bridge = bridge;
            }

            bridge.Reattach(_client);
        }
    }

    private void Detach(PiRpcClient client)
    {
        client.EventReceived -= OnClientEvent;
        client.TransportFailed -= OnTransportFailed;
        _bridge?.Detach();
    }

    /// <summary>Stop feeding the attached bridge. The bridge itself stays host-owned.</summary>
    public void DetachBridge()
    {
        FeedBridge? bridge;
        lock (_clientGate)
        {
            bridge = _bridge;
            _bridge = null;
        }

        bridge?.Detach();
    }

    private void OnTransportFailed(object? sender, Exception error)
        => ConnectionLost?.Invoke(this, error);

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
        var client = Client;
        ObjectDisposedException.ThrowIf(_disposed, this);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await RefreshHistoryAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshHistoryAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetMessagesAsync(cancellationToken).ConfigureAwait(false);
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
        var client = Client;

        void Counter(object? sender, PiRpcEventArgs args) => CountRunError(args, ref errors);
        client.EventReceived += Counter;
        try
        {
            var (text, aborted) = await client.PromptAndWaitAsync(message, textProgress, cancellationToken).ConfigureAwait(false);
            var state = await client.GetStateAsync(cancellationToken).ConfigureAwait(false);
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
            client.EventReceived -= Counter;
        }
    }

    public Task AbortAsync(CancellationToken cancellationToken = default)
        => Client.AbortAsync(cancellationToken);

    private void OnClientEvent(object? sender, PiRpcEventArgs args)
    {
        var activity = DescribeActivity(args.Record);
        if (activity is not null)
        {
            ActivityChanged?.Invoke(this, activity);
        }

        var toolActivity = ParseToolActivity(args.Record);
        if (toolActivity is not null)
        {
            ToolActivityChanged?.Invoke(this, toolActivity);
        }
    }

    /// <summary>
    /// Structured tool lifecycle for the feed. Start events carry the observed
    /// file path (edit/write) or shell command (bash/powershell); end events
    /// carry only the outcome. Unknown tools and malformed records yield a
    /// name-only activity or null — never a fabricated path.
    /// </summary>
    internal static PiToolActivity? ParseToolActivity(JsonDocument record)
    {
        try
        {
            var root = record.RootElement;
            var type = PiRpcFraming.GetEventType(record);
            if (type == "tool_execution_start" && root.TryGetProperty("toolName", out var startName))
            {
                var toolName = startName.GetString() ?? "?";
                var args = root.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : (JsonElement?)null;
                return new PiToolActivity(toolName, SummarizeToolArgs(toolName, args), ExtractToolPath(toolName, args), IsError: false, IsStart: true);
            }

            if (type == "tool_execution_end" && root.TryGetProperty("toolName", out var endName))
            {
                var failed = root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
                return new PiToolActivity(endName.GetString() ?? "?", null, null, IsError: failed, IsStart: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static string? SummarizeToolArgs(string toolName, JsonElement? args)
    {
        if (args is null)
        {
            return null;
        }

        if ((toolName.Equals("bash", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            && args.Value.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String)
        {
            return PiRedaction.Redact(TruncateSingleLine(command.GetString() ?? string.Empty, 300));
        }

        var path = ExtractToolPath(toolName, args);
        return path;
    }

    private static string? ExtractToolPath(string toolName, JsonElement? args)
    {
        // Only edit/write paths are reported: a read/grep path is not a change.
        if (!toolName.Equals("edit", StringComparison.OrdinalIgnoreCase)
            && !toolName.Equals("write", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (args is null)
        {
            return null;
        }

        foreach (var name in new[] { "path", "file", "filePath", "filename" })
        {
            if (args.Value.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var path = PiRedaction.Redact(value.GetString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static string? TruncateSingleLine(string text, int maxChars)
    {
        var line = text.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        return line.Length <= maxChars ? line : line[..maxChars] + "…";
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
                    => $"Running tool: {PiRedaction.Name(name.GetString())}…",
                "tool_execution_end" when root.TryGetProperty("toolName", out var endName)
                    => root.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True
                        ? $"Tool {PiRedaction.Name(endName.GetString())} failed."
                        : $"Tool {PiRedaction.Name(endName.GetString())} finished.",
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
        var header = failed ? $"[tool {PiRedaction.Name(name)} failed]" : $"[tool result: {PiRedaction.Name(name)}]";
        return string.IsNullOrEmpty(body) ? header : header + "\n" + PiRedaction.Redact(body);
    }

    private static string FormatBashExecution(JsonElement message)
    {
        var command = message.TryGetProperty("command", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var output = message.TryGetProperty("output", out var o) ? o.GetString() ?? string.Empty : string.Empty;
        var safeCommand = PiRedaction.Redact(command);
        var safeOutput = PiRedaction.Redact(output);
        return string.IsNullOrEmpty(output) ? $"Ran `{safeCommand}`" : $"Ran `{safeCommand}`\n{safeOutput}";
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
        PiRpcClient? client;
        lock (_clientGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            client = _client;
        }

        Detach(client);
        await client.DisposeAsync().ConfigureAwait(false);
    }
}
