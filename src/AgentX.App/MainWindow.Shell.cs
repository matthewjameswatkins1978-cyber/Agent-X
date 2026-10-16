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
    private readonly Dictionary<string, (string? Branch, DateTime CheckedUtc)> _gitBranchCache = new(StringComparer.Ordinal);
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
        CardScroller.GetObservable(Visual.BoundsProperty).Subscribe(new CardWidthWatcher(this));
        RebuildContextCards();
        RefreshCompactFooter();
    }

    // Expandable notice (Lucy corrective pass): normal operation shows exactly
    // one footer line. Routine information never lights the toggle; genuine
    // warning/error language (ShellCards.IsAlert, tested both ways) ambers it
    // and opens the text on arrival. Writers are untouched; the full text is
    // always one click away.
    // Notice lifecycle lives in ContextCards.NoticeState (tested): alerts
    // auto-open, routine collapses unless manually opened, writers untouched.
    // Keyword severity is temporary; typed severity is a future improvement.
    private ContextCards.NoticeState _notice = new(false, false, null);

    private void SyncTransientVisibility()
    {
        _notice = ShellCards.NextNotice(_notice, StatusText.Text);
        var hasNotice = !string.IsNullOrEmpty(StatusText.Text);
        // Amber is reserved for genuine alerts (the old toggle's language:
        // yellow only for alert keywords, muted otherwise) — routine
        // status text must never paint the gauge as a warning.
        FooterGauge.HasNotice = hasNotice && ShellCards.IsAlert(StatusText.Text);
        FooterTransient.IsVisible = hasNotice && _notice.Open;
        RefreshGaugeTip();
    }

    /// Gauge tooltip carries both halves of the truth: the measured
    /// context (or its honest absence) and the notice affordance.
    private void RefreshGaugeTip()
    {
        var context = FooterGauge.Percent is { } p ? $"Context {p:F1}%" : "Context unknown";
        var notice = _notice.Open ? "Hide notice" : string.IsNullOrEmpty(StatusText.Text) ? "No notices" : "Show notice";
        ToolTip.SetTip(ContextGaugeButton, $"{context} · {notice} (activate to toggle)");
        ContextGaugeButton.SetValue(AutomationProperties.NameProperty, $"{context} · {notice}");
    }

    private void NoticeToggle_OnClick(object? sender, RoutedEventArgs e)
    {
        _notice = ShellCards.ToggleNotice(_notice);
        SyncTransientVisibility();
    }

    // --- Worker cards ----------------------------------------------------------

    /// Rebuild the card strip from the tab-strip model with minimal churn:
    /// identical strips skip entirely (focus, scroll and selection
    /// untouched); otherwise buttons are updated in place, added, moved or
    /// removed by key, so a state change never destroys the focused
    /// control. The Add button lives outside the strip in XAML.
    private void RebuildContextCards()
    {
        var fresh = new List<ContextCardData>();
        var selectedKey = SelectedWorker?.Id ?? SelectedEntry?.Key;
        foreach (var item in WorkspaceTabs.Items)
        {
            if (item is not TabStripItem tab)
            {
                continue;
            }

            if (tab.Tag is PiWorker worker)
            {
                fresh.Add(WorkerCardData(worker, worker.Id == selectedKey));
            }
            else if (tab.Tag is CatalogEntry entry)
            {
                fresh.Add(WorkspaceCardData(entry, entry.Key == selectedKey));
            }
        }

        var strip = ContextCards.Children;
        var current = strip.OfType<Button>().Select(b => (b.Tag as CardView)?.Data).ToList();
        if (current.All(d => d is not null) && ShellCards.CardsEqual(current!, fresh))
        {
            ApplyCardWidths();
            return;
        }

        var byKey = strip.OfType<Button>()
            .Where(b => (b.Tag as CardView)?.Data is not null)
            .ToDictionary(
                b => ((CardView)b.Tag!).Data.Key,
                b => (CardView)b.Tag!,
                StringComparer.Ordinal);
        foreach (var key in byKey.Keys.Where(k => fresh.All(f => f.Key != k)).ToList())
        {
            strip.Remove(byKey[key].Button);
        }

        for (var i = 0; i < fresh.Count; i++)
        {
            var data = fresh[i];
            if (!byKey.TryGetValue(data.Key, out var view) || !strip.Contains(view.Button))
            {
                view = CreateCardButton(data);
                byKey[data.Key] = view;
                strip.Insert(Math.Min(i, strip.Count), view.Button);
            }
            else
            {
                UpdateCardButton(view, data);
                var at = strip.IndexOf(view.Button);
                if (at != i)
                {
                    strip.Move(at, i);
                }
            }
        }

        ApplyCardWidths();
    }

    private ContextCardData WorkerCardData(PiWorker worker, bool selected)
    {
        var busy = _busyWorkers.Contains(worker.Id);
        var connecting = _connectingWorkers.Contains(worker.Id);
        var detail = busy
            ? "run in progress"
            : FolderShortName(worker.ProjectRoot, worker.Label);
        return ShellCards.WorkerCard(
            worker.Id,
            WorkerViewKey.DisplayLabel(worker.Id, worker.Label),
            detail,
            worker.IsConnected,
            busy,
            connecting,
            selected);
    }

    private ContextCardData WorkspaceCardData(CatalogEntry entry, bool selected)
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

        return ShellCards.WorkspaceCard(
            entry.Key,
            AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label),
            FolderShortName(root, entry.Label),
            connected,
            busy,
            entry.Key == _connectingKey,
            selected);
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

    private sealed record CardView(
        Button Button,
        Avalonia.Controls.Shapes.Ellipse Dot,
        TextBlock Name,
        TextBlock Status,
        ContextCardData Data);

    private CardView CreateCardButton(ContextCardData data)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = WorkerDotFill(data.IsConnected, data.IsBusy),
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
        button.Click += (_, _) => SelectContextCard(data.Key);
        button.ContextFlyout = BuildCardMenu(button);
        var view = new CardView(button, dot, name, status, data);
        button.Tag = view;
        return view;
    }

    /// Per-card contextual actions (relocated rail capabilities): Connect
    /// on the disconnected card, Reconnect/Disconnect beside it, Remove
    /// worker on worker cards. Every item selects its own card first
    /// (via the captured button's live Tag) and then runs the existing
    /// handler — no new connection paths, no new Core calls. States
    /// refresh on open from live selection/connection.
    private MenuFlyout BuildCardMenu(Button button)
    {
        var connect = new MenuItem { Header = "Connect" };
        var reconnect = new MenuItem { Header = "Reconnect" };
        var disconnect = new MenuItem { Header = "Disconnect" };
        var remove = new MenuItem { Header = "Remove worker…" };
        connect.Click += (_, e) => CardMenu_Act(button, ConnectPi_OnClick, e);
        reconnect.Click += (_, e) => CardMenu_Act(button, ReconnectPi_OnClick, e);
        disconnect.Click += (_, e) => CardMenu_Act(button, DisconnectPi_OnClick, e);
        remove.Click += (_, e) => CardMenu_Act(button, RemoveWorker_OnClick, e);
        var menu = new MenuFlyout();
        menu.Items.Add(connect);
        menu.Items.Add(reconnect);
        menu.Items.Add(disconnect);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        menu.Opening += (_, _) => RefreshCardMenu(button, connect, reconnect, disconnect, remove);
        // Programmatic open (composer pattern): right-click AND keyboard
        // menu keys open at the card, never off-window; Handled suppresses
        // any default so exactly one menu appears.
        button.ContextRequested += (_, e) =>
        {
            menu.ShowAt(button);
            e.Handled = true;
        };
        return menu;
    }

    private void CardMenu_Act(Button button, Action<object?, Avalonia.Interactivity.RoutedEventArgs> handler, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((button.Tag as CardView)?.Data is { } data)
        {
            SelectContextCard(data.Key);
            handler(button, e);
        }
    }

    /// Resolve the card's live target: a current worker by id, else the
    /// catalog entry still behind the tab. A removed worker or entry
    /// disables the whole menu rather than acting on stale identity.
    private bool TryCardTarget(Button button, out PiWorker? worker, out CatalogEntry? entry)
    {
        worker = null;
        entry = null;
        if ((button.Tag as CardView)?.Data is not { } data)
        {
            return false;
        }

        worker = _manager?.Workers.FirstOrDefault(w => w.Id == data.Key);
        if (worker is not null)
        {
            return true;
        }

        entry = WorkspaceTabs.Items.OfType<TabStripItem>()
            .Select(t => t.Tag).OfType<CatalogEntry>()
            .FirstOrDefault(e => e.Key == data.Key);
        return entry is not null;
    }

    private void RefreshCardMenu(Button button, MenuItem connect, MenuItem reconnect, MenuItem disconnect, MenuItem remove)
    {
        if (!TryCardTarget(button, out var worker, out var entry))
        {
            connect.IsEnabled = reconnect.IsEnabled = disconnect.IsEnabled = remove.IsEnabled = false;
            return;
        }

        if (worker is not null)
        {
            var busy = _busyWorkers.Contains(worker.Id) || _connectingWorkers.Contains(worker.Id);
            connect.IsEnabled = !worker.IsConnected && !busy;
            ToolTip.SetTip(connect, $"Connect worker '{WorkerViewKey.DisplayLabel(worker.Id, worker.Label)}'");
            reconnect.IsEnabled = worker.IsConnected && !busy;
            ToolTip.SetTip(reconnect, "Reconnect (session is persisted)");
            disconnect.IsVisible = false;
            remove.IsVisible = true;
            remove.IsEnabled = !busy;
            ToolTip.SetTip(remove, "Stop the worker's process and remove its registration (session files stay on disk)");
        }
        else
        {
            var connected = _connectedKey == entry!.Key && _conversation?.IsConnected == true;
            connect.IsEnabled = !connected && !_connecting;
            ToolTip.SetTip(connect, $"Connect workspace '{entry.Label}'");
            reconnect.IsEnabled = connected;
            ToolTip.SetTip(reconnect, "Reconnect (session is persisted)");
            disconnect.IsVisible = true;
            disconnect.IsEnabled = connected;
            ToolTip.SetTip(disconnect, "Disconnect the workspace session.");
            remove.IsVisible = false;
        }
    }

    private void UpdateCardButton(CardView view, ContextCardData data)
    {
        var updated = view with { Data = data };
        var index = ContextCards.Children.IndexOf(view.Button);
        if (index >= 0)
        {
            view.Button.Tag = updated;
        }

        updated.Dot.Fill = WorkerDotFill(data.IsConnected, data.IsBusy);
        updated.Name.Text = data.Name;
        updated.Status.Text = data.StatusLine;
        updated.Button.Background = data.IsSelected ? AxTheme.Surface2Brush : Brushes.Transparent;
        updated.Button.BorderBrush = data.IsSelected ? AxTheme.BorderStrongBrush : AxTheme.BorderBrush;
        updated.Button.SetValue(AutomationProperties.NameProperty, $"Select {data.Name}");
    }

    /// Share the viewport across cards (single card fills the strip so its
    /// edges meet the page below; overflow scrolls past the 200px floor).
    private void ApplyCardWidths()
    {
        var cards = ContextCards.Children.OfType<Button>()
            .Where(b => b.Tag is CardView)
            .ToList();
        if (cards.Count == 0)
        {
            return;
        }

        var viewport = CardScroller.Bounds.Width;
        if (viewport <= 0)
        {
            return;
        }

        var width = ShellCards.CardWidth(viewport, cards.Count);
        foreach (var card in cards)
        {
            if (double.IsNaN(card.Width) || Math.Abs(card.Width - width) >= 0.5)
            {
                card.Width = width;
            }
        }
    }

    /// Bounds subscription needs an observer, not a lambda.
    private sealed class CardWidthWatcher(MainWindow window) : IObserver<Rect>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(Rect value) => window.ApplyCardWidths();
    }

    private void AddContextButton_OnClick(object? sender, RoutedEventArgs e)
        => AddWorkspace_OnClick(sender, e);

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
            if (_gitBranchCache.TryGetValue(root, out var cached)
                && !ShellCards.BranchNeedsRefresh(cached.CheckedUtc, DateTime.UtcNow))
            {
                branch = cached.Branch;
            }
            else
            {
                RefreshGitBranchAsync(root);
            }
        }

        var (left, right) = ShellCards.FooterText(name, branch, model, thinking, context);
        FooterContextText.Text = left;
        FooterEngineText.Text = right;
        FooterGauge.Percent = context;
        RefreshGaugeTip();
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

                _gitBranchCache[root] = (branch, DateTime.UtcNow);
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
