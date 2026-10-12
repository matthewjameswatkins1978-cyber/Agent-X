using System.Text;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Strict JSONL framing for Pi RPC: one complete JSON object per record terminated by LF.
/// Splits only on LF, strips an optional preceding CR, and never treats Unicode
/// separators (U+2028/U+2029) as boundaries. Reads continuously as UTF-8 bytes.
/// </summary>
public static class PiRpcFraming
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Encode(IDictionary<string, object?> command)
    {
        var json = JsonSerializer.Serialize(command, SerializerOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var framed = new byte[bytes.Length + 1];
        Buffer.BlockCopy(bytes, 0, framed, 0, bytes.Length);
        framed[^1] = (byte)'\n';
        return framed;
    }

    /// <summary>Strip one trailing CR so both LF and CRLF input are accepted.</summary>
    public static string StripRecordTerminator(string line)
        => line.EndsWith('\r') ? line[..^1] : line;

    public static bool IsResponseRecord(JsonDocument record, out string? id, out string? command)
    {
        id = null;
        command = null;
        if (!record.RootElement.TryGetProperty("type", out var type)
            || type.GetString() != "response")
        {
            return false;
        }

        if (record.RootElement.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String)
        {
            id = idProp.GetString();
        }

        if (record.RootElement.TryGetProperty("command", out var commandProp))
        {
            command = commandProp.GetString();
        }

        return true;
    }

    public static string GetEventType(JsonDocument record)
        => record.RootElement.TryGetProperty("type", out var type)
            ? type.GetString() ?? string.Empty
            : string.Empty;

    public static string? GetTextDelta(JsonDocument record)
    {
        try
        {
            if (GetEventType(record) != "message_update")
            {
                return null;
            }

            var update = record.RootElement.GetProperty("assistantMessageEvent");
            if (update.GetProperty("type").GetString() != "text_delta")
            {
                return null;
            }

            return update.TryGetProperty("delta", out var delta) ? delta.GetString() : null;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    public static bool IsSettledEvent(JsonDocument record, out bool aborted)
    {
        aborted = false;
        if (GetEventType(record) != "agent_settled")
        {
            return false;
        }

        if (record.RootElement.TryGetProperty("aborted", out var abortedProp)
            && abortedProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            aborted = abortedProp.GetBoolean();
        }

        return true;
    }
}
