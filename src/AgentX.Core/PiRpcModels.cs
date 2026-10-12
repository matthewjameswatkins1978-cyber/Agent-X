using System.Text.Json;

namespace AgentX.Core;

/// <summary>Failure of one Pi RPC command (success: false response).</summary>
public sealed class PiRpcCommandException(string command, string error)
    : Exception($"Pi command '{command}' failed: {error}")
{
    public string Command { get; } = command;
    public string Error { get; } = error;
}

/// <summary>Pi process exited or became unusable while commands were outstanding.</summary>
public sealed class PiRpcTransportException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>Snapshot of engine state from get_state.</summary>
public sealed record PiEngineState(
    string? ModelId,
    string? ModelName,
    string? Provider,
    string ThinkingLevel,
    bool IsStreaming,
    bool IsCompacting,
    string? SessionId,
    int MessageCount,
    int PendingMessageCount)
{
    public static PiEngineState FromResponse(JsonDocument response)
    {
        var data = response.RootElement.GetProperty("data");
        string? Str(string name)
            => data.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        string? modelId = null;
        string? modelName = null;
        string? provider = null;
        if (data.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
        {
            if (model.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                modelId = id.GetString();
            }

            if (model.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                modelName = name.GetString();
            }

            if (model.TryGetProperty("provider", out var prov) && prov.ValueKind == JsonValueKind.String)
            {
                provider = prov.GetString();
            }
        }

        return new PiEngineState(
            modelId,
            modelName,
            provider,
            Str("thinkingLevel") ?? string.Empty,
            data.TryGetProperty("isStreaming", out var s) && s.ValueKind == JsonValueKind.True,
            data.TryGetProperty("isCompacting", out var c) && c.ValueKind == JsonValueKind.True,
            Str("sessionId"),
            data.TryGetProperty("messageCount", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0,
            data.TryGetProperty("pendingMessageCount", out var pmc) && pmc.ValueKind == JsonValueKind.Number ? pmc.GetInt32() : 0);
    }
}

/// <summary>One session event or extension record received outside command correlation.</summary>
public sealed class PiRpcEventArgs(JsonDocument record, string rawLine) : EventArgs
{
    public JsonDocument Record { get; } = record;
    public string RawLine { get; } = rawLine;
    public string EventType => PiRpcFraming.GetEventType(Record);
}
