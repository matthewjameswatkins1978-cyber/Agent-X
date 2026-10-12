using System.Text;

namespace AgentX.Core;

/// <summary>
/// One row in the GUI conversation feed. <see cref="Text"/> is always the
/// complete text — display truncation happens in the view, never here.
/// </summary>
public sealed record FeedEntry(
    long Seq,
    string Role,
    string Text,
    bool IsStreaming = false,
    string? Note = null,
    string? Path = null,
    bool IsMutating = false,
    bool IsError = false)
{
    public static string TruncateForDisplay(string text, int maxChars = 2000)
        => text.Length <= maxChars
            ? text
            : text[..maxChars] + $"… [truncated for display; all {text.Length:N0} chars preserved — copy the message for the full text]";
}

/// <summary>One session-map row. <see cref="Seq"/> is the evidence link: the feed entry number.</summary>
public sealed record FeedMilestone(string Kind, string Title, string? Detail, long Seq);

/// <summary>
/// Derived session map. Everything here is observed Pi activity or an
/// explicit heuristic label — decisions are never inferred (see docs).
/// </summary>
public sealed record SessionMapSnapshot(
    string? CurrentActivity,
    IReadOnlyList<FeedMilestone> Milestones,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> TestCommands,
    int ErrorCount,
    string? LastReceipt)
{
    public string ToDisplayText(bool verbose)
    {
        var output = new StringBuilder();
        output.AppendLine($"Current activity: {CurrentActivity ?? "idle"}");
        output.AppendLine(ErrorCount == 0
            ? "Error events observed: none"
            : $"Error events observed: {ErrorCount} — review before trusting this answer.");
        if (!string.IsNullOrEmpty(LastReceipt))
        {
            output.AppendLine($"Last run: {LastReceipt}");
        }

        output.AppendLine($"Changed files (observed via edit/write calls, not verified on disk): {(ChangedFiles.Count == 0 ? "none yet" : string.Join(", ", ChangedFiles))}");
        output.AppendLine(TestCommands.Count == 0
            ? "Test commands (heuristic): none observed"
            : $"Test commands (heuristic, outcome unknown): {string.Join(" | ", TestCommands)}");
        output.AppendLine($"Milestones ({Milestones.Count}):");
        foreach (var milestone in Milestones)
        {
            output.Append($"  [#{milestone.Seq} {milestone.Kind}] {milestone.Title}");
            if (verbose && !string.IsNullOrEmpty(milestone.Detail))
            {
                output.Append($" — {milestone.Detail}");
            }

            output.AppendLine();
        }

        return output.ToString();
    }
}

/// <summary>
/// UI-free ordered conversation feed: restored history plus the live turn,
/// tool activity, errors and run receipts. The GUI binds a snapshot of this
/// to a virtualized list and flushes streamed deltas in batches. All members
/// are lock-protected; <see cref="Changed"/> fires on mutation and the
/// subscriber must marshal to its own thread (same contract as the RPC
/// connection: copy what is needed during the callback).
/// </summary>
public sealed class ConversationFeed
{
    private readonly Lock _gate = new();
    private readonly List<FeedEntry> _entries = new();
    private long _nextSeq = 1;
    private string? _activity;

    public event Action? Changed;

    public IReadOnlyList<FeedEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public void LoadHistory(IReadOnlyList<ConversationMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        lock (_gate)
        {
            _entries.Clear();
            foreach (var message in history)
            {
                _entries.Add(new FeedEntry(_nextSeq++, message.Role, message.Text));
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Replace the whole feed with previously snapshotted entries (same
    /// workspace, later visit). Entries are immutable records, so a
    /// <see cref="Snapshot"/> array round-trips exactly; the sequence
    /// counter resumes past the highest restored Seq.
    /// </summary>
    public void LoadSnapshot(IReadOnlyList<FeedEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            _entries.Clear();
            _entries.AddRange(entries);
            if (entries.Count > 0)
            {
                _nextSeq = Math.Max(_nextSeq, entries.Max(e => e.Seq) + 1);
            }
        }

        Changed?.Invoke();
    }

    public long AddUser(string text)
        => Add(new FeedEntry(NextSeq(), "user", text));

    public long BeginAssistantTurn()
        => Add(new FeedEntry(NextSeq(), "assistant", string.Empty, IsStreaming: true));

    public void AppendDelta(long seq, string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            if (index < 0 || !_entries[index].IsStreaming)
            {
                return;
            }

            _entries[index] = _entries[index] with { Text = _entries[index].Text + delta };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Replace a streaming turn's text with the authoritative full text.
    /// The RPC client returns the exact accumulated string on completion;
    /// it wins over the progressively flushed chunks (same deltas, but free
    /// of any progress-reporting race). No-op when the turn already ended.
    /// </summary>
    public void ReconcileStreamingText(long seq, string authoritativeText)
    {
        ArgumentNullException.ThrowIfNull(authoritativeText);
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            if (index < 0 || !_entries[index].IsStreaming)
            {
                return;
            }

            _entries[index] = _entries[index] with { Text = authoritativeText };
        }

        Changed?.Invoke();
    }

    public void EndAssistantTurn(long seq, PiRunReceipt? receipt)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            if (index >= 0)
            {
                _entries[index] = _entries[index] with { IsStreaming = false };
            }

            // Settling the turn means Pi is no longer working: drop transient
            // activity here (not only in the host) so no map render — whatever
            // its call order — can show a stale "working" state once idle.
            _activity = null;

            if (receipt is not null)
            {
                _entries.Add(new FeedEntry(
                    _nextSeq++,
                    "receipt",
                    receipt.Display,
                    Note: receipt.RunHealthy ? "healthy" : "needs review",
                    IsError: !receipt.RunHealthy));
            }
        }

        Changed?.Invoke();
    }

    public long AddToolStart(string toolName, string? summary, string? path, bool isMutating)
        => Add(new FeedEntry(NextSeq(), "tool", summary ?? string.Empty,
            Note: $"started: {toolName}", Path: path, IsMutating: isMutating));

    public long AddToolEnd(string toolName, bool isError)
        => Add(new FeedEntry(NextSeq(), "tool", isError ? $"Tool {toolName} failed." : $"Tool {toolName} finished.",
            Note: isError ? $"failed: {toolName}" : $"finished: {toolName}", IsError: isError));

    public long AddError(string text)
        => Add(new FeedEntry(NextSeq(), "system", text, Note: "error", IsError: true));

    public long AddSystemNotice(string text)
        => Add(new FeedEntry(NextSeq(), "system", text));

    public void SetActivity(string? activity)
    {
        lock (_gate)
        {
            _activity = activity;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Full text of one entry by Seq, or null when unknown. Copy actions must
    /// use this (the authoritative Core text), never a possibly stale view
    /// snapshot: live rows are created empty and only the feed sees every
    /// chunk, reconcile and cancellation partials.
    /// </summary>
    public string? GetEntryText(long seq)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            return index < 0 ? null : _entries[index].Text;
        }
    }

    /// <summary>
    /// Full conversation text with role labels. Message bodies are
    /// byte-preserved; only the labels and newlines are added.
    /// </summary>
    public string BuildTranscriptText()
    {
        FeedEntry[] entries;
        lock (_gate)
        {
            entries = _entries.ToArray();
        }

        var output = new StringBuilder();
        foreach (var entry in entries)
        {
            var label = entry.Role switch
            {
                "user" => "You",
                "assistant" => "Pi",
                "tool" => $"Tool{(entry.Note is not null ? $" ({entry.Note})" : string.Empty)}",
                "receipt" => "Run receipt",
                _ => entry.Role,
            };
            output.Append('[').Append(label).Append(" #").Append(entry.Seq).AppendLine("]");
            output.AppendLine(entry.Text);
        }

        return output.ToString();
    }

    public SessionMapSnapshot BuildMap()
    {
        FeedEntry[] entries;
        string? activity;
        lock (_gate)
        {
            entries = _entries.ToArray();
            activity = _activity;
        }

        var milestones = new List<FeedMilestone>();
        var changedFiles = new List<string>();
        var testCommands = new List<string>();
        var errors = 0;
        string? lastReceipt = null;

        foreach (var entry in entries)
        {
            // Error EVENTS count (tool failures, error notices). Receipt rows
            // only summarize the run (their Display already says "Settled
            // with N problems"); counting them too would inflate one failure
            // into two. Transport loss is recorded as an error entry by the
            // host, never as a synthetic receipt.
            if (entry.IsError && entry.Role != "receipt")
            {
                errors++;
            }

            switch (entry.Role)
            {
                case "user":
                    milestones.Add(new FeedMilestone("prompt", FirstLine(entry.Text, 120), null, entry.Seq));
                    break;
                case "tool" when entry.Note is not null && entry.Note.StartsWith("started:", StringComparison.Ordinal):
                    var toolName = entry.Note["started:".Length..].Trim();
                    milestones.Add(new FeedMilestone("tool", $"Ran {toolName}", entry.Text.Length == 0 ? null : FirstLine(entry.Text, 200), entry.Seq));
                    if (entry.IsMutating && !string.IsNullOrEmpty(entry.Path) && !changedFiles.Contains(entry.Path))
                    {
                        changedFiles.Add(entry.Path);
                    }

                    if (IsTestCommand(toolName, entry.Text) && !testCommands.Contains(entry.Text))
                    {
                        testCommands.Add(entry.Text);
                    }

                    break;
                case "tool":
                    milestones.Add(new FeedMilestone(
                        entry.IsError ? "error" : "tool",
                        FirstLine(entry.Text, 120), entry.Note, entry.Seq));
                    break;
                case "receipt":
                    lastReceipt = entry.Text;
                    milestones.Add(new FeedMilestone("receipt", FirstLine(entry.Text, 140), null, entry.Seq));
                    break;
                case "system" when entry.Text.StartsWith("[context summary]", StringComparison.Ordinal):
                    milestones.Add(new FeedMilestone("compaction", FirstLine(entry.Text, 140), null, entry.Seq));
                    break;
                case "system" when entry.IsError:
                    milestones.Add(new FeedMilestone("error", FirstLine(entry.Text, 140), null, entry.Seq));
                    break;
            }
        }

        return new SessionMapSnapshot(activity, milestones, changedFiles, testCommands, errors, lastReceipt);
    }

    private long Add(FeedEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }

        Changed?.Invoke();
        return entry.Seq;
    }

    private long NextSeq()
    {
        lock (_gate)
        {
            return _nextSeq++;
        }
    }

    internal static bool IsTestCommand(string toolName, string command)
    {
        if (command.Length == 0)
        {
            return false;
        }

        if (!toolName.Equals("bash", StringComparison.OrdinalIgnoreCase)
            && !toolName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TestCommandMarkers.Any(marker =>
            command.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] TestCommandMarkers =
    [
        "dotnet test", "pytest", "npm test", "npx jest", "npx vitest",
        "cargo test", "go test", "ctest", "gradlew test", "mvn test",
    ];

    private static string FirstLine(string text, int maxChars)
    {
        var line = text;
        var newline = line.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            line = line[..newline];
        }

        line = line.Trim();
        if (line.Length == 0)
        {
            return "(empty)";
        }

        return line.Length <= maxChars ? line : line[..maxChars] + "…";
    }
}
