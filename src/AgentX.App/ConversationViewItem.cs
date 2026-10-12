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
/// carries the parsed assistant model for rich rendering (null while
/// streaming); <see cref="IsExpanded"/> reveals the full text on explicit
/// user action for long tool output. Brushes follow <see cref="PiPalette"/>.
/// </summary>
public sealed class ConversationViewItem : INotifyPropertyChanged
{
    private string _displayText;
    private string _fullText;
    private MdDocument? _markdown;
    private bool _isExpanded;
    private string _header;
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

    public bool IsStreaming { get; }

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

    public bool IsUser => Kind == FeedBlockKind.User;

    public bool IsAssistant => Kind == FeedBlockKind.Assistant;

    public bool IsTool => Kind == FeedBlockKind.Tool;

    public bool IsSystemRow => Kind is FeedBlockKind.System or FeedBlockKind.Receipt;

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
