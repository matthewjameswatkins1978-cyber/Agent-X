using AgentX.Core;
using Avalonia;
using ShellCards = AgentX.App.ContextCards;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace AgentX.App;

/// <summary>
/// Approved-shell behaviour (Checkpoint A): identity header actions, worker
/// cards over the hidden tab-strip selection model, compact footer binding,
/// and feed find. The cards never own selection truth: clicks drive
/// WorkspaceTabs.SelectedItem through the existing guarded paths, and the
/// hidden strip stays the single model (flagged tech debt for Lucy).
/// </summary>
public partial class MainWindow
{
    private readonly Dictionary<string, string?> _gitBranchCache = new(StringComparer.Ordinal);
    private CancellationTokenSource? _gitBranchCts;

    /// One-time shell wiring after the handle exists.
    private void InitApprovedShell()
    {
        BrandIcon.Stroke = AxTheme.GreenBrush;
        // StatusText keeps every existing writer: reparent the live control
        // into the transient slot and show the slot only while it says
        // something. No call site changes.
        if (StatusText.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(StatusText);
        }

        FooterTransient.Children.Add(StatusText);
        SyncTransientVisibility();
        StatusText.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                SyncTransientVisibility();
            }
        };
        SyncRailSelection();
        RebuildContextCards();
        RefreshCompactFooter();
    }

    // Expandable notice (Lucy Checkpoint A): normal operation shows exactly
    // one footer line. A message lights the toggle; the text opens only on
    // demand and never persists as a second strip. No severity guessing.
    private bool _noticeOpen;

    private void SyncTransientVisibility()
    {
        var hasNotice = !string.IsNullOrEmpty(StatusText.Text);
        NoticeToggle.IsVisible = hasNotice;
        FooterTransient.IsVisible = hasNotice && _noticeOpen;
        NoticeToggle.SetValue(AutomationProperties.NameProperty, _noticeOpen ? "Hide notice" : "Show notice");
        ToolTip.SetTip(NoticeToggle, _noticeOpen ? "Hide notice" : "Show notice");
    }

    private void NoticeToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        _noticeOpen = !_noticeOpen;
        SyncTransientVisibility();
    }

    // --- Worker cards ----------------------------------------------------------

    /// Rebuild the card strip from the tab-strip model (workers then legacy
    /// entries, in tab order) plus the add-context button. Called from
    /// UpdateTabDots, which already fans out to every selection, busy and
    /// connection change.
    private void RebuildContextCards()
    {
        ContextCards.Children.Clear();
        var selectedKey = SelectedWorker?.Id ?? SelectedEntry?.Key;
        foreach (var item in WorkspaceTabs.Items)
        {
            if (item is not TabStripItem tab)
            {
                continue;
            }

            if (tab.Tag is PiWorker worker)
            {
                AddWorkerCard(worker, worker.Id == selectedKey);
            }
            else if (tab.Tag is CatalogEntry entry)
            {
                AddWorkspaceCard(entry, entry.Key == selectedKey);
            }
        }

        var add = new Button
        {
            Content = new AxIcon { Kind = AxIconKind.Add, Width = 14, Height = 14 },
            Background = Brushes.Transparent,
            BorderBrush = AxTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Stretch,
            };
        add.SetValue(ToolTip.TipProperty, "Add project");
        add.SetValue(AutomationProperties.NameProperty, "Add project");
        add.Click += (_, _) => AddWorkspace_OnClick(add, new RoutedEventArgs());
        ContextCards.Children.Add(add);
    }

    private void AddWorkerCard(PiWorker worker, bool selected)
    {
        var busy = _busyWorkers.Contains(worker.Id);
        var connecting = _connectingWorkers.Contains(worker.Id);
        var detail = busy
            ? "run in progress"
            : FolderShortName(worker.ProjectRoot, worker.Label);
        var data = ShellCards.WorkerCard(
            worker.Id,
            WorkerViewKey.DisplayLabel(worker.Id, worker.Label),
            detail,
            worker.IsConnected,
            busy,
            connecting,
            selected);
        AddCardButton(data, WorkerDotFill(worker.IsConnected, busy), () => SelectContextCard(worker.Id));
    }

    private void AddWorkspaceCard(CatalogEntry entry, bool selected)
    {
        var connected = entry.Key == _connectedKey && _conversation?.IsConnected == true;
        var busy = connected && _piBusy;
        string root;
        try
        {
            root = entry.ToWorkspace(ManagedRoot).ProjectRoot;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            root = string.Empty;
        }

        var data = ShellCards.WorkspaceCard(
            entry.Key,
            AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label),
            FolderShortName(root, entry.Label),
            connected,
            busy,
            _connecting && !connected,
            selected);
        AddCardButton(data, WorkerDotFill(connected, busy), () => SelectContextCard(entry.Key));
    }

    private static string FolderShortName(string root, string fallback)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return fallback;
        }

        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? fallback : name;
    }

    private void AddCardButton(ContextCardData data, IBrush dotFill, Action onClick)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = dotFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var name = new TextBlock
        {
            Text = data.Name,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        head.Children.Add(dot);
        head.Children.Add(name);
        var status = new TextBlock
        {
            Text = data.StatusLine,
            Foreground = AxTheme.MutedBrush,
            FontSize = 12,
        };
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(head);
        body.Children.Add(status);
        var button = new Button
        {
            Content = body,
            Background = data.IsSelected ? AxTheme.Surface2Brush : Brushes.Transparent,
            BorderBrush = data.IsSelected ? AxTheme.BorderStrongBrush : AxTheme.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8),
            MinWidth = 200,
        };
        button.SetValue(AutomationProperties.NameProperty, $"Select {data.Name}");
        button.Click += (_, _) => onClick();
        ContextCards.Children.Add(button);
    }

    /// Drive the hidden tab strip: the existing SelectionChanged path owns
    /// the switch (coordinator guards, view save/restore, feed rebind).
    private void SelectContextCard(string key)
    {
        foreach (var item in WorkspaceTabs.Items)
        {
            if (item is TabStripItem tab
                && ((tab.Tag is PiWorker w && w.Id == key)
                    || (tab.Tag is CatalogEntry e && e.Key == key)))
            {
                WorkspaceTabs.SelectedItem = tab;
                return;
            }
        }
    }

    // --- Compact footer ----------------------------------------------------------

    /// Rebind the one-line footer from viewed-context truth. Called from
    /// UpdateTabDots (selection/busy/connection fan-out) and engine refresh;
    /// the git branch resolves async and refreshes again on arrival.
    private void RefreshCompactFooter()
    {
        string name;
        string? root;
        string? model;
        string? thinking;
        double? context = null;
        if (SelectedWorker is { } worker)
        {
            name = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
            root = worker.ProjectRoot;
            var engine = ActiveEngine();
            model = engine.Model;
            thinking = engine.Thinking;
            context = worker.LatestStats?.ContextPercent;
        }
        else if (SelectedEntry is { } entry)
        {
            name = AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label);
            root = null;
            try
            {
                root = entry.ToWorkspace(ManagedRoot).ProjectRoot;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                root = null;
            }

            var engine = ActiveEngine();
            model = engine.Model;
            thinking = engine.Thinking;
            context = _bridge?.LatestStats?.ContextPercent;
        }
        else
        {
            name = "Main";
            root = null;
            model = null;
            thinking = null;
        }

        string? branch = null;
        if (!string.IsNullOrWhiteSpace(root))
        {
            if (!_gitBranchCache.TryGetValue(root, out branch))
            {
                RefreshGitBranchAsync(root);
            }
        }

        var (left, right) = ShellCards.FooterText(name, branch, model, thinking, context);
        FooterContextText.Text = left;
        FooterEngineText.Text = right;
    }

    private void RefreshGitBranchAsync(string root)
    {
        _gitBranchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitBranchCts = cts;
        _ = Task.Run(async () =>
        {
            string? branch = null;
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "git", $"-C \"{root}\" rev-parse --abbrev-ref HEAD")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is not null)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token);
                    var output = await process.StandardOutput.ReadToEndAsync(linked.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                    if (process.ExitCode == 0)
                    {
                        branch = ShellCards.ParseGitBranch(output);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
            {
                branch = null;
            }

            var token = cts.Token;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                _gitBranchCache[root] = branch;
                RefreshCompactFooter();
            });
        }, cts.Token);
    }

    // --- Header actions + find -----------------------------------------------------

    private void SearchButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_page != AppPage.Live)
        {
            ShowPage("Live");
        }

        FindBar.IsVisible = !FindBar.IsVisible;
        if (FindBar.IsVisible)
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }
    }

    private void FindClose_OnClick(object? sender, RoutedEventArgs e) => FindBar.IsVisible = false;

    private void FindNext_OnClick(object? sender, RoutedEventArgs e) => FindStep(1);

    private void FindPrev_OnClick(object? sender, RoutedEventArgs e) => FindStep(-1);

    private void FindBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            FindStep(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FindBar.IsVisible = false;
            e.Handled = true;
        }
    }

    /// Step selection through feed rows whose display text contains the
    /// query, wrapping around. No match leaves selection untouched.
    private void FindStep(int direction)
    {
        var query = FindBox.Text;
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        var items = ConversationList.Items.OfType<ConversationViewItem>().ToList();
        if (items.Count == 0)
        {
            return;
        }

        var selected = ConversationList.SelectedIndex;
        for (var step = 1; step <= items.Count; step++)
        {
            var at = ((selected + (direction * step)) % items.Count + items.Count) % items.Count;
            if (items[at].DisplayText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            {
                ConversationList.SelectedIndex = at;
                ConversationList.ScrollIntoView(items[at]);
                return;
            }
        }
    }
}
