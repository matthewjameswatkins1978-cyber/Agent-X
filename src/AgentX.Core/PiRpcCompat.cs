using System.Diagnostics;
using System.Text;

namespace AgentX.Core;

/// <summary>
/// Executable-reported Pi version ("1.2.0"). INFORMATIONAL ONLY: the worker
/// runtime never gates behavior on a version number — probed
/// <see cref="PiRpcCapabilities"/> are the authority (a fork or backport can
/// change behavior without changing the number). Null version + working
/// probes is a perfectly good runtime state.
/// </summary>
public sealed record PiVersionInfo(string Raw, int Major, int Minor, int Patch)
{
    public static PiVersionInfo? TryParse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();
        var start = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsDigit(text[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        var parts = text[start..end].TrimEnd('.').Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }

        var patch = parts.Length >= 3 && int.TryParse(parts[2], out var p) ? p : 0;
        return new PiVersionInfo(text, major, minor, patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

/// <summary>
/// Probed RPC capabilities of ONE live Pi connection. Booleans come from
/// actually issuing each command (see <see cref="PiRpcCompat.ProbeAsync"/>):
/// success = supported, command-level rejection = not supported. Transport
/// failures are NOT capability answers and propagate instead.
///
/// Prompt/abort/bash are deliberately absent: probing them costs inference
/// or writes session rows, so they are baseline protocol — failures surface
/// as typed exceptions at use time, not as preflight guesses.
/// </summary>
public sealed record PiRpcCapabilities(
    bool GetState,
    bool GetMessages,
    bool AvailableModels,
    bool SetModel,
    bool AvailableThinkingLevels,
    bool SetThinkingLevel)
{
    public static PiRpcCapabilities None => new(false, false, false, false, false, false);

    /// <summary>The model/thinking surface the worker runtime confirms through state.</summary>
    public bool StateOps => GetState;
}

/// <summary>
/// Minimal version-tolerant RPC compatibility boundary. Two jobs:
/// (1) detect the executable's reported version for display/diagnostics,
/// (2) probe which RPC capabilities THIS connection actually supports by
/// issuing the commands themselves — no version tables, no hardcoded error
/// strings, no assumptions about future releases.
///
/// Probes are side-effect free by construction: no-argument queries, plus
/// idempotent set calls that re-apply the CURRENT state (verified process-
/// scoped in Pi 1.1.0/1.2.0: model/thinking writes never touch
/// settings.json or other processes).
/// </summary>
public static class PiRpcCompat
{
    /// <summary>
    /// Run <c>&lt;executable&gt; --version</c> and parse its first line.
    /// Returns null when the version cannot be determined — callers must
    /// treat capabilities, not the number, as the authority.
    /// </summary>
    public static async Task<PiVersionInfo?> DetectVersionAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var (fileName, arguments) = PiRpcClient.ResolveLaunch(executablePath, "--version");
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            var firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return PiVersionInfo.TryParse(firstLine) ?? PiVersionInfo.TryParse(stdout);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return null;
        }
    }

    /// <summary>
    /// Probe the capability surface of a live connection. Each probe issues
    /// its command and classifies ONLY the response: success → supported;
    /// a command-level rejection (PiRpcCommandException) → not supported;
    /// transport faults propagate (a dead pipe is not a capability answer).
    /// Idempotent sets re-apply the state reported by get_state, so probing
    /// never changes what the connection is doing.
    /// </summary>
    public static async Task<PiRpcCapabilities> ProbeAsync(PiRpcClient client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        var getState = await ProbeAsync(client, "get_state", null, cancellationToken).ConfigureAwait(false);
        var getMessages = await ProbeAsync(client, "get_messages", null, cancellationToken).ConfigureAwait(false);
        var availableModels = await ProbeAsync(client, "get_available_models", null, cancellationToken).ConfigureAwait(false);
        var availableThinking = await ProbeAsync(client, "get_available_thinking_levels", null, cancellationToken).ConfigureAwait(false);

        // Idempotent set probes: re-apply what get_state reports. When the
        // state needed for idempotence is unavailable, the set capability is
        // unknown and reported as unsupported (fail closed, never assume).
        var setModel = false;
        var setThinking = false;
        if (getState)
        {
            try
            {
                using var state = await client.SendRawAsync("get_state", null, cancellationToken).ConfigureAwait(false);
                var data = state.RootElement.TryGetProperty("data", out var d) ? d : default;
                if (data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && data.TryGetProperty("model", out var model) && model.ValueKind == System.Text.Json.JsonValueKind.Object
                    && model.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String
                    && model.TryGetProperty("provider", out var prov) && prov.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    setModel = await ProbeAsync(client, "set_model",
                        new Dictionary<string, object?> { ["provider"] = prov.GetString(), ["modelId"] = id.GetString() },
                        cancellationToken).ConfigureAwait(false);
                }

                if (data.ValueKind == System.Text.Json.JsonValueKind.Object
                    && data.TryGetProperty("thinkingLevel", out var level) && level.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(level.GetString()))
                {
                    setThinking = await ProbeAsync(client, "set_thinking_level",
                        new Dictionary<string, object?> { ["level"] = level.GetString() },
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (PiRpcTransportException)
            {
                throw;
            }
        }

        return new PiRpcCapabilities(getState, getMessages, availableModels, setModel, availableThinking, setThinking);
    }

    private static async Task<bool> ProbeAsync(
        PiRpcClient client,
        string command,
        Dictionary<string, object?>? payload,
        CancellationToken cancellationToken)
    {
        // Per-probe timeout: a future Pi that ignores an unknown command
        // must fail the probe closed (unsupported), never hang the connect.
        // The caller's own cancellation still propagates.
        using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendRawAsync(command, payload, probeTimeout.Token).ConfigureAwait(false);
            return PiRpcClient.IsCommandSuccess(response);
        }
        catch (PiRpcCommandException)
        {
            // Command-level rejection from a no-arg/idempotent probe means
            // this Pi build does not support the command. Deliberately not
            // matched on message text: wording is not a stable contract.
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // Probe timed out on its own clock: unsupported (fail closed).
        }
    }
}
