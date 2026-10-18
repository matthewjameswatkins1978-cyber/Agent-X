using System.Text;

namespace AgentX.Core;

/// <summary>
/// One row in the GUI conversation feed. <see cref="Text"/> is always the
/// complete text — display truncation happens in the view, never here.
/// <see cref="CallId"/> is Pi's observed tool-call ID (raw, never rendered),
/// set ONLY on a start row paired with its end; unverified end rows are
/// always null. It is identity evidence for history reconciliation.
/// </summary>
public sealed record FeedEntry(
    long Seq,
    string Role,
    string Text,
    bool IsStreaming = false,
    string? Note = null,
    string? Path = null,
    bool IsMutating = false,
    bool IsError = false,
    string? CallId = null)
{
    public static string TruncateForDisplay(string text, int maxChars = 2000)
        => text.Length <= maxChars
            ? text
            : text[..maxChars] + $"… [truncated for display; all {text.Length:N0} chars preserved — copy the message for the full text]";

    /// Explicitly bounded inline expansion for large tool output. Rendering
    /// one inline per line freezes the UI on 100k-line outputs, so expanded
    /// display stops at the first bound (lines or chars) with a notice
    /// carrying the EXACT totals. The source text is never altered — copy
    /// actions keep using the full text, so nothing is silently lost.
    public static string TruncateExpanded(string text, int maxLines = 2000, int maxChars = 100_000)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return text;
        }

        var lines = 0;
        var cut = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n' && ++lines == maxLines)
            {
                cut = i + 1;
                break;
            }
        }

        var lineBounded = cut >= 0 && cut < text.Length;
        var charBounded = text.Length > maxChars;
        if (!lineBounded && !charBounded)
        {
            return text;
        }

        var shown = lineBounded && cut < maxChars ? text[..cut] : text[..Math.Min(maxChars, text.Length)];
        var (shownLines, totalLines) = CountLines(shown, text);
        return shown + $"… [expanded display bounded: showing {shownLines:N0} of {totalLines:N0} lines " +
            $"({shown.Length:N0} of {text.Length:N0} chars) — copy the message for the full text]";
    }

    private static (int Shown, int Total) CountLines(string shown, string full)
    {
        return (CountContentLines(shown), CountContentLines(full));
    }

    private static int CountContentLines(string text)
    {
        var lines = 0;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        // A trailing newline terminates the last line rather than starting
        // an empty one: "a\nb\n" is two lines, not three.
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            lines++;
        }

        return lines;
    }
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

/// Result of a history-reconcile attempt. Only Applied and InSync leave
/// rows touched; both skips leave the existing safe output alone
/// (SkippedAmbiguous additionally records one explicit marker row so the
/// refusal is visible, not silent).
public enum ReconcileOutcome
{
    Applied,
    InSync,
    SkippedCountMismatch,
    SkippedAmbiguous,
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
    // Open starts by Pi's observed call ID (exact identity). Starts whose
    // wire record carried no ID fall back to per-name stacks, paired only
    // when unambiguous (see AddToolEnd). Both are pairing indexes only:
    // rows are the record and are never rewritten by pairing.
    private readonly Dictionary<string, long> _openByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<long>> _openByName = new(StringComparer.OrdinalIgnoreCase);
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
            AbandonOpenToolsLocked();
            foreach (var message in history)
            {
                // CallId propagates so restored rows keep identity for a
                // later reconcile; rows without one fall back to exclusivity.
                // IsError propagates Pi's own persisted failure flag, so a
                // restored failure counts exactly like a live one (observed
                // evidence — nothing inferred, nothing invented).
                _entries.Add(new FeedEntry(_nextSeq++, message.Role, message.Text, CallId: message.CallId, IsError: message.IsError));
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
            // Feed replacement is a new evidence context: any in-flight
            // pairing from the previous context is dropped. Restored rows
            // keep their observed text/notes untouched; a later orphan end
            // becomes a separate row, never a rewrite of restored history.
            AbandonOpenToolsLocked();
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

            // The turn is over: no more ends can arrive for its calls. Drop
            // any orphan starts so a LATER run's ends can never pop them.
            // (Ends already processed paired before this ran; a genuinely
            // late end becomes a separate unverified row, outcome preserved.)
            AbandonOpenToolsLocked();

            if (receipt is not null)
            {
                // Three honest states: settled runs with unresolved
                // problems need review (amber); runs that never completed
                // fail (red); an empty settle is neutral and anything else
                // is healthy. Recovered tool errors stay visible as tool
                // rows and in the error count — the receipt just refuses
                // to bill them as a current failure.
                var needsReview = receipt.Aborted || receipt.UnresolvedErrorCount > 0;
                var failed = receipt.Aborted || !receipt.Settled;
                var note = needsReview ? "needs review" : !receipt.HasReply ? "no reply" : "healthy";
                _entries.Add(new FeedEntry(
                    _nextSeq++,
                    "receipt",
                    receipt.Display,
                    Note: note,
                    IsError: failed));
            }
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Close one live assistant row at a Pi message/turn boundary WITHOUT
    /// settling the run: the row stops streaming with its text kept, and
    /// later deltas open a fresh row, so narration keeps chronological
    /// order around tool operations. Pairing, activity, receipts and error
    /// accounting are untouched — unlike EndAssistantTurn(seq, null), which
    /// is the cancel path. Returns false when the row is unknown or
    /// already settled. Later text never rewrites this row.
    /// </summary>
    public bool EndAssistantSegment(long seq)
    {
        bool closed;
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            closed = index >= 0 && _entries[index].IsStreaming;
            if (closed)
            {
                _entries[index] = _entries[index] with { IsStreaming = false };
            }
        }

        if (closed)
        {
            Changed?.Invoke();
        }

        return closed;
    }

    /// <summary>
    /// Settle the run's final text row from the authoritative full run
    /// text: prefixes already preserved in closed segments are stripped
    /// (exact match, in order) so narration is never duplicated across
    /// rows. With no closed segments this is identical to
    /// ReconcileStreamingText. Unknown or settled rows are left alone.
    /// </summary>
    public void ReconcileFinalText(long seq, string fullText, IReadOnlyList<string> closedSegments)
    {
        ArgumentNullException.ThrowIfNull(fullText);
        ArgumentNullException.ThrowIfNull(closedSegments);
        var tail = fullText;
        foreach (var prior in closedSegments)
        {
            if (prior.Length == 0 || !tail.StartsWith(prior, StringComparison.Ordinal))
            {
                break;
            }

            tail = tail[prior.Length..];
        }

        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            if (index < 0 || !_entries[index].IsStreaming)
            {
                return;
            }

            _entries[index] = _entries[index] with { Text = tail };
        }

        Changed?.Invoke();
    }

    public long AddToolStart(string toolName, string? summary, string? path, bool isMutating, string? callId = null)
    {
        long seq;
        lock (_gate)
        {
            seq = _nextSeq++;
            _entries.Add(new FeedEntry(seq, "tool", summary ?? string.Empty,
                Note: $"started: {toolName}", Path: path, IsMutating: isMutating,
                CallId: string.IsNullOrEmpty(callId) ? null : callId));
            if (!string.IsNullOrEmpty(callId))
            {
                _openByCallId[callId] = seq;
            }
            else
            {
                if (!_openByName.TryGetValue(toolName, out var stack))
                {
                    stack = new Stack<long>();
                    _openByName[toolName] = stack;
                }

                stack.Push(seq);
            }
        }

        Changed?.Invoke();
        return seq;
    }

    /// <summary>
    /// Close one tool operation IN its start row — but ONLY with proven
    /// identity. Pairing order: (1) Pi's observed call ID against the open
    /// call-ID index (exact match, order-independent); (2) when the wire
    /// carried no ID, the per-name stack ONLY if exactly one start of that
    /// name is open (a single candidate is unambiguous). Anything else —
    /// unknown ID, several same-name opens with no ID, no open start —
    /// becomes a separate outcome row marked "(unverified)" so the outcome
    /// is preserved without attributing it to an unproven invocation.
    /// Never fabricates, never swaps concurrent same-name outcomes.
    /// </summary>
    public long AddToolEnd(string toolName, bool isError, string? callId = null)
    {
        long? openSeq = null;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(callId))
            {
                if (_openByCallId.Remove(callId, out var startSeq))
                {
                    openSeq = startSeq;
                }
            }
            else if (_openByName.TryGetValue(toolName, out var stack) && stack.Count == 1)
            {
                openSeq = stack.Pop();
            }
        }

        if (openSeq is { } seq)
        {
            CloseToolCall(seq, toolName, isError);
            return seq;
        }

        return AddUnverifiedEnd(toolName, isError);
    }

    private long AddUnverifiedEnd(string toolName, bool isError)
    {
        // No CallId on this row (its start was never proven), and the
        // " (unverified)" note suffix keeps its name out of reconcile name
        // groups — together they gate history reconciliation without any
        // separate bookkeeping (the write-only set we used to keep).
        var note = isError ? $"failed: {toolName} (unverified)" : $"finished: {toolName} (unverified)";
        var text = isError
            ? $"Tool {toolName} failed (outcome not paired to an observed start)."
            : $"Tool {toolName} finished (outcome not paired to an observed start).";
        long seq;
        lock (_gate)
        {
            seq = _nextSeq++;
            _entries.Add(new FeedEntry(seq, "tool", text, Note: note, IsError: isError));
        }

        Changed?.Invoke();
        return seq;
    }

    /// <summary>
    /// Drop all in-flight tool pairing state WITHOUT touching rows (session
    /// boundary: disconnect, workspace switch, feed replacement). Rows keep
    /// their observed text; later ends become separate unverified rows.
    /// Same-session ends (cancel, reconnect resume) keep pairing because
    /// their call IDs are still indexed — abandon only at real boundaries.
    /// </summary>
    public void AbandonOpenTools()
    {
        lock (_gate)
        {
            AbandonOpenToolsLocked();
        }
        // No Changed: rows are untouched (pairing indexes only), so no
        // view refresh is owed. The next real mutation drives the tick.
    }

    private void AbandonOpenToolsLocked()
    {
        _openByCallId.Clear();
        _openByName.Clear();
    }

    private void CloseToolCall(long seq, string toolName, bool isError)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Seq == seq);
            if (index < 0)
            {
                return;
            }

            var entry = _entries[index];
            var outcome = isError ? $"Tool {toolName} failed." : $"Tool {toolName} finished.";
            var text = string.IsNullOrEmpty(entry.Text) ? outcome : entry.Text + "\n" + outcome;
            _entries[index] = entry with
            {
                Text = text,
                Note = isError ? $"failed: {toolName}" : $"finished: {toolName}",
                IsError = isError,
            };
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Replace live tool placeholder texts with the full persisted bodies
    /// from post-run history — ONLY under a demonstrable identity contract
    /// (see ReconcileOutcome). Equal counts never suffice. Matching:
    /// (1) exact call-ID identity where BOTH sides carry the observed ID
    /// (wire and session both use toolCall.id); (2) among leftovers whose
    /// identity evidence is GENUINELY absent (no observed CallId on either
    /// side of the key), a (name, outcome) group with exactly one row and
    /// exactly one body — unique candidate, no positional assumption. A
    /// key holding ANY leftover with a known ID — conflicting, duplicated
    /// or merely unmatched — is blocked from fallback entirely: known
    /// identity is never silently overridden by a name match. Everything
    /// else — duplicate IDs, two same-name/same-outcome leftovers,
    /// unparseable bodies, note-less rows without an ID — cannot be
    /// proven; such rows keep their text and, if they differ from the
    /// leftover bodies (multiset comparison), one truthful marker records
    /// the refusal.
    /// </summary>
    public ReconcileOutcome ReconcileToolResults(IReadOnlyList<ConversationMessage> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var applied = false;
        var ambiguous = false;
        lock (_gate)
        {
            var feedRows = new List<(int Index, FeedEntry Row)>(16);
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Role == "tool")
                {
                    feedRows.Add((i, _entries[i]));
                }
            }

            var bodies = new List<(string Text, string? CallId)>(16);
            foreach (var message in history)
            {
                if (message.Role == "tool")
                {
                    bodies.Add((message.Text, message.CallId));
                }
            }

            if (feedRows.Count == 0)
            {
                return ReconcileOutcome.InSync;
            }

            if (feedRows.Count != bodies.Count)
            {
                return ReconcileOutcome.SkippedCountMismatch;
            }

            // A still-open start can never be proven to match a persisted
            // result (its end was never observed): refuse outright.
            foreach (var (_, row) in feedRows)
            {
                if (row.Note is not null && row.Note.StartsWith("started:", StringComparison.Ordinal))
                {
                    ambiguous = true;
                    MarkAmbiguousLocked();
                    goto Done; // Steps below would only fabricate pairings.
                }
            }

            // Step 1: exact call-ID identity. An ID is usable only when it
            // appears exactly once on EACH side — a duplicate on either side
            // makes that ID unprovable rather than guessable.
            var bodyIdCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, id) in bodies)
            {
                if (id is not null)
                {
                    bodyIdCounts[id] = bodyIdCounts.GetValueOrDefault(id) + 1;
                }
            }

            var feedIdCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, row) in feedRows)
            {
                if (row.CallId is not null)
                {
                    feedIdCounts[row.CallId] = feedIdCounts.GetValueOrDefault(row.CallId) + 1;
                }
            }

            var bodyIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var b = 0; b < bodies.Count; b++)
            {
                if (bodies[b].CallId is { } id && bodyIdCounts[id] == 1)
                {
                    bodyIndexById[id] = b;
                }
            }

            var bodyMatched = new bool[bodies.Count];
            var feedPaired = new bool[feedRows.Count];
            var replacements = new List<(int Index, string Text)>(4);

            for (var f = 0; f < feedRows.Count; f++)
            {
                var (entryIndex, row) = feedRows[f];
                if (row.CallId is not { } fid
                    || !feedIdCounts.ContainsKey(fid) || feedIdCounts[fid] != 1
                    || !bodyIndexById.TryGetValue(fid, out var b))
                {
                    continue;
                }

                // Identity matched: still require the body's own header to
                // agree with the row's lifecycle (name + error). Contradictory
                // evidence is never resolved by guessing.
                if (!ToolPairAgrees(row, bodies[b].Text))
                {
                    continue;
                }

                feedPaired[f] = true;
                bodyMatched[b] = true;
                if (row.Text != bodies[b].Text)
                {
                    replacements.Add((entryIndex, bodies[b].Text));
                }
            }

            // Step 2: exclusivity among leftovers — exactly one note-bearing
            // feed row and exactly one parseable body per (name, outcome)
            // group is a unique candidate, not a positional guess. Fallback
            // exists ONLY for genuinely missing identity: if ANY leftover
            // candidate in a (name, outcome) key carries an observed CallId
            // (conflicting, duplicated, or simply unmatched), known identity
            // would be silently ignored — the whole key is blocked from
            // fallback. Candidates with an observed ID are never fallback
            // material themselves. Un-note'd restored rows and unparseable
            // bodies cannot name a group at all.
            var blockedKeys = new HashSet<(string, bool)>();
            for (var f = 0; f < feedRows.Count; f++)
            {
                if (!feedPaired[f]
                    && feedRows[f].Row.CallId is not null
                    && ToolNoteName(feedRows[f].Row.Note) is { } blockedRowName)
                {
                    blockedKeys.Add((blockedRowName.ToLowerInvariant(), feedRows[f].Row.IsError));
                }
            }

            for (var b = 0; b < bodies.Count; b++)
            {
                if (!bodyMatched[b]
                    && bodies[b].CallId is not null
                    && ParseHistoryToolIdentity(bodies[b].Text) is { } blockedIdentity)
                {
                    blockedKeys.Add((blockedIdentity.Name.ToLowerInvariant(), blockedIdentity.IsError));
                }
            }

            var feedGroups = new Dictionary<(string, bool), List<int>>();
            for (var f = 0; f < feedRows.Count; f++)
            {
                if (feedPaired[f] || feedRows[f].Row.CallId is not null)
                {
                    continue; // Known ID: never fallback material.
                }

                if (ToolNoteName(feedRows[f].Row.Note) is { } name)
                {
                    var key = (name.ToLowerInvariant(), feedRows[f].Row.IsError);
                    if (!blockedKeys.Contains(key))
                    {
                        AddKey(feedGroups, key, f);
                    }
                }
            }

            var bodyGroups = new Dictionary<(string, bool), List<int>>();
            for (var b = 0; b < bodies.Count; b++)
            {
                if (bodyMatched[b] || bodies[b].CallId is not null)
                {
                    continue; // Known ID: never fallback material.
                }

                if (ParseHistoryToolIdentity(bodies[b].Text) is { } identity)
                {
                    var key = (identity.Name.ToLowerInvariant(), identity.IsError);
                    if (!blockedKeys.Contains(key))
                    {
                        AddKey(bodyGroups, key, b);
                    }
                }
            }

            foreach (var (key, feedList) in feedGroups)
            {
                if (feedList.Count != 1
                    || !bodyGroups.TryGetValue(key, out var bodyList)
                    || bodyList.Count != 1)
                {
                    continue;
                }

                var f = feedList[0];
                var b = bodyList[0];
                feedPaired[f] = true;
                bodyMatched[b] = true;
                if (feedRows[f].Row.Text != bodies[b].Text)
                {
                    replacements.Add((feedRows[f].Index, bodies[b].Text));
                }
            }

            // Leftovers: unprovable by both steps. If their texts already
            // match as a multiset, every possible mapping is a no-op and
            // rows keep correct text silently (restored rows are their own
            // history). Otherwise some row's text should change but no
            // mapping is provable → one truthful marker, no replacement.
            var leftoverFeed = new List<string>();
            var leftoverBodies = new List<string>();
            for (var f = 0; f < feedRows.Count; f++)
            {
                if (!feedPaired[f])
                {
                    leftoverFeed.Add(feedRows[f].Row.Text);
                }
            }

            for (var b = 0; b < bodies.Count; b++)
            {
                if (!bodyMatched[b])
                {
                    leftoverBodies.Add(bodies[b].Text);
                }
            }

            if (leftoverFeed.Count > 0)
            {
                leftoverFeed.Sort(StringComparer.Ordinal);
                leftoverBodies.Sort(StringComparer.Ordinal);
                if (!leftoverFeed.SequenceEqual(leftoverBodies, StringComparer.Ordinal))
                {
                    ambiguous = true;
                }
            }

            foreach (var (index, text) in replacements)
            {
                _entries[index] = _entries[index] with { Text = text };
                applied = true;
            }

            if (ambiguous)
            {
                MarkAmbiguousLocked();
            }

            Done: ;
        }

        if (applied || ambiguous)
        {
            Changed?.Invoke();
        }

        return ambiguous ? ReconcileOutcome.SkippedAmbiguous
            : applied ? ReconcileOutcome.Applied
            : ReconcileOutcome.InSync;
    }

    private static void AddKey(Dictionary<(string, bool), List<int>> groups, (string, bool) key, int index)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = new List<int>(2);
            groups[key] = list;
        }

        list.Add(index);
    }

    /// One pair agrees when the feed row's lifecycle note names the same
    /// tool as the history body's own header with the same error state.
    /// Used as a contradiction check after call-ID identity already matched:
    /// restored rows (no note) and "(unverified)" rows (suffixed name) can
    /// never agree by name, so they fall through to the leftover multiset
    /// check — which never rewrites text.
    private static bool ToolPairAgrees(FeedEntry row, string body)
    {
        var feedName = ToolNoteName(row.Note);
        var identity = ParseHistoryToolIdentity(body);
        if (feedName is null || identity is null)
        {
            return false;
        }

        return feedName.Equals(identity.Value.Name, StringComparison.OrdinalIgnoreCase)
            && row.IsError == identity.Value.IsError;
    }

    /// Identity of a persisted history tool body from its own header:
    /// "[tool result: NAME]" / "[tool NAME failed]" (FormatToolResult).
    /// "Ran ..." bash-execution bodies and anything else carry no provable
    /// name and return null. Public so the Overview timeline reads restored
    /// rows with the exact same parser the session map uses — one observed
    /// identity, never two interpretations.
    public static (string Name, bool IsError)? ParseHistoryToolIdentity(string text)
    {
        const string prefix = "[tool ";
        if (string.IsNullOrEmpty(text) || !text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var header = text;
        var newline = header.IndexOfAny(['\r', '\n']);
        if (newline >= 0)
        {
            header = header[..newline];
        }

        const string resultPrefix = "[tool result: ";
        if (header.StartsWith(resultPrefix, StringComparison.Ordinal) && header.EndsWith("]", StringComparison.Ordinal))
        {
            var name = header[resultPrefix.Length..^1].Trim();
            return name.Length == 0 ? null : (name, false);
        }

        const string failedSuffix = " failed]";
        if (header.EndsWith(failedSuffix, StringComparison.Ordinal))
        {
            var name = header[prefix.Length..^failedSuffix.Length].Trim();
            return name.Length == 0 ? null : (name, true);
        }

        return null;
    }

    private const string AmbiguousMarker =
        "Some tool results could not be proven to match their invocation — those rows keep their existing text; copy individual tool messages for the full text.";

    private void MarkAmbiguousLocked()
    {
        // One marker per ambiguous stretch: repeat post-run reconciles while
        // the rows stay ambiguous must not stack notices.
        if (_entries.Count > 0 && _entries[^1].Text == AmbiguousMarker)
        {
            return;
        }

        _entries.Add(new FeedEntry(_nextSeq++, "system", AmbiguousMarker, Note: "reconcile-skipped"));
    }

    public long AddError(string text)
        => Add(new FeedEntry(NextSeq(), "system", text, Note: "error", IsError: true));

    public long AddSystemNotice(string text)
        => Add(new FeedEntry(NextSeq(), "system", text));

    /// Cancellation notice: a distinct note so attention can tell "the
    /// latest run was cancelled" apart from "the latest settled run
    /// failed" — the cancelled run earned no health signal either way,
    /// and a stale receipt must never speak for it.
    public long AddCancelNotice(string text)
        => Add(new FeedEntry(NextSeq(), "system", text, Note: "cancelled"));

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
                case "tool" when entry.Note is not null && ToolNoteName(entry.Note) is { } toolName:
                    milestones.Add(new FeedMilestone(
                        entry.IsError ? "error" : "tool",
                        $"Ran {toolName}",
                        entry.Text.Length == 0 ? null : FirstLine(entry.Text, 200),
                        entry.Seq));
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

    /// <summary>
    /// Tool name from a lifecycle note ("started: X", "finished: X",
    /// "failed: X"); null for anything else. Single parser for the note
    /// shapes AddToolStart/AddToolEnd produce.
    /// </summary>
    internal static string? ToolNoteName(string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return null;
        }

        foreach (var prefix in new[] { "started:", "finished:", "failed:" })
        {
            if (note.StartsWith(prefix, StringComparison.Ordinal))
            {
                var name = note[prefix.Length..].Trim();
                return name.Length == 0 ? null : name;
            }
        }

        return null;
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
