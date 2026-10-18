using System.ComponentModel;
using AgentX.Core;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>Visual block kind for one feed row (Pi visual grammar).</summary>
public enum FeedBlockKind
{
    User,
    Assistant,
    Tool,
    System,
    Receipt,
}

/// <summary>Studio mission state for the card indicator. Read from the
/// tool result's own markers (Studio schema STATUSES), never inferred
/// from a successful invocation.</summary>
public enum StudioState
{
    Working,
    Complete,
    NeedsHuman,
    Blocked,
    Failed,
}

/// <summary>
/// One bound row in the conversation feed. <see cref="FullText"/> is the
/// complete message (copy source, always byte-exact); <see cref="DisplayText"/>
/// is the bounded view with an explicit truncation notice. <see cref="Markdown"/>
/// carries the parsed Markdown model for rich rendering (assistant narrative
/// and user packets; null while streaming or when empty); <see cref="IsExpanded"/> reveals the full text on explicit
/// user action for long tool output. Brushes follow <see cref="PiPalette"/>.
/// </summary>
public sealed class ConversationViewItem : INotifyPropertyChanged
{
    private string _displayText;
    private string _fullText;
    private MdDocument? _markdown;
    private bool _isExpanded;
    private string _header;
    private string _toolName = "tool";
    private string _toolSuffix = string.Empty;
    private readonly int _displayCap;

    public ConversationViewItem(
        long seq,
        string role,
        string header,
        string displayText,
        string fullText,
        FeedBlockKind kind,
        bool isStreaming,
        IBrush stateBrush,
        int displayCap = ViewCaps.History)
    {
        Seq = seq;
        Role = role;
        _header = header;
        _displayText = displayText;
        _fullText = fullText;
        Kind = kind;
        IsStreaming = isStreaming;
        StateBrush = stateBrush;
        _displayCap = displayCap;
        RoleBrush = RoleBrushes.GetValueOrDefault(role, RoleBrushes["system"]);
        IsCompaction = role == "system" && fullText.StartsWith("[context summary]", StringComparison.Ordinal);
        var (toolName, toolSuffix) = ToolDisplay(header, seq);
        _toolName = toolName;
        _toolSuffix = toolSuffix;
        // Neighbours unknown yet: detail rows start tight, everything else
        // full. The sync pass applies GroupMargins once the row lands.
        RowMargin = IsDetailRow
            ? new Avalonia.Thickness(0, 1, 0, 1)
            : new Avalonia.Thickness(0, 4, 0, 4);
        RefreshExpansion();
    }

    public long Seq { get; }

    public string Role { get; }

    public string Header
    {
        get => _header;
        set
        {
            if (_header != value)
            {
                _header = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
            }
        }
    }

    public FeedBlockKind Kind { get; }

    /// True for the persisted-compaction system row (Pi's purple box).
    public bool IsCompaction { get; }

    /// Pi-style tool header: bold white name ("read").
    public string ToolName
    {
        get => _toolName;
        private set
        {
            if (_toolName != value)
            {
                _toolName = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolName)));
            }
        }
    }

    /// True for bridge detail rows ("[detail …]", "[result …]",
    /// "[progress …]"): evidence annotations that read as attachments to
    /// their tool operation, rendered compact — never as cards. Receipts,
    /// compactions and plain notices stay full rows.
    public bool IsDetailRow => Role == "system" && IsDetailPrefix(FullText);

    /// View-layer density rule (presentation only — the feed keeps every
    /// row): transport detail rows must not read as ordinary chat lines,
    /// so normal conversation folds them away. Full Trace reveals them;
    /// tool rows keep their own expanders and the tap retains everything.
    internal static bool ShouldShow(FeedEntry entry, bool showDiagnostics)
        => showDiagnostics || !(entry.Role == "system" && IsDetailPrefix(entry.Text));

    private Avalonia.Thickness _rowMargin = new(0, 4, 0, 4);

    /// Row rhythm, set by the host from neighbour roles (see
    /// <see cref="GroupMargins"/>): turn anchors keep the full 4px
    /// rhythm, rows inside one run join tight. Detail rows stay tight
    /// always. Assigned, never guessed — no negative margins anywhere.
    public Avalonia.Thickness RowMargin
    {
        get => _rowMargin;
        set
        {
            if (_rowMargin != value)
            {
                _rowMargin = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowMargin)));
            }
        }
    }

    /// Group-aware rhythm from neighbour roles. Turn anchors (user
    /// prompts, receipts) always keep full margins; detail rows always
    /// join tight; every other row joins whatever came before unless a
    /// turn boundary intervenes. Pure so the rhythm is tested, not
    /// eyeballed.
    internal static Avalonia.Thickness GroupMargins(string? prevRole, FeedEntry entry, string? nextRole)
    {
        if (entry.Role is "tool" or "system" && IsDetailPrefix(entry.Text))
        {
            return new Avalonia.Thickness(0, 1, 0, 1);
        }

        if (entry.Role is "user" or "receipt")
        {
            return new Avalonia.Thickness(0, 4, 0, 4);
        }

        var top = prevRole is null or "user" or "receipt" ? 4 : 1;
        var bottom = nextRole is null or "user" or "receipt" ? 4 : 1;
        return new Avalonia.Thickness(0, top, 0, bottom);
    }

    internal static bool IsDetailPrefix(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return text.StartsWith("[detail ", StringComparison.Ordinal)
            || text.StartsWith("[result ", StringComparison.Ordinal)
            || text.StartsWith("[progress ", StringComparison.Ordinal);
    }

    public bool IsStreaming { get; }

    /// Pi-style tool header suffix: dim state + evidence seq ("· finished #4").
    /// The state word keeps failure/success readable without colour.
    public string ToolSuffix
    {
        get => _toolSuffix;
        private set
        {
            if (_toolSuffix != value)
            {
                _toolSuffix = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolSuffix)));
            }
        }
    }

    /// Recompute the Pi-style tool display from the lifecycle header.
    /// Called with Header whenever a row settles (start → finished/failed).
    public void RefreshToolDisplay()
    {
        var (toolName, toolSuffix) = ToolDisplay(Header, Seq);
        ToolName = toolName;
        ToolSuffix = toolSuffix;
    }

    /// Split a lifecycle header ("[Tool #4] finished: read") into the Pi
    /// header pair ("read", "· finished #4"). The state word is kept so
    /// success/failure never depends on colour alone; the seq keeps the
    /// evidence link. Unknown shapes fall back to the raw header text.
    internal static (string Name, string Suffix) ToolDisplay(string? header, long seq)
    {
        const string tag = "(unverified)";
        if (!string.IsNullOrEmpty(header))
        {
            var colon = header.IndexOf(':');
            if (colon > 0)
            {
                var state = header[..colon].Trim();
                var open = state.LastIndexOf(' ');
                state = open >= 0 ? state[(open + 1)..].Trim() : state;
                var rest = header[(colon + 1)..].Trim();
                var unverified = false;
                if (rest.EndsWith(tag, StringComparison.Ordinal))
                {
                    unverified = true;
                    rest = rest[..^tag.Length].Trim();
                }

                if (state.Length > 0 && rest.Length > 0)
                {
                    var suffix = unverified
                        ? $"· {state} (unverified) #{seq}"
                        : $"· {state} #{seq}";
                    return (rest, suffix);
                }
            }
        }

        return (header ?? "tool", $"#{seq}");
    }

    /// Parse a history-restored tool row: Pi persists the tool name and
    /// outcome only inside the body's first line ("[tool result: NAME]" /
    /// "[tool NAME failed]", see PiConversation.FormatToolResult), with no
    /// Note and no error flag. Live finished/failed rows repeat the same
    /// prefix under an already-good header, so they strip too. Returns the header note ("finished: NAME" /
    /// "failed: NAME"), whether it failed, and the body with that redundant
    /// first line removed for display. Started/pending rows and unknown
    /// shapes return null and render exactly as before. Never invents:
    /// empty or bracketed names refuse. Copy always uses FullText, and the
    /// expanded view keeps the original line, so nothing is lost.
    internal static (string Note, bool IsError, string DisplayBody)? HistoryToolPresentation(string? note, string text)
    {
        // History rows carry no Note; live finished/failed rows carry their
        // own ("finished: NAME") and are already well-headed, but their
        // body repeats the same Core prefix — strip it too. Started/pending
        // rows and unknown shapes refuse (null) and render exactly as before.
        if (!string.IsNullOrEmpty(note)
            && !note.StartsWith("finished:", StringComparison.Ordinal)
            && !note.StartsWith("failed:", StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var cut = text.IndexOf((char)10);
        var firstLine = (cut < 0 ? text : text[..cut]).TrimEnd('\r');
        var rest = cut < 0 ? string.Empty : text[(cut + 1)..];

        const string resultPrefix = "[tool result: ";
        if (firstLine.StartsWith(resultPrefix, StringComparison.Ordinal) && firstLine.EndsWith("]", StringComparison.Ordinal))
        {
            var name = firstLine[resultPrefix.Length..^1].Trim();
            if (name.Length == 0 || name.IndexOfAny(['[', ']']) >= 0)
            {
                return null;
            }

            return ($"finished: {name}", false, rest);
        }

        const string failPrefix = "[tool ";
        const string failSuffix = " failed]";
        if (firstLine.StartsWith(failPrefix, StringComparison.Ordinal) && firstLine.EndsWith(failSuffix, StringComparison.Ordinal))
        {
            var name = firstLine[failPrefix.Length..^failSuffix.Length].Trim();
            if (name.Length == 0 || name.IndexOfAny(['[', ']']) >= 0)
            {
                return null;
            }

            return ($"failed: {name}", true, rest);
        }

        return null;
    }

    public IBrush StateBrush
    {
        get;
        set
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateBrush)));
            }
        }
    }

    /// 3px state edge for the B7 tool/user surfaces. Set by the host
    /// alongside <see cref="StateBrush"/> (shared AxTheme instances, so
    /// treatment switches propagate to live rows).
    public IBrush EdgeBrush
    {
        get;
        set
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EdgeBrush)));
            }
        }
    } = AxTheme.UserEdgeBrush;

    public bool IsUser => Kind == FeedBlockKind.User;

    public bool IsAssistant => Kind == FeedBlockKind.Assistant;

    public bool IsTool => Kind == FeedBlockKind.Tool;

    public bool IsSystemRow => Kind is FeedBlockKind.System or FeedBlockKind.Receipt;

    /// Plain dim rows: system/bridge/receipt rows that are not compaction.
    public bool IsPlainSystemRow => IsSystemRow && !IsCompaction;

    private bool _isFinalReport;

    /// The run's settled closing word: an assistant row immediately
    /// followed by a receipt. Position only, never content — restored
    /// history carries no receipts and cancelled turns end in notices,
    /// so neither outlines. Assigned by the host during sync.
    public bool IsFinalReport
    {
        get => _isFinalReport;
        set
        {
            if (_isFinalReport != value)
            {
                _isFinalReport = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFinalReport)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlainAssistant)));
            }
        }
    }

    /// Assistant rows that are not the final report (the ordinary case).
    public bool IsPlainAssistant => IsAssistant && !IsFinalReport;

    /// Position test for the final-report outline: the visible entry at
    /// index is an assistant row closed by a receipt in the same cluster
    /// (a later user prompt ends the cluster first). Live turns stream
    /// into the row opened BEFORE the tools run, so adjacency is not the
    /// test — cluster membership is. Restored history carries no receipts
    /// and cancelled turns end in notices, so neither outlines.
    internal static bool ClosesReport(IReadOnlyList<FeedEntry> visible, int index, bool showDiagnostics)
    {
        if (index < 0 || index >= visible.Count || visible[index].Role != "assistant")
        {
            return false;
        }

        for (var i = index + 1; i < visible.Count; i++)
        {
            if (!ShouldShow(visible[i], showDiagnostics))
            {
                continue;
            }

            if (visible[i].Role == "receipt")
            {
                return true;
            }

            if (visible[i].Role == "user")
            {
                return false;
            }
        }

        return false;
    }

    /// Streaming caret: a subtle block cursor on the live assistant row so
    /// readers see genuine Pi deltas landing. View-only — FullText (the
    /// copy source) never carries it, and it vanishes on settle.
    internal static string StreamingDisplay(string displayText, bool isStreaming)
        => isStreaming ? displayText + " ▍" : displayText;

    private bool _flowMode;

    /// Flow Mode presentation for this row. Assigned by the host at build;
    /// toggling rebuilds the list, so rows never mix modes.
    public bool FlowMode
    {
        get => _flowMode;
        set
        {
            if (_flowMode != value)
            {
                _flowMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowMode)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFlowTool)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCardTool)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFlowExpanded)));
            }
        }
    }

    /// Slim Flow tool line (instead of the full card; Studio rows keep
    /// their charcoal card in both modes).
    public bool IsFlowTool => IsTool && FlowMode && !IsStudioTool;

    /// Full tool card (classic presentation; never a Studio row).
    public bool IsCardTool => IsTool && !FlowMode && !IsStudioTool;

    private bool _isStudioTool;

    /// Studio mission row: charcoal card with a truthful state indicator.
    /// Suppresses both the Flow line and the classic card.
    public bool IsStudioTool
    {
        get => _isStudioTool;
        set
        {
            if (_isStudioTool != value)
            {
                _isStudioTool = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStudioTool)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFlowTool)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCardTool)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStudioExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStudioSummary)));
            }
        }
    }

    /// Human-readable Studio state for the card header.
    public string StudioStateText
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StudioStateText)));
            }
        }
    } = "Working";

    /// One-line evidence summary for the Studio card header.
    public string StudioSummary
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StudioSummary)));
            }
        }
    } = string.Empty;

    /// State edge + dot for the Studio card. Assigned by the host.
    public IBrush StudioEdge
    {
        get;
        set
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StudioEdge)));
            }
        }
    } = AxTheme.CyanBrush;

    /// Expanded Studio evidence (the full original receipt).
    public bool IsStudioExpanded => IsStudioTool && IsExpanded;

    /// Collapsed Studio summary line.
    public bool IsStudioSummary => IsStudioTool && !IsExpanded;

    /// Studio receipt identity + state from authoritative markers only:
    /// RESULT lines, bracketed mission states and quoted status fields
    /// (Studio schema STATUSES). Prose that merely mentions these words
    /// does not count — an unmarked successful call is Working (the
    /// mission continues), never Complete. Null unless Studio's tool.
    internal static StudioFact? StudioOutcome(string toolName, bool isError, string text)
    {
        if (string.IsNullOrEmpty(toolName)
            || !toolName.StartsWith("studio_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (isError)
        {
            return new StudioFact(StudioState.Failed, StudioSummaryOf(text));
        }

        var state = StateOfResult(text) ?? StateOfBrackets(text) ?? StateOfStatusField(text);
        return new StudioFact(state ?? StudioState.Working, StudioSummaryOf(text));
    }

    /// A Studio receipt: parsed mission state plus the summary line.
    public sealed record StudioFact(StudioState State, string Summary);

    internal static string DescribeStudioState(StudioState state) => state switch
    {
        StudioState.Complete => "Complete",
        StudioState.NeedsHuman => "Needs human",
        StudioState.Blocked => "Blocked",
        StudioState.Failed => "Failed",
        _ => "Working",
    };

    internal static IBrush StudioEdgeFor(StudioState state) => state switch
    {
        StudioState.Complete => AxTheme.GreenBrush,
        StudioState.NeedsHuman => AxTheme.YellowBrush,
        StudioState.Blocked => AxTheme.BlueBrush,
        StudioState.Failed => AxTheme.RedBrush,
        _ => AxTheme.CyanBrush,
    };

    internal static string StudioSummaryOf(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0)
            {
                return line.Length <= 120 ? line : line[..120].TrimEnd() + "…";
            }
        }

        return "no output recorded";
    }

    private static StudioState? StateOfToken(string token) => token switch
    {
        "COMPLETE" => StudioState.Complete,
        "PARTIAL" => StudioState.NeedsHuman,
        "FAILED" => StudioState.Failed,
        "NEEDS_HUMAN" => StudioState.NeedsHuman,
        "WAITING_REVIEW" => StudioState.Blocked,
        "WORKING" => StudioState.Working,
        _ => null,
    };

    private static StudioState? StateOfResult(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("RESULT:", StringComparison.Ordinal))
            {
                continue;
            }

            var token = line["RESULT:".Length..].Trim()
                .Split([' ', ',', ';', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (token is not null && StateOfToken(token) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    private static StudioState? StateOfBrackets(string text)
    {
        var at = 0;
        while (at < text.Length)
        {
            var open = text.IndexOf('[', at);
            if (open < 0)
            {
                return null;
            }

            var close = text.IndexOf(']', open + 1);
            if (close < 0)
            {
                return null;
            }

            if (StateOfToken(text[(open + 1)..close].Trim()) is { } state)
            {
                return state;
            }

            at = close + 1;
        }

        return null;
    }

    private static StudioState? StateOfStatusField(string text)
    {
        var key = text.IndexOf("\"status\"", StringComparison.Ordinal);
        while (key >= 0)
        {
            var colon = text.IndexOf(':', key + 8);
            if (colon < 0)
            {
                return null;
            }

            var quote = text.IndexOf('"', colon + 1);
            if (quote < 0)
            {
                return null;
            }

            var end = text.IndexOf('"', quote + 1);
            if (end < 0)
            {
                return null;
            }

            if (StateOfToken(text[(quote + 1)..end]) is { } state)
            {
                return state;
            }

            key = text.IndexOf("\"status\"", end + 1, StringComparison.Ordinal);
        }

        return null;
    }

    /// Expanded Flow detail under the slim line.
    public bool IsFlowExpanded => IsFlowTool && IsExpanded;

    /// Status dot for the Flow line. Assigned by the host with the row.
    public IBrush FlowDot
    {
        get;
        set
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDot)));
            }
        }
    } = AxTheme.GreenBrush;

    public string FullText
    {
        get => _fullText;
        set
        {
            if (_fullText != value)
            {
                _fullText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullText)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDetailRow)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowMargin)));
                RefreshExpansion();
            }
        }
    }

    public MdDocument? Markdown
    {
        get => _markdown;
        set
        {
            if (!ReferenceEquals(_markdown, value))
            {
                _markdown = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Markdown)));
            }
        }
    }

    /// <summary>True when the full text exceeds the display cap (expandable).</summary>
    public bool CanExpand { get; private set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFlowExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStudioExpanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsStudioSummary)));
            }
        }
    }

    public string ExpandLabel => IsExpanded
        ? "Show less"
        : $"Show full output ({FullText.Length:N0} chars)";

    public IBrush RoleBrush { get; }

    public bool IsMono => Role is "tool" or "receipt" or "system";

    public string DisplayText
    {
        get => _displayText;
        set
        {
            if (_displayText != value)
            {
                _displayText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RefreshExpansion()
    {
        // Only tool rows expand: assistant narrative renders in full (parsed
        // once on settle), and user/system rows are short by construction.
        var canExpand = Kind == FeedBlockKind.Tool && _fullText.Length > _displayCap;
        if (canExpand != CanExpand)
        {
            CanExpand = canExpand;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanExpand)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandLabel)));
        }
        else
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandLabel)));
        }
    }

    private static readonly Dictionary<string, IBrush> RoleBrushes = new()
    {
        ["user"] = new SolidColorBrush(Color.Parse(PiPalette.Blue)),
        ["assistant"] = new SolidColorBrush(Color.Parse(PiPalette.Text)),
        ["tool"] = new SolidColorBrush(Color.Parse(PiPalette.Green)),
        ["system"] = new SolidColorBrush(Color.Parse(PiPalette.Muted)),
        ["receipt"] = new SolidColorBrush(Color.Parse(PiPalette.Yellow)),
    };
}

/// <summary>Display caps shared by the feed view (bounded view, full copy).</summary>
public static class ViewCaps
{
    public const int History = 2000;
    public const int Live = 32_000;
}
