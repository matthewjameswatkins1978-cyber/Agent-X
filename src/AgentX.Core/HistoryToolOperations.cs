using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// One correlated unit of persisted Pi tool history (issue #25 Core half):
/// an assistant toolCall block, its matching toolResult/bashExecution row,
/// or either side alone when identity or counterpart is genuinely absent.
///
/// <para><b>Identity.</b> Pairing uses ONLY the observed call ID
/// (<c>toolCall.id</c> on the call block, <c>toolCallId</c> on the result —
/// the same value), unique on both sides and name-consistent when both sides
/// name the tool. Missing, oversized, duplicated or contradictory identity
/// evidence never pairs; there is no positional or count-based inference.</para>
///
/// <para><b>Fields.</b> <see cref="CallId"/> is raw identity evidence (same
/// contract as <see cref="ConversationMessage.CallId"/>: never rendered by
/// Core, the GUI decides). <see cref="ToolName"/> is the observed name
/// passed through <see cref="PiRedaction.Name"/> exactly like
/// <c>[tool result: …]</c> headers. <see cref="ArgumentsJson"/> is the
/// genuine persisted arguments (serialized, redacted) — null when the call
/// side is absent or Pi stored none. <see cref="OutputText"/> is the
/// genuine result body (redacted; may be empty when the result carried no
/// content) — null only when no result was observed. <see cref="IsError"/>
/// is null exactly when <see cref="HasResult"/> is false. Duration comes
/// from the result's own <c>durationMs</c> when present.</para>
///
/// <para><b>Not the copy source.</b> Byte-exact copy/export keeps using
/// ConversationMessage / feed text. This model is additive presentation
/// evidence; it never replaces or rewrites the original rows.</para>
/// </summary>
public sealed record HistoryToolOperation(
    string? CallId,
    string? ToolName,
    string? ArgumentsJson,
    bool HasCall,
    bool HasResult,
    bool? IsError,
    long? DurationMs,
    string? OutputText);

/// <summary>
/// Pure parser that correlates persisted tool history from a
/// <c>get_messages</c> response (same document shape as
/// <c>PiConversation.ParseHistory</c>) into <see cref="HistoryToolOperation"/>
/// units. Additive and side-effect free: ParseHistory's output is untouched,
/// and every message is considered exactly once — nothing is dropped,
/// merged across true boundaries, or fabricated when evidence is missing.
///
/// <para>Scope: Pi session history (<c>get_messages</c>). Imported
/// transcripts (TranscriptImporter) are a different path and are not
/// parsed here. Unknown roles and malformed entries are tolerated the same
/// way ParseHistory tolerates them: skipped individually, never thrown.</para>
/// </summary>
public static class HistoryToolOperations
{
    /// <summary>Parse correlated tool operations. Materializes everything it
    /// needs — the returned list is a frozen snapshot safe to use after the
    /// JsonDocument is disposed.</summary>
    public static IReadOnlyList<HistoryToolOperation> Parse(JsonDocument? response)
    {
        if (response is null || !TryMessages(response, out var messages))
        {
            return Array.Empty<HistoryToolOperation>();
        }

        // One monotonic sequence across both sides, assigned in message walk
        // order: a paired unit's position is its first appearance (normally
        // the call; min() also covers a result that somehow precedes its
        // call), an unmatched result keeps its own position. No reordering
        // assumptions, no positional pairing — the sequence is presentation
        // order only.
        var seq = 0;
        var calls = new List<(int Seq, string? Id, string? RawName, string? Args)>();
        var results = new List<(int Seq, string? Id, string? RawName, bool IsError, long? DurationMs, string Output)>();

        foreach (var message in messages.EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = StringProp(message, "role");
            if (role == "assistant"
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object
                        || StringProp(block, "type") != "toolCall")
                    {
                        continue;
                    }

                    calls.Add((seq++, NormalizeCallId(StringProp(block, "id")),
                        StringProp(block, "name"), ArgumentsOf(block)));
                }
            }
            else if (role is "toolResult" or "bashExecution")
            {
                results.Add((seq++,
                    PiConversation.HistoryCallId(message),
                    StringProp(message, "toolName"),
                    BoolProp(message, "isError"),
                    DurationOf(message),
                    role == "bashExecution"
                        ? BashOutput(message)
                        : PiRedaction.Redact(SafeResultBody(message))));
            }
        }

        // Pairing: an ID is usable ONLY when it appears exactly once on each
        // side (duplicates make identity unprovable — same discipline as
        // ConversationFeed.ReconcileToolResults), and only when the two raw
        // names do not contradict each other (both present and different).
        var callIdCounts = CountIds(calls.Select(c => c.Id));
        var resultIdCounts = CountIds(results.Select(r => r.Id));
        var resultIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < results.Count; r++)
        {
            if (results[r].Id is { } id
                && resultIdCounts[id] == 1
                && callIdCounts.GetValueOrDefault(id) == 1)
            {
                resultIndexById[id] = r;
            }
        }

        var callToResult = new Dictionary<int, int>();
        for (var c = 0; c < calls.Count; c++)
        {
            if (calls[c].Id is not { } id || !resultIndexById.TryGetValue(id, out var r))
            {
                continue;
            }

            var (callName, resultName) = (calls[c].RawName, results[r].RawName);
            if (callName is not null && resultName is not null
                && !callName.Equals(resultName, StringComparison.OrdinalIgnoreCase))
            {
                continue; // Contradictory evidence: never resolved by guessing.
            }

            callToResult[c] = r;
        }

        var pairedResult = new bool[results.Count];
        foreach (var r in callToResult.Values)
        {
            pairedResult[r] = true;
        }

        var operations = new List<(int Order, HistoryToolOperation Op)>(calls.Count + results.Count);
        for (var c = 0; c < calls.Count; c++)
        {
            var call = calls[c];
            if (callToResult.TryGetValue(c, out var r))
            {
                var res = results[r];
                operations.Add((Math.Min(call.Seq, res.Seq), new HistoryToolOperation(
                    CallId: call.Id ?? res.Id,
                    ToolName: RedactName(call.RawName ?? res.RawName),
                    ArgumentsJson: call.Args,
                    HasCall: true,
                    HasResult: true,
                    IsError: res.IsError,
                    DurationMs: res.DurationMs,
                    OutputText: res.Output)));
            }
            else
            {
                operations.Add((call.Seq, new HistoryToolOperation(
                    CallId: call.Id,
                    ToolName: RedactName(call.RawName),
                    ArgumentsJson: call.Args,
                    HasCall: true,
                    HasResult: false,
                    IsError: null,
                    DurationMs: null,
                    OutputText: null)));
            }
        }

        for (var r = 0; r < results.Count; r++)
        {
            if (pairedResult[r])
            {
                continue;
            }

            var res = results[r];
            operations.Add((res.Seq, new HistoryToolOperation(
                CallId: res.Id,
                ToolName: RedactName(res.RawName),
                ArgumentsJson: null,
                HasCall: false,
                HasResult: true,
                IsError: res.IsError,
                DurationMs: res.DurationMs,
                OutputText: res.Output)));
        }

        // Stable order by first appearance. Sequence values are unique per
        // item, so ties cannot occur; OrderBy keeps the sort deterministic.
        operations.Sort((a, b) => a.Order.CompareTo(b.Order));
        var ordered = new HistoryToolOperation[operations.Count];
        for (var i = 0; i < operations.Count; i++)
        {
            ordered[i] = operations[i].Op;
        }

        return ordered;
    }

    private static bool TryMessages(JsonDocument response, out JsonElement messages)
    {
        messages = default;
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Envelope tolerance (Lucy review): the shared readers' own
        // precondition is that "data" is an OBJECT before TryGetProperty
        // is called on it — "[]", {"data":null} and {"data":[]} are empty
        // results, never exceptions. Messages must genuinely be an array.
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return data.TryGetProperty("messages", out messages)
            && messages.ValueKind == JsonValueKind.Array;
    }

    /// <summary>bashExecution display honoring FormatBashExecution's exact
    /// contract while tolerating malformed fields (Lucy review): a
    /// non-string command/output is treated as absent — precisely the
    /// shared formatter's own <c>?? string.Empty</c> fallback — so a corrupt
    /// row degrades instead of throwing. Valid rows go through the shared
    /// formatter unchanged: ParseHistory/copy display is untouched and this
    /// path stays byte-identical for well-formed input.</summary>
    private static string BashOutput(JsonElement message)
    {
        if (IsStringish(message, "command") && IsStringish(message, "output"))
        {
            return PiConversation.FormatBashExecution(message);
        }

        var command = ReadStringOrEmpty(message, "command");
        var output = ReadStringOrEmpty(message, "output");
        var safeCommand = PiRedaction.Redact(command);
        var safeOutput = PiRedaction.Redact(output);
        return string.IsNullOrEmpty(output) ? $"Ran `{safeCommand}`" : $"Ran `{safeCommand}`\n{safeOutput}";
    }

    private static bool IsStringish(JsonElement message, string name)
        => !message.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.String or JsonValueKind.Null;

    private static string ReadStringOrEmpty(JsonElement message, string name)
        => message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>Result body via the shared extractor, guarded (Lucy
    /// review): its String contracts on nested content blocks are strict
    /// and are deliberately NOT changed here — ParseHistory/copy stay
    /// exactly as they are — so an unreadable malformed body degrades to
    /// empty. The row's identity/name/error/duration evidence is preserved
    /// either way; only unreadable text is lost.</summary>
    private static string SafeResultBody(JsonElement message)
    {
        try
        {
            return PiConversation.ExtractContentText(message);
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Same normalization as wire/session readers: raw, non-empty,
    /// ≤160 chars — anything else is absent identity, never guessed.</summary>
    private static string? NormalizeCallId(string? id)
        => string.IsNullOrEmpty(id) || id.Length > 160 ? null : id;

    private static string? ArgumentsOf(JsonElement block)
    {
        if (!block.TryGetProperty("arguments", out var args))
        {
            return null;
        }

        var serialized = args.ValueKind switch
        {
            JsonValueKind.String => args.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => args.GetRawText(),
            _ => null,
        };
        return string.IsNullOrEmpty(serialized) ? null : PiRedaction.Redact(serialized);
    }

    private static long? DurationOf(JsonElement message)
        => message.TryGetProperty("durationMs", out var d)
            && d.ValueKind == JsonValueKind.Number
            && d.TryGetInt64(out var ms)
            ? ms
            : null;

    private static string? StringProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? RedactName(string? rawName)
        => rawName is null ? null : PiRedaction.Name(rawName);

    private static Dictionary<string, int> CountIds(IEnumerable<string?> ids)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (id is not null)
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }
}
