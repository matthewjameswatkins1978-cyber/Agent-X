using System.Text.RegularExpressions;

namespace AgentX.Core;

/// <summary>
/// Deterministic, fail-closed redaction for captured Pi traffic. Applied at
/// decode time, before anything is stored or broadcast, so every retained
/// string, subscriber payload, and render level is already safe.
///
/// What it catches: bearer/basic authorization values, api_key/secret/token/
/// password-style key=value pairs (including ENV_SHOUTING_CASE assignments),
/// private-key blocks, and well-known token prefixes (GitHub, OpenAI, Slack,
/// AWS). What it cannot promise: split-across-deltas secrets, bare
/// high-entropy tokens with no scheme or key, novel schemes, or secrets
/// inside opaque blobs. Those limits are documented in
/// docs/observability.md; unknown raw payloads are withheld, never kept.
///
/// Performance: a keyword fast-path skips pattern work entirely when no
/// sensitive shape can be present, and every pattern is written to run in
/// linear time (bounded repetitions, no nested open-ended quantifiers), so
/// large benign outputs cannot cause catastrophic backtracking.
/// </summary>
public static partial class PiRedaction
{
    private const string Marker = "[redacted]";

    private static readonly string[] TriggerWords =
    [
        "api_key", "apikey", "api-key", "secret", "passwd", "password", "token",
        "bearer", "authorization", "basic", "private key", "private_key",
        "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat",
        "sk-proj-", "sk-live-", "xox", "akia", "-----begin",
    ];

    // key=value with a sensitive-looking key (leading run bounded so matching
    // stays linear). No lookbehind: the key may follow any quote or boundary
    // (JSON and shell quoting included); the lazy leading run absorbs the
    // full identifier so mid-word starts produce the same replacement.
    [GeneratedRegex(
        """(?<key>[A-Za-z0-9_]{0,64}?(?:api[_-]?key|secret|passwd|password|token|bearer|authorization|private[_-]?key)[A-Za-z0-9_]{0,64})['\"]?\s*[:=]\s*['\"]?(?!\[redacted\])[^\s'\";,\}]{1,512}['\"]?""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    // JSON quoted form: "api_key":"..." — shape-preserving replacement keeps
    // the surrounding quotes so retained JSON stays well-formed.
    [GeneratedRegex(
        """"([A-Za-z0-9_]{0,64}?(?:api[_-]?key|secret|passwd|password|token|bearer|authorization|private[_-]?key)[A-Za-z0-9_]{0,64})"\s*:\s*"(?!\[redacted\])[^"\\]{1,512}"""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonKeyValue();

    // Standalone bearer/basic credentials with a bounded value.
    [GeneratedRegex(
        """\b(?<scheme>bearer|basic)\s+['\"]?[A-Za-z0-9\-._~+/=]{8,512}""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthScheme();

    // PEM private-key blocks, multiline.
    [GeneratedRegex(
        """-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z0-9 ]*PRIVATE KEY-----""",
        RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    // Well-known token prefixes with bounded tails.
    [GeneratedRegex(
        """\b(?:ghp|gho|ghu|ghs|ghr|github_pat)_[A-Za-z0-9_]{1,256}|\bsk-(?:proj|live)-[A-Za-z0-9_-]{1,256}|\bxox[bpas]-[A-Za-z0-9-]{1,256}|\bAKIA[0-9A-Z]{16}""",
        RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefix();

    /// <summary>Redact one retained string. Never returns null; never throws.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Fast path: no trigger word means no pattern can match. This keeps
        // large benign outputs (build logs, listings) cheap and linear.
        var triggered = false;
        foreach (var word in TriggerWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                triggered = true;
                break;
            }
        }

        if (!triggered)
        {
            return text;
        }

        try
        {
            var redacted = PrivateKey().Replace(text, Marker);
            redacted = TokenPrefix().Replace(redacted, Marker);
            redacted = AuthScheme().Replace(redacted, m => m.Groups["scheme"].Value + " " + Marker);
            redacted = JsonKeyValue().Replace(redacted, m => "\"" + m.Groups[1].Value + "\":\"" + Marker + "\"");
            redacted = KeyValue().Replace(redacted, m => m.Groups["key"].Value + "=" + Marker);
            return redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological input must fail closed: withhold, do not retain.
            return Marker;
        }
    }

    /// <summary>
    /// Gate one metadata field: redact secret shapes, then keep the value
    /// only when it matches the expected identifier shape and length.
    /// Anything else becomes [withheld] — fail-closed for attacker-shaped
    /// names, ids, and tokens. Empty stays empty (no information either way).
    /// </summary>
    public static string Meta(string? value, Func<string, bool> shape, int maxLength)
    {
        var redacted = Redact(value ?? string.Empty);
        if (redacted.Length == 0)
        {
            return string.Empty;
        }

        if (redacted.Length > maxLength)
        {
            return "[withheld]";
        }

        try
        {
            return shape(redacted) ? redacted : "[withheld]";
        }
        catch (RegexMatchTimeoutException)
        {
            return "[withheld]";
        }
    }

    /// <summary>Tool names, call ids, command/session ids: tight identifier shape.</summary>
    public static string Id(string? value) => Meta(value, s => IdShape().IsMatch(s), 160);

    /// <summary>Tool names: letters, digits, _, ., - (covers mcp__ namespaced tools).</summary>
    public static string Name(string? value) => Meta(value, s => NameShape().IsMatch(s), 128);

    /// <summary>Event type tokens: attacker-controlled types that fail the shape are withheld.</summary>
    public static string TypeToken(string? value) => Meta(value, s => TypeShape().IsMatch(s), 96);

    /// <summary>Single JSON property name from an unknown shape.</summary>
    public static string FieldName(string? value) => Meta(value, s => NameShape().IsMatch(s), 128);

    /// <summary>Model identifiers may contain slashes (provider/model).</summary>
    public static string Model(string? value) => Meta(value, s => ModelShape().IsMatch(s), 160);

    /// <summary>Prompt dispositions are a closed set; anything else is withheld.</summary>
    public static string Disposition(string? value)
    {
        var redacted = Redact(value ?? string.Empty);
        return redacted is "started" or "queued" or "handled" ? redacted : "[withheld]";
    }

    /// <summary>Filesystem paths: redacted and length-capped, never shape-gated (legit paths vary).</summary>
    public static string Path(string? value, int maxLength = 512)
    {
        var redacted = Redact(value ?? string.Empty);
        if (redacted.Length == 0)
        {
            return string.Empty;
        }

        return redacted.Length <= maxLength ? redacted : "[withheld]";
    }

    [GeneratedRegex("^[A-Za-z0-9_|.-]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdShape();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex NameShape();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,96}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeShape();

    /// <summary>Display text metadata (model names, providers): redacted with a loose identifier shape.</summary>
    public static string Text(string? value) => Meta(value, s => TextShape().IsMatch(s), 160);

    [GeneratedRegex("^[A-Za-z0-9_ .\\-/|]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex TextShape();

    [GeneratedRegex("^[A-Za-z0-9_|./-]{1,160}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelShape();
    /// <summary>True when redaction changed the text (a sensitive shape was present).</summary>
    public static bool Changed(string? original, string redacted)
        => !string.Equals(original ?? string.Empty, redacted, StringComparison.Ordinal);
}
