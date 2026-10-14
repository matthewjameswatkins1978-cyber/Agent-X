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

    public string FullText
    {
        get => _fullText;
        set
        {
            if (_fullText != value)
            {
                _fullText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullText)));
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
