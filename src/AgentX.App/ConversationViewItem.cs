using System.ComponentModel;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// One bound row in the conversation feed. <see cref="FullText"/> is the
/// complete message (copy source); <see cref="DisplayText"/> is the bounded
/// view with an explicit truncation notice. Role colors mirror Pi's terminal
/// presentation: you, Pi, tools, system/receipts each read distinctly.
/// </summary>
public sealed class ConversationViewItem : INotifyPropertyChanged
{
    private string _displayText;
    private string _fullText;

    public ConversationViewItem(long seq, string role, string header, string displayText, string fullText)
    {
        Seq = seq;
        Role = role;
        Header = header;
        _displayText = displayText;
        _fullText = fullText;
        RoleBrush = RoleBrushes.GetValueOrDefault(role, RoleBrushes["system"]);
    }

    public long Seq { get; }

    public string Role { get; }

    public string Header { get; }

    public string FullText
    {
        get => _fullText;
        set
        {
            if (_fullText != value)
            {
                _fullText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FullText)));
            }
        }
    }

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

    private static readonly Dictionary<string, IBrush> RoleBrushes = new()
    {
        ["user"] = new SolidColorBrush(Color.Parse("#8AB4FF")),
        ["assistant"] = new SolidColorBrush(Color.Parse("#E6E9EF")),
        ["tool"] = new SolidColorBrush(Color.Parse("#7DD3A8")),
        ["system"] = new SolidColorBrush(Color.Parse("#AAB2BF")),
        ["receipt"] = new SolidColorBrush(Color.Parse("#E5C07B")),
    };
}
