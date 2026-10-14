using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentX.Core;

namespace AgentX.App;

/// <summary>
/// Left-rail pages (Live default). Indices match the page visibility
/// switch; a future Stats page plugs in as index 3 with one more rail
/// item — no placeholder, no dead destination in the meantime.
/// </summary>
public enum AppPage
{
    Overview = 0,
    Live = 1,
    Settings = 2,
}

public partial class MainWindow : Window
{
    private const int HistoryDisplayCap = 2000;
    private const int LiveDisplayCap = 32_000;

    private TranscriptImportResult? _current;
    // Volatile: event-reader threads take a guarded snapshot for the
    // stale-callback check, so tool/activity events never need a UI post
    // just to ask which conversation is current. All writes stay on the
    // UI thread (connect assigns, disconnect nulls).
    private volatile PiConversation? _conversation;
    private CancellationTokenSource? _promptLifetime;
    private bool _piBusy;

    // Worker tabs + pages: one active workspace at a time, presentation-only
    // page switches. Tab selection never retargets a live Pi process (only
    // Disconnect/Reconnect change connection state); page switches never
    // touch the feed, so scroll, selection and composer input survive.
    // Per-worker-tab view state (App-owned): composer draft, selection by
    // feed Seq, scroll offset, status snapshots. Transcript memory lives in
    // the Core coordinator; this dictionary covers only what the view owns.
    private readonly WorkerViewLifecycle _viewLifecycle = new();
    private ViewPrefs _viewPrefs = new();
    // Engine settings boundary (Agent ONE owns the Core contract): model
    // and thinking selection flow ONLY through this adapter. Pending until
    // Core ships the verified operations; the footer says so explicitly.
    private readonly IEngineSettingsRuntime _settingsRuntime = new PendingEngineSettingsRuntime();
    // Last genuine engine values (from get_state on connect/reconnect).
    // Usage shows messages only: token/cost context does not exist here.
    private string? _knownProvider;
    private string? _knownModelId;
    private string? _knownModelDisplay;
    private string? _knownThinking;
    private int? _knownMessages;
    // Left-rail pages. Live is default; a future Stats page plugs in as a
    // fourth enum member + one rail item — no placeholders in the meantime.
    private AppPage _page = AppPage.Live;
    private bool _syncingRail;
    private WorkspaceCatalog _catalog = null!;
    private AgentXPreferences _prefs = new();
    private string? _connectedKey;
    private string? _connectedLabel;
    // Immutable owner of the live conversation: reconnect restores THIS
    // workspace even if the picker selection moved meanwhile. Set on
    // connect, cleared only on disconnect — never by selection changes.
    private AgentXWorkspace? _connectedWorkspace;
    // Per-workspace ownership: every identity transition — user tab click
    // or programmatic reselect after a registry rebuild — flows through one
    // coordinator, so the guarded switch and the feed-memory sync cannot be
    // bypassed by a suppressed UI event.
    private WorkspaceSessionCoordinator _coordinator = null!;
    // Guards against re-entrant UI updates (programmatic reselect, pref sync).
    private bool _suppressTabEvents;
    private bool _syncingPrefs;
    // True while a connect or reconnect is underway: blocks re-entrant
    // Connect/Reconnect/switch transitions. Set alongside _piBusy on UI thread.
    private bool _connecting;

    // Genuine multiworker runtime (one manager per app): worker tabs render
    // worker.Feed; selection is a view pointer only — background workers are
    // never interrupted, and worker switching never touches the workspace
    // coordinator. Null when workers.json failed to load (fail-closed): the
    // worker UI then stays disabled and the workspace path works alone.
    private PiWorkerManager? _manager;
    private string? _managerError;
    // Per-worker run/connect state (UI thread only): busy = run in flight,
    // connecting = connect/reconnect underway, sinks/seqs = live run display.
    // Run ownership, cancellation and status are keyed by worker identity,
    // never by global selected-tab state.
    private readonly HashSet<string> _busyWorkers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _connectingWorkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunProgressSink> _runSinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _runSeqs = new(StringComparer.Ordinal);
    // Selected worker id mirrored off the UI thread: feed/activity events
    // arrive on RPC threads that must never touch Avalonia controls, so the
    // drop-or-sync decision reads this (UI thread writes it on every view
    // switch). A stale read self-heals on the next event or the switch
    // rebuild — never a crash, never a misroute.
    private volatile string? _selectedWorkerId;
    // True once the window closed: marshaled manager callbacks arriving
    // late must not touch dead controls.
    private bool _closed;
    // True while a folder picker is open: Add is not re-entrant (stacked
    // modal pickers). Set on UI thread around OpenFolderPickerAsync.
    private bool _pickingWorkspace;

    private static string ManagedRoot => PiSessionOptions.Default().ManagedRoot;

    // Conversation feed: the Core feed owns the exact text; the observable
    // collection is only the bounded display view. Streamed deltas buffer in
    // the accumulator and reach the screen on a 100 ms flush tick instead of
    // rebuilding the full text per character.
    private readonly ConversationFeed _feed = new();
    private readonly ObservableCollection<ConversationViewItem> _conversationItems = new();
    // Bounded dispatch: feed Changed may fire thousands of times per second
    // (streaming chunks, tool/detail bursts); at most one sync is ever
    // queued and each run observes the latest state. No unbounded queue,
    // no per-event full-history pass.
    private readonly FeedEventCoalescer _syncCoalescer;
    // Latest activity text from any thread; the coalesced tick applies it.
    private volatile string? _pendingActivityText;
    // Sync ticks executed (evidence for burst behaviour in Overview facts).
    private long _syncRuns;
    private StreamingTextAccumulator? _liveAccumulator;
    private long _liveSeq = -1;
    private readonly DispatcherTimer _flushTimer;
    // Highest feed Seq materialized in the view. SyncFeedView (below) is the
    // single incremental path from feed mutations to rows.
    private long _shownSeq;
    // Observability bridge: routes tap detail rows into the same feed at the
    // visibility level. Re-attached to every fresh client via the factory so
    // reconnects keep flowing without touching Core client code.
    private readonly FeedBridge _bridge;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversationItems;
        // Empty state follows the collection itself: any add/clear on the UI
        // thread updates it, on every page, with no scattered call sites.
        _conversationItems.CollectionChanged += (_, _) => UpdateEmptyState();
        _feed.Changed += FeedChanged;
        _syncCoalescer = new FeedEventCoalescer(work => Dispatcher.UIThread.Post(work));
        _bridge = new FeedBridge(_feed);
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushLive();
        // Chord delivery: the TextBox inserts the newline in its own class
        // handling on the bubble route, so a bubble subscription would send
        // AND leak a newline into the draft. The tunnel runs first: marking
        // the chord handled preempts the control, so Ctrl+Enter sends exactly
        // what is in the box and plain Enter still inserts its newline.
        PiPromptBox.AddHandler(
            InputElement.KeyDownEvent,
            new EventHandler<Avalonia.Input.KeyEventArgs>(PiPromptBox_OnKeyDown),
            Avalonia.Interactivity.RoutingStrategies.Tunnel,
            handledEventsToo: true);
        var (prefs, note) = AgentXPreferences.Load(ManagedRoot);
        _prefs = prefs;
        ApplyVisibility();
        _viewPrefs = ViewPrefs.Load(ManagedRoot);
        ApplyRailWidth();
        _coordinator = new WorkspaceSessionCoordinator(_feed, WorkspaceCatalog.LegacyKey);
        // One worker manager per app; persisted workers become real tabs
        // below. A corrupt workers.json fails closed (file preserved, worker
        // UI disabled) — the workspace path keeps working alone.
        try
        {
            _manager = new PiWorkerManager(ManagedRoot);
            foreach (var worker in _manager.Workers)
            {
                AttachWorker(worker);
            }

            _manager.WorkerAdded += (_, worker) =>
            {
                AttachWorker(worker);
                RebuildWorkerTabs();
            };
            _manager.WorkerRemoved += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // RemoveWorkerAsync fires after ConfigureAwait(false): this
                // can arrive on a ThreadPool thread. Marshal to the UI
                // dispatcher, and never touch a closed window.
                if (!_closed)
                {
                    RebuildWorkerTabs();
                }
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _manager = null;
            _managerError = $"Worker store unreadable — workers unavailable, file preserved. {ex.Message}";
        }
        ApplyTheme();
        ThemeCombo.ItemsSource = AgentXPreferences.KnownThemes;
        TreatmentCombo.ItemsSource = AxTreatments.Names;
        AxTheme.Apply(AxTreatments.Parse(_viewPrefs.Treatment));
        _syncingPrefs = true;
        try
        {
            AutoScrollMenuItem.IsChecked = _prefs.AutoScroll;
        }
        finally
        {
            _syncingPrefs = false;
        }

        LoadWorkspaceTabs(selectKey: WorkspaceCatalog.LegacyKey);
        ShowPage("Live");
        RefreshSettings();
        if (note is not null)
        {
            StatusText.Text = note;
        }

        if (_viewPrefs.IsRecovery)
        {
            StatusText.Text = _viewPrefs.RecoveryMessage;
        }

        AppendWorkerTabs();
        UpdateTabDots();
        if (_managerError is not null)
        {
            StatusText.Text = _viewPrefs.IsRecovery
                ? $"{_viewPrefs.RecoveryMessage} {_managerError}"
                : _managerError;
        }
    }

    /// Append persisted worker tabs after the workspace tabs (startup only;
    /// later changes go through RebuildWorkerTabs). Never steals the
    /// workspace selection — startup still lands on legacy.
    private void AppendWorkerTabs()
    {
        if (_manager is null)
        {
            return;
        }

        _suppressTabEvents = true;
        try
        {
            var tintIndex = WorkspaceTabs.Items.Count;
            foreach (var worker in _manager.Workers)
            {
                WorkspaceTabs.Items.Add(MakeWorkerTab(
                    worker, worker.IsConnected, _busyWorkers.Contains(worker.Id), tintIndex++));
            }
        }
        finally
        {
            _suppressTabEvents = false;
        }
    }

    // --- Workspace tabs: one active identity, guarded switching. ---

    private void LoadWorkspaceTabs(string? selectKey = null)
    {
        // The rebuild performs its own authoritative transition below, so
        // list construction must not re-enter through WorkspaceTabs_OnChanged:
        // adding the first tab auto-selects it, and that shadow transition
        // would snapshot the still-empty composer (startup wipe) and repeat
        // feed snapshots on every rebuild. User clicks still flow through
        // the handler — they only happen outside rebuilds.
        _suppressTabEvents = true;
        try
        {
        // Never throws for registry problems: the catalog degrades to
        // legacy-only with a recovery reason instead of crashing init.
        _catalog = WorkspaceCatalog.Load(ManagedRoot);
        AddWorkspaceButton.IsEnabled = !_catalog.IsRecovery;

        // Tab list + transition target come from one tested Core decision:
        // a connected workspace missing from the catalog keeps an explicitly
        // labeled retained tab, so its feed is never shown under another
        // tab's name. Never fabricated — retention rebuilds from the live
        // connection's own workspace object.
        var resolved = WorkspaceCatalog.ResolveTabs(
            _catalog,
            _conversation is not null ? _connectedWorkspace : null,
            selectKey);
        var connectedLive = _conversation is not null && _connectedKey is not null;
        WorkspaceTabs.Items.Clear();
        var tintIndex = 0;
        foreach (var entry in resolved.Entries)
        {
            WorkspaceTabs.Items.Add(MakeTab(entry,
                isConnected: connectedLive && entry.Key == _connectedKey,
                tintIndex: tintIndex++));
        }

        // One authoritative transition for programmatic reselects too: a
        // connected workspace keeps its tab (new registrations never steal
        // the view), and a disconnected reselect snapshots/restores feeds
        // exactly like a user click. Suppressed selection alone would skip
        // all of that and can show the wrong project's history.
        // Snapshot the outgoing VIEW first (draft, selection, scroll,
        // status) under its owning key: the coordinator snapshots the feed
        // below, but without this the rebuild would restore the target's
        // (possibly stale) snapshot over unsent work. On refusal the same
        // key is restored afterwards, round-tripping exactly; unchanged
        // views skip the disk write inside. No feed work is added here.
        // Startup exception: on the very first rebuild the composer still
        // holds its default empty string, so snapshotting now would store
        // "" over the persisted draft before the restore below ever reads
        // it. Restore first; snapshot on every later rebuild (including
        // refused pinned switches, which round-trip the same key).
        if (_viewLifecycle.StartupComplete)
        {
            SaveWorkerViewState(_lastViewKey);
        }
        var result = _coordinator.RequestTransition(resolved.TargetKey);
        SelectTab(_coordinator.ActiveKey);
        RebuildConversationList();
        RebuildSessionMap();
        if (OverviewPanel.IsVisible)
        {
            RebuildOverview();
        }

        UpdateEmptyState();
        RestoreWorkerViewState(_coordinator.ActiveKey);
        _lastViewKey = _coordinator.ActiveKey;
        _selectedWorkerId = null;
        RefreshWorkingRow();
        RefreshDirectoryLabel();
        RefreshEngineSegments();
        PiConnectionState.Text = _piBusy ? "Working…" : _connecting ? "Connecting…" : _conversation?.IsConnected == true ? "Connected" : "Disconnected";
        PiActivityText.Text = string.Empty;
        _pendingActivityText = null;
        if (!result.Accepted)
        {
            StatusText.Text = $"Staying on '{_connectedLabel}' — {result.RefusalReason} Histories never mix.";
        }
        else if (_conversation is null && SelectedEntry is { } current)
        {
            StatusText.Text = current.IsLegacy
                ? "Main selected — press Connect."
                : $"Workspace '{current.Label}' selected — press Connect.";
        }

        if (_catalog.RecoveryError is { } recovery)
        {
            StatusText.Text = resolved.RetentionNote is not null
                ? $"{recovery} {resolved.RetentionNote}"
                : recovery;
        }
        }
        finally
        {
            _suppressTabEvents = false;
        }
    }

    private static TabStripItem MakeTab(CatalogEntry entry, bool isConnected, int tintIndex)
    {
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = isConnected
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7DD3A8"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280")),
        };
        // Display label only: legacy shows as Main (storage identity and
        // session id stay in the tooltip). See AxTheme.WorkspaceTabLabel.
        var label = new TextBlock { Text = AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(dot);
        header.Children.Add(label);
        // Stable identity tint per tab position (terminal-tab idea): a
        // translucent fill, brightened when selected (see UpdateTabDots).
        // State is never colour-alone: dot + footer text + tooltip carry it.
        var tint = Avalonia.Media.Color.Parse(WorkerTabColors.TintFor(tintIndex));
        var tab = new TabStripItem
        {
            Content = header,
            Background = new Avalonia.Media.SolidColorBrush(tint, 0.28),
        };
        Avalonia.Controls.ToolTip.SetTip(tab, entry.Detail);
        tab.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, entry.Label);
        tab.Tag = entry;
        return tab;
    }

    private CatalogEntry? SelectedEntry => (WorkspaceTabs.SelectedItem as TabStripItem)?.Tag as CatalogEntry;

    /// Selected worker, or null while a workspace tab is selected. A tab's
    /// Tag is exactly one of the two — workers are never presented as
    /// workspace entries and legacy/workspace tabs never as workers.
    private PiWorker? SelectedWorker => (WorkspaceTabs.SelectedItem as TabStripItem)?.Tag as PiWorker;

    /// Feed the view renders: the selected worker's own feed, else the
    /// workspace feed. Resolved live at every use, so a queued sync that
    /// outlives a tab switch renders the new feed, never a stale one.
    private ConversationFeed ActiveFeed => SelectedWorker?.Feed ?? _feed;

    /// Whether a feed row belongs to the viewed run (follow-behaviour only,
    /// never routing). Worker turn Seqs are Core-owned: the GUI records the
    /// last Seq before sending and follows newer rows of that run.
    private bool IsLiveRow(long seq) => SelectedWorker is { } worker
        ? _runSeqs.TryGetValue(worker.Id, out var floor) && seq > floor
        : seq == _liveSeq;

    /// Owning key of the visible view (worker id namespaced, workspace key
    /// passthrough). Updated at the end of every view switch; the next
    /// switch saves the outgoing draft/selection/scroll under exactly this.
    private string _lastViewKey = WorkspaceCatalog.LegacyKey;

    /// Subscribe one worker's view-relevant events. Feed rows, tool pairing
    /// and error rows are written by Core on the RPC thread; the GUI only
    /// syncs the view (when selected) and refreshes tab dots on death.
    /// Tool events need no GUI subscription at all.
    private void AttachWorker(PiWorker worker)
    {
        worker.Feed.Changed += () => WorkerFeedChanged(worker);
        worker.ActivityChanged += Worker_ActivityChanged;
        worker.ConnectionLost += Worker_ConnectionLost;
        worker.Visibility = _prefs.Visibility switch
        {
            "Compact" => VisibilityLevel.Compact,
            "Full" => VisibilityLevel.Full,
            _ => VisibilityLevel.Detailed,
        };
    }

    private void WorkerFeedChanged(PiWorker worker)
    {
        // Background feeds never touch the view: no focus steal, no scroll
        // disturbance. The full rebuild on selection shows everything.
        // Runs on the RPC thread: compare ids, never touch controls.
        if (worker.Id != _selectedWorkerId)
        {
            return;
        }

        _syncCoalescer.Signal(SyncFeedView);
    }

    private void Worker_ActivityChanged(object? sender, string activity)
    {
        // Feed activity was already recorded by Core; stash the footer text
        // only for the viewed worker — never let a background worker
        // overwrite the reader's activity line. Dots refresh with run
        // transitions, not per activity event.
        if (sender is PiWorker worker && worker.Id == _selectedWorkerId)
        {
            _pendingActivityText = activity;
        }
    }

    private void Worker_ConnectionLost(object? sender, Exception error)
    {
        if (sender is not PiWorker worker)
        {
            return;
        }

        // Core already recorded the error row and cleared feed activity.
        // If this worker is selected its feed Changed drives the view sync;
        // otherwise the reader is undisturbed apart from the tab dot.
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTabDots();
            if (!ReferenceEquals(worker, SelectedWorker))
            {
                return;
            }

            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            PiConnectionState.Text = "Connection lost";
            PiRunStatus.Text = "Connection to Pi lost — session is persisted. Press Reconnect to restore.";
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            RefreshConnectionButtons();
            RefreshEngineSegments();
        });
    }

    private void SelectTab(string key)
    {
        // Nesting-safe: rebuilds already suppress around this call.
        var priorSuppress = _suppressTabEvents;
        _suppressTabEvents = true;
        try
        {
            foreach (var item in WorkspaceTabs.Items)
            {
                if (item is TabStripItem tab && (tab.Tag as CatalogEntry)?.Key == key)
                {
                    WorkspaceTabs.SelectedItem = tab;
                    _selectedWorkerId = null;
                    return;
                }
            }

            WorkspaceTabs.SelectedIndex = 0;
            _selectedWorkerId = null;
        }
        finally
        {
            _suppressTabEvents = priorSuppress;
        }
    }

    private void SelectWorkerTab(string id)
    {
        // Nesting-safe twin of SelectTab for worker ids.
        var priorSuppress = _suppressTabEvents;
        _suppressTabEvents = true;
        try
        {
            foreach (var item in WorkspaceTabs.Items)
            {
                if (item is TabStripItem tab && (tab.Tag as PiWorker)?.Id == id)
                {
                    WorkspaceTabs.SelectedItem = tab;
                    _selectedWorkerId = id;
                    return;
                }
            }
        }
        finally
        {
            _suppressTabEvents = priorSuppress;
        }
    }

    /// Switch the view to a worker tab: save the outgoing view under its
    /// owning key, rebind to the worker's own feed, rebuild derived views,
    /// restore this worker's draft/selection/scroll. No coordinator, no
    /// refusal, no process action — a foreground run continues on any tab.
    private void SwitchToWorker(PiWorker worker)
    {
        SaveWorkerViewState(_lastViewKey);
        _lastViewKey = WorkerViewKey.ForWorker(worker.Id);
        _selectedWorkerId = worker.Id;
        // Keep the manager's view pointer truthful (SelectionChanged has no
        // GUI subscriber; same-id selects raise no event).
        _manager?.SelectWorker(worker.Id);
        _pendingActivityText = null;
        RebuildConversationList();
        RebuildSessionMap();
        if (OverviewPanel.IsVisible)
        {
            RebuildOverview();
        }

        UpdateEmptyState();
        var restored = RestoreWorkerViewState(_lastViewKey);
        UpdateTabDots();
        RefreshDirectoryLabel();
        RefreshConnectionButtons();
        RefreshEngineSegments();
        RefreshWorkingRow();
        // Connection state follows the viewed worker too — never another
        // tab's leftover. Activity restarts from the live feed's ticks.
        PiConnectionState.Text = _busyWorkers.Contains(worker.Id)
            ? "Working…"
            : _connectingWorkers.Contains(worker.Id)
                ? "Connecting…"
                : worker.IsConnected ? "Connected" : "Disconnected";
        PiActivityText.Text = string.Empty;
        if (!restored)
        {
            var label = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
            StatusText.Text = worker.IsConnected
                ? $"Worker '{label}' selected."
                : $"Worker '{label}' selected — press Connect.";
        }
    }

    private static Avalonia.Media.IBrush WorkerDotFill(bool connected, bool busy)
        => busy
            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5C07B"))
            : connected
                ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7DD3A8"))
                : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280"));

    private static TabStripItem MakeWorkerTab(PiWorker worker, bool isConnected, bool busy, int tintIndex)
    {
        // Same chrome as workspace tabs (B3 fidelity): dot + label, identity
        // tint, tooltip and automation name carry the worker identity so a
        // worker tab is never mistaken for a legacy/workspace tab.
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = WorkerDotFill(isConnected, busy),
        };
        var display = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
        var label = new TextBlock { Text = display, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        var header = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(dot);
        header.Children.Add(label);
        var tint = Avalonia.Media.Color.Parse(WorkerTabColors.TintFor(tintIndex));
        var tab = new TabStripItem
        {
            Content = header,
            Background = new Avalonia.Media.SolidColorBrush(tint, 0.28),
        };
        Avalonia.Controls.ToolTip.SetTip(tab, WorkerViewKey.Tooltip(worker.Id, worker.Label, worker.SessionId, worker.ProjectRoot));
        tab.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, display);
        tab.Tag = worker;
        return tab;
    }

    /// Rebuild ONLY the worker section of the tab strip (workspace tabs and
    /// the coordinator are untouched). A surviving selection is reselected
    /// without touching its view; a vanished selection falls forward to the
    /// manager selection or back to the legacy workspace view.
    private void RebuildWorkerTabs()
    {
        if (_manager is null)
        {
            return;
        }

        var selectedId = SelectedWorker?.Id;
        _suppressTabEvents = true;
        try
        {
            for (var i = WorkspaceTabs.Items.Count - 1; i >= 0; i--)
            {
                if (WorkspaceTabs.Items[i] is TabStripItem { Tag: PiWorker })
                {
                    WorkspaceTabs.Items.RemoveAt(i);
                }
            }

            var tintIndex = WorkspaceTabs.Items.Count;
            foreach (var worker in _manager.Workers)
            {
                WorkspaceTabs.Items.Add(MakeWorkerTab(
                    worker, worker.IsConnected, _busyWorkers.Contains(worker.Id), tintIndex++));
            }

            // Never steal a workspace view: reselect only when a worker was
            // selected. Otherwise the viewed worker vanished: fall forward
            // to the manager selection, else back to legacy.
            if (selectedId is not null)
            {
                // Pure repair decision (tested): surviving views reselect
                // untouched, vanished views fall to the manager selection.
                var target = WorkerDisplayPolicy.RepairTarget(
                    selectedId,
                    _manager.Workers.Select(w => w.Id).ToArray(),
                    _manager.Selected?.Id);
                if (target is not null)
                {
                    SelectWorkerTab(target);
                }
                else
                {
                    SelectTab(WorkspaceCatalog.LegacyKey);
                }
            }
        }
        finally
        {
            _suppressTabEvents = false;
        }

        // The view follows only when the viewed worker actually vanished;
        // list-only changes (add/remove elsewhere) never disturb the box.
        var now = SelectedWorker;
        if (selectedId is not null && (now is null || now.Id != selectedId))
        {
            if (now is not null)
            {
                SwitchToWorker(now);
            }
            else
            {
                LoadWorkspaceTabs(selectKey: WorkspaceCatalog.LegacyKey);
            }
        }

        _selectedWorkerId = SelectedWorker?.Id;
        UpdateTabDots();
    }

    private void UpdateTabDots()
    {
        var index = 0;
        foreach (var item in WorkspaceTabs.Items)
        {
            if (item is TabStripItem tab
                && tab.Content is StackPanel header
                && header.Children.Count > 0
                && header.Children[0] is Avalonia.Controls.Shapes.Ellipse dot)
            {
                if (tab.Tag is PiWorker workerTab)
                {
                    // Worker tabs show worker truth: connected green, running
                    // amber, idle grey. Never the workspace connection state.
                    dot.Fill = WorkerDotFill(workerTab.IsConnected, _busyWorkers.Contains(workerTab.Id));
                }
                else if (tab.Tag is CatalogEntry entry)
                {
                    var on = entry.Key == _connectedKey && _conversation?.IsConnected == true;
                    var busy = on && _piBusy;
                    dot.Fill = busy
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5C07B"))
                        : on
                            ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7DD3A8"))
                            : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280"));
                }
                // Identity tint follows tab position; selection brightens
                // the fill so the active worker reads at a glance. State
                // itself stays on the dot + footer text, never colour alone.
                var selected = ReferenceEquals(WorkspaceTabs.SelectedItem, tab);
                var tint = Avalonia.Media.Color.Parse(WorkerTabColors.TintFor(index));
                tab.Background = new Avalonia.Media.SolidColorBrush(tint, selected ? 0.55 : 0.28);
            }

            index++;
        }

        RefreshConnectionButtons();
    }

    /// Working indicator follows the VIEWED run only — never another
    /// worker's activity. Background runs keep their tab dots.
    private void RefreshWorkingRow()
    {
        var viewedBusy = SelectedWorker is { } worker ? _busyWorkers.Contains(worker.Id) : _piBusy;
        WorkingRow.IsVisible = WorkerDisplayPolicy.ShowWorking(viewedBusy);
        // The toolbar Stop icon exists only while the VIEWED run is active —
        // never a permanent button, never for another worker's run.
        StopButton.IsVisible = viewedBusy;
    }

    /// Button states follow the selection: Disconnect stops the workspace
    /// connection only (workers end via Remove worker); Remove worker acts
    /// on worker tabs only. Refreshed with the tab dots, so every selection
    /// and state change carries it — no scattered call sites.
    private void RefreshConnectionButtons()
    {
        var workerSelected = SelectedWorker is not null;
        DisconnectMenuItem.IsEnabled = !workerSelected;
        Avalonia.Controls.ToolTip.SetTip(DisconnectMenuItem, workerSelected
            ? "Workers have no disconnect — Remove worker stops the selected worker's process."
            : "Disconnect the workspace session.");
        RemoveWorkerMenuItem.IsEnabled = workerSelected && _manager is not null;
    }

    private void WorkspaceTabs_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents)
        {
            return;
        }

        // Worker tabs bypass the workspace coordinator entirely: its
        // connected-pins-the-tab rule belongs to the single-connection
        // design and would refuse worker switching. Selection is a view
        // pointer only — background workers keep running untouched.
        if (SelectedWorker is { } worker)
        {
            SwitchToWorker(worker);
            return;
        }

        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        // Same authoritative path as programmatic reselects: the coordinator
        // refuses pinned workspaces and syncs feed memory atomically.
        // Save this tab's view state FIRST: refusal keeps the same view, and
        // saving is idempotent, so the current draft/selection/scroll/status
        // always lands on the key that owns them (the last viewed tab, which
        // may be a worker the coordinator never knew).
        SaveWorkerViewState(_lastViewKey);
        var result = _coordinator.RequestTransition(entry.Key);
        if (!result.Accepted)
        {
            // The kept tab must own a coherent view: the composer and rows
            // may still show the worker (or workspace) we came from, which
            // would misroute the next prompt under the wrong identity.
            // Restore the active workspace view first; the refusal stays
            // the final word.
            SaveWorkerViewState(_lastViewKey);
            SelectTab(_coordinator.ActiveKey);
            _lastViewKey = _coordinator.ActiveKey;
            _selectedWorkerId = null;
            _pendingActivityText = null;
            RebuildConversationList();
            RebuildSessionMap();
            if (OverviewPanel.IsVisible)
            {
                RebuildOverview();
            }

            UpdateEmptyState();
            RestoreWorkerViewState(_lastViewKey);
            UpdateTabDots();
            RefreshDirectoryLabel();
            RefreshConnectionButtons();
            RefreshEngineSegments();
            RefreshWorkingRow();
            PiConnectionState.Text = _piBusy ? "Working…" : _connecting ? "Connecting…" : _conversation?.IsConnected == true ? "Connected" : "Disconnected";
            PiActivityText.Text = string.Empty;
            StatusText.Text = $"Disconnect from '{_connectedLabel}' before opening '{entry.Label}' — histories never mix.";
            return;
        }

        RebuildConversationList();
        RebuildSessionMap();
        if (OverviewPanel.IsVisible)
        {
            RebuildOverview();
        }

        UpdateEmptyState();
        var restored = RestoreWorkerViewState(entry.Key);
        _lastViewKey = entry.Key;
        _selectedWorkerId = null;
        UpdateTabDots();
        RefreshDirectoryLabel();
        RefreshWorkingRow();
        RefreshEngineSegments();
        // Footer connection/activity follow the viewed workspace: a worker's
        // leftovers must not linger under this tab. The feed keeps truth.
        PiConnectionState.Text = _piBusy ? "Working…" : _connecting ? "Connecting…" : _conversation?.IsConnected == true ? "Connected" : "Disconnected";
        PiActivityText.Text = string.Empty;
        _pendingActivityText = null;
        if (_conversation is null && !restored)
        {
            StatusText.Text = entry.IsLegacy
                ? "Main selected — press Connect."
                : $"Workspace '{entry.Label}' selected — press Connect.";
        }
    }

    /// <summary>
    /// Snapshot the view-owned state of one worker tab: composer draft,
    /// selection (by feed Seq), scroll offset, and the status strings it
    /// showed. Transcript stays in the Core coordinator. Connection and
    /// engine identity stay global (one live worker max under today's
    /// contract); only the per-view feedback is snapshotted.
    /// </summary>
    /// Snapshot the view-owned state of one worker tab and persist it when
    /// anything changed. Returns true when a persist was attempted. Memory
    /// always updates first; a failed disk write marks a retry instead of
    /// destroying the in-memory copy.
    private bool SaveWorkerViewState(string key)
    {
        var snapshot = new WorkerViewState
        {
            DraftText = PiPromptBox.Text ?? string.Empty,
            SelectedSeq = (ConversationList.SelectedItem as ConversationViewItem)?.Seq,
            ScrollOffset = ConversationScrollOffset(),
            StatusText = StatusText.Text ?? string.Empty,
            RunStatusText = PiRunStatus.Text ?? string.Empty,
        };
        var changed = _viewLifecycle.Store.Save(key, snapshot);
        if (!changed && !_viewLifecycle.Store.TakeDirty(key))
        {
            return false; // Nothing new; skip the disk write entirely.
        }

        _viewPrefs.Drafts[key] = snapshot.DraftText;
        try
        {
            SaveViewPrefs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _viewLifecycle.Store.MarkDirty(key);
            StatusText.Text = $"Could not save view preferences: {ex.Message} — draft kept in memory.";
        }

        return true;
    }

    /// <summary>
    /// Restore one worker tab's view: draft (seeded from persisted prefs on
    /// first visit), selection, scroll, and its status snapshot. Live
    /// connection/engine strings stay global and are NOT overwritten here.
    /// Returns true when a prior snapshot existed (so callers keep it over
    /// generic hints).
    /// </summary>
    private bool RestoreWorkerViewState(string key)
    {
        // Ordering lives in WorkerViewLifecycle: first visit seeds from
        // persisted prefs (and completes startup); the empty composer is
        // never snapshotted before this runs (see LoadWorkspaceTabs).
        var (state, existed) = _viewLifecycle.Restore(
            key,
            k => _viewPrefs.Drafts.TryGetValue(k, out var draft) ? draft : null);

        PiPromptBox.Text = state.DraftText;
        ConversationList.SelectedItem = state.SelectedSeq is { } seq
            ? _conversationItems.FirstOrDefault(i => i.Seq == seq)
            : null;
        var offset = state.ScrollOffset;
        Dispatcher.UIThread.Post(() => SetConversationScrollOffset(offset));
        StatusText.Text = state.StatusText;
        PiRunStatus.Text = state.RunStatusText;
        return existed;
    }

    private double ConversationScrollOffset()
    {
        try
        {
            return ConversationList
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .FirstOrDefault()?.Offset.Y ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private void SetConversationScrollOffset(double offset)
    {
        try
        {
            var viewer = ConversationList
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .FirstOrDefault();
            if (viewer is not null)
            {
                viewer.Offset = new Avalonia.Vector(0, Math.Max(0, offset));
            }
        }
        catch
        {
            // Scroll restore is best-effort: a missed offset never loses data.
        }
    }

    // --- Pages: presentation-only switches. ---

    private async void AddWorkspace_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_catalog.IsRecovery)
        {
            StatusText.Text = $"Registration disabled while the registry is unreadable — repair or back up {WorkspaceRegistry.RegistryPath(ManagedRoot)}, then restart.";
            return;
        }

        if (_pickingWorkspace)
        {
            return;
        }

        _pickingWorkspace = true;
        try
        {
            await AddWorkspaceCoreAsync();
        }
        finally
        {
            _pickingWorkspace = false;
        }

        // Surface the staged confirmation: it lives on the Settings page.
        if (_pendingWorkspacePath is not null)
        {
            ShowPage("Settings");
        }
    }

    // Pending explicit confirmation: the picker result is staged here and
    // registered ONLY by ConfirmWorkspace_OnClick. A phantom or dismissed
    // picker can never create a workspace by itself.
    private string? _pendingWorkspacePath;

    private async Task AddWorkspaceCoreAsync()
    {
        // Visible state while the native dialog is up: confirm/cancel/fail
        // paths below all replace this. (A dialog that can never appear —
        // e.g. no interactive shell — leaves this line up: honest.)
        StatusText.Text = "Opening folder picker…";
        IReadOnlyList<Avalonia.Platform.Storage.IStorageFolder> folders;
        try
        {
            folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select project folder",
                AllowMultiple = false,
            });
        }
        catch (Exception ex)
        {
            // Async-void event boundary: a picker failure must surface as
            // status, never crash the window or touch the registry.
            StatusText.Text = $"Folder picker failed: {ex.Message} — nothing registered.";
            return;
        }

        if (folders.Count == 0)
        {
            StatusText.Text = "Folder picker cancelled — nothing registered.";
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusText.Text = "Could not resolve a local path for the selected folder — nothing registered.";
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusText.Text = $"Selected folder no longer exists: {path} — nothing registered.";
            return;
        }

        // Explicit confirmation step: stage, do not register. Only the
        // Confirm button below turns a staged path into a workspace.
        _pendingWorkspacePath = path;
        PendingConfirmText.Text = $"Register project at {path} ?";
        PendingConfirmPanel.IsVisible = true;
        StatusText.Text = "Confirm registration below — nothing has been registered yet.";
    }

    /// Create a worker for the staged folder (the Add flow's explicit
    /// confirmation). Reuses the existing picker staging — no new navigation.
    /// Shows the new worker immediately; the outgoing draft stays under its
    /// own key.
    private void CreateWorker_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = _pendingWorkspacePath;
        _pendingWorkspacePath = null;
        PendingConfirmPanel.IsVisible = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_manager is null)
        {
            StatusText.Text = $"Workers unavailable ({_managerError ?? "store failed to load"}) — nothing created.";
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusText.Text = $"Selected folder no longer exists: {path} — nothing created.";
            return;
        }

        try
        {
            var folder = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var worker = _manager.CreateWorker(path, Path.GetFileName(folder));
            // WorkerAdded already rebuilt the tab list; move the view now.
            SaveWorkerViewState(_lastViewKey);
            _manager.SelectWorker(worker.Id);
            SelectWorkerTab(worker.Id);
            SwitchToWorker(worker);
            StatusText.Text = $"Worker '{WorkerViewKey.DisplayLabel(worker.Id, worker.Label)}' created — session {worker.SessionId}. Press Connect.";
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText.Text = $"Cannot create worker: {ex.Message}";
        }
    }

    /// Remove the selected worker: Core retires its Pi process first, then
    /// unpersists. Refused while a run or connect is active. Session files
    /// stay on disk; the tab list rebuild moves the view to a survivor.
    private async void RemoveWorker_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedWorker is not { } worker || _manager is null)
        {
            return;
        }

        var id = worker.Id;
        var label = WorkerViewKey.DisplayLabel(id, worker.Label);
        if (!WorkerViewKey.CanRemove(_busyWorkers.Contains(id), _connectingWorkers.Contains(id)))
        {
            StatusText.Text = $"Worker '{label}' is active — cancel the run first.";
            return;
        }

        try
        {
            await _manager.RemoveWorkerAsync(id);
            StatusText.Text = $"Worker '{label}' removed — process stopped, session files kept on disk.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Cannot remove worker '{label}': {ex.Message} — retry to complete.";
        }
    }

    private void ConfirmWorkspace_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = _pendingWorkspacePath;
        _pendingWorkspacePath = null;
        PendingConfirmPanel.IsVisible = false;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            StatusText.Text = $"Selected folder no longer exists: {path} — nothing registered.";
            return;
        }

        WorkspaceRegistry registry;
        try
        {
            // Reload at confirm time: the file may have changed since startup.
            // Any new corruption drops to recovery instead of overwriting.
            registry = WorkspaceRegistry.Load(ManagedRoot);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            LoadWorkspaceTabs(selectKey: SelectedEntry?.Key);
            StatusText.Text = $"Registry became unreadable — registration disabled, file preserved. {ex.Message}";
            return;
        }

        try
        {
            var workspace = registry.GetOrRegister(path);
            registry.Save();
            LoadWorkspaceTabs(selectKey: workspace.Slug);
            StatusText.Text = _conversation is not null && workspace.Slug != _connectedKey
                ? $"Workspace '{workspace.Slug}' registered — disconnect from '{_connectedLabel}' to open it."
                : $"Workspace '{workspace.Slug}' ready — session {workspace.SessionId}. Press Connect.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Cannot add workspace: {ex.Message}";
        }
    }

    private void CancelWorkspace_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _pendingWorkspacePath = null;
        PendingConfirmPanel.IsVisible = false;
        StatusText.Text = "Registration cancelled — nothing changed.";
    }

    private async void PastePacket_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard access is unavailable on this platform.";
            return;
        }

        using var transfer = await clipboard.TryGetDataAsync();
        var text = transfer is null ? null : await transfer.TryGetTextAsync();
        if (text is null)
        {
            StatusText.Text = "The clipboard does not contain text.";
            return;
        }

        await ImportAsync(new TranscriptImportRequest(text, "clipboard"));
    }

    private async void LoadSample_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await LoadGeneratedSampleAsync("100k-line sample", () => GenerateLineSample(100_000));

    private async void Load25MiB_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await LoadGeneratedSampleAsync("25 MiB sample", Generate25MiBSample);

    private async void LoadMillionSample_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await LoadGeneratedSampleAsync("1M-line sample", () => GenerateLineSample(1_000_000));

    private async Task LoadGeneratedSampleAsync(string sourceName, Func<string> generator)
    {
        StatusText.Text = $"Preparing {sourceName}…";
        var text = await Task.Run(generator);
        await ImportAsync(new TranscriptImportRequest(text, sourceName));
    }

    private static string GenerateLineSample(int lineCount)
    {
        var builder = new StringBuilder(lineCount * 64);
        for (var i = 0; i < lineCount; i++)
        {
            builder.Append('[').Append(i.ToString("D6")).Append("] sample agent event — full source remains available\n");
        }

        return builder.ToString();
    }

    private static string Generate25MiBSample()
    {
        const int targetBytes = 25 * 1024 * 1024;
        const int charactersPerLine = 256;
        const int lineCount = targetBytes / charactersPerLine;
        var builder = new StringBuilder(targetBytes);

        for (var i = 0; i < lineCount; i++)
        {
            builder.Append('[').Append(i.ToString("D7")).Append("] ").Append('x', 245).Append('\n');
        }

        return builder.ToString();
    }

    private async Task ImportAsync(TranscriptImportRequest request)
    {
        StatusText.Text = $"Indexing {request.SourceName}…";
        var result = await Task.Run(() => TranscriptImporter.Import(request));
        _current = result;
        TranscriptList.ItemsSource = result.Document.Lines;
        StatusText.Text = $"{result.LineCount:N0} lines · {result.Utf8ByteCount:N0} UTF-8 bytes · source: {result.SourceName}";
    }

    private async void CopyFullText_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Paste or load a packet first.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard access is unavailable on this platform.";
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(_current.Document.Text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = $"Copied all {_current.Utf8ByteCount:N0} bytes from the original text.";
    }

    private async void ExportFullText_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_current is null)
        {
            StatusText.Text = "Paste or load a packet first.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export complete transcript",
            SuggestedFileName = "agent-x-transcript.txt",
            FileTypeChoices = [new FilePickerFileType("Text file") { Patterns = ["*.txt"] }]
        });

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(_current.Document.Text);
        StatusText.Text = $"Exported the complete {_current.Utf8ByteCount:N0}-byte source.";
    }

    // --- Pi agent panel: thin UI over the AgentX.Core RPC client. ---

    /// Connect the selected worker: Core verifies identity, probes
    /// capabilities and loads the feed itself — the GUI renders, never
    /// re-validates. Footer and status update only while still selected.
    private async Task WorkerConnectAsync(PiWorker worker)
    {
        var id = worker.Id;
        var label = WorkerViewKey.DisplayLabel(id, worker.Label);
        if (worker.IsConnected)
        {
            if (IsSelectedWorker(id))
            {
                StatusText.Text = $"Worker '{label}' is already connected.";
            }

            return;
        }

        if (!_connectingWorkers.Add(id))
        {
            if (IsSelectedWorker(id))
            {
                StatusText.Text = $"Worker '{label}' is already connecting.";
            }

            return;
        }

        if (IsSelectedWorker(id))
        {
            PiConnectionState.Text = "Connecting…";
        }

        try
        {
            await worker.ConnectAsync();
            UpdateTabDots();
            if (!IsSelectedWorker(id))
            {
                return; // Reader moved on; feed + dots are current.
            }

            PiConnectionState.Text = "Connected";
            var count = worker.History.Count;
            RebuildConversationList();
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            PiRunStatus.Text = count == 0
                ? $"New worker session for '{label}' — no previous conversation."
                : $"Worker '{label}' connected — restored {count} message(s). Send a prompt to continue.";
            StatusText.Text = $"Pi runs in {worker.ProjectRoot} with your user authority.";
            RefreshDirectoryLabel();
            RefreshConnectionButtons();
            RefreshEngineSegments();
        }
        catch (Exception ex)
        {
            UpdateTabDots();
            if (!IsSelectedWorker(id))
            {
                return;
            }

            PiConnectionState.Text = "Connection failed";
            StatusText.Text = $"Worker '{label}' connection failed: {ex.Message} — nothing connected.";
            RefreshConnectionButtons();
            RefreshEngineSegments();
        }
        finally
        {
            _connectingWorkers.Remove(id);
        }
    }

    /// Reconnect the selected worker after transport death: Core swaps the
    /// process, keeps the session and records the restore notice in the
    /// feed itself. Never-connected workers are told to connect first.
    private async Task WorkerReconnectAsync(PiWorker worker)
    {
        var id = worker.Id;
        var label = WorkerViewKey.DisplayLabel(id, worker.Label);
        if (_busyWorkers.Contains(id))
        {
            if (IsSelectedWorker(id))
            {
                StatusText.Text = $"Worker '{label}' has a run in progress — cancel it before reconnecting.";
            }

            return;
        }

        if (worker.IsConnected)
        {
            if (IsSelectedWorker(id))
            {
                StatusText.Text = $"Worker '{label}' is already connected — nothing to reconnect.";
            }

            return;
        }

        if (!_connectingWorkers.Add(id))
        {
            if (IsSelectedWorker(id))
            {
                StatusText.Text = $"Worker '{label}' is already connecting.";
            }

            return;
        }

        if (IsSelectedWorker(id))
        {
            PiConnectionState.Text = "Reconnecting…";
        }

        try
        {
            await worker.ReconnectAsync();
            UpdateTabDots();
            if (!IsSelectedWorker(id))
            {
                return;
            }

            PiConnectionState.Text = "Connected";
            RebuildConversationList();
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            MaybeAutoScroll();
            PiRunStatus.Text = $"Worker '{label}' reconnected — session intact.";
            RefreshConnectionButtons();
            RefreshEngineSegments();
        }
        catch (InvalidOperationException)
        {
            // Never connected: Core refuses the swap. Connect first.
            UpdateTabDots();
            if (IsSelectedWorker(id))
            {
                PiConnectionState.Text = "Disconnected";
                StatusText.Text = $"Worker '{label}' never connected — press Connect first.";
            }
        }
        catch (Exception ex)
        {
            UpdateTabDots();
            if (!IsSelectedWorker(id))
            {
                return;
            }

            PiConnectionState.Text = "Reconnect failed";
            PiRunStatus.Text = $"Worker '{label}' reconnect failed: {ex.Message} — session is persisted; press Reconnect to retry.";
            RefreshConnectionButtons();
        }
        finally
        {
            _connectingWorkers.Remove(id);
        }
    }

    private async void ConnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Worker tabs connect the selected worker only — independent of any
        // workspace connection, which keeps running untouched.
        if (SelectedWorker is { } worker)
        {
            await WorkerConnectAsync(worker);
            return;
        }

        // One controlled transition: while a conversation object exists
        // (connected OR dead) only Disconnect or Reconnect may change state.
        // Otherwise a failed transport plus Connect would strand the old
        // conversation — undisposed, still subscribed — beside a new one.
        if (_conversation is not null)
        {
            if (_conversation.IsConnected && SelectedEntry?.Key == _connectedKey)
            {
                return;
            }

            StatusText.Text = "Disconnect before switching workspaces or reconnecting — histories never mix.";
            return;
        }

        if (_connecting || _piBusy)
        {
            StatusText.Text = "A connection or run is already underway.";
            return;
        }

        if (SelectedEntry is not { } choice)
        {
            StatusText.Text = "Select a workspace first.";
            return;
        }

        var workspace = choice.ToWorkspace(ManagedRoot);
        if (!choice.IsLegacy && !Directory.Exists(workspace.ProjectRoot))
        {
            StatusText.Text = $"Project folder unavailable: {workspace.ProjectRoot} — refusing to connect elsewhere.";
            return;
        }

        _connecting = true;
        PiConnectionState.Text = "Connecting…";
        // The factory captures each fresh client for the observability bridge
        // (initial connect AND every reconnect swap) without touching Core.
        PiRpcClient? liveClient = null;
        var conversation = new PiConversation(() =>
        {
            liveClient = new PiRpcClient(workspace.ToPiRpcOptions());
            _bridge.Detach();
            _bridge.Attach(liveClient);
            return liveClient;
        });
        conversation.ActivityChanged += Conversation_ActivityChanged;
        conversation.ToolActivityChanged += Conversation_ToolActivityChanged;
        conversation.ConnectionLost += Conversation_ConnectionLost;
        try
        {
            // This workspace's Pi-native session, resumed by stable session id.
            var state = await conversation.ConnectAsync();
            // Runtime identity check BEFORE showing a restore: the reported
            // session must be the one requested, never a foreign history.
            workspace.ValidateSessionMatch(state.SessionId);
            _conversation = conversation;
            _connectedKey = choice.Key;
            _connectedLabel = choice.Label;
            _connectedWorkspace = workspace;
            _coordinator.NoteConnected(choice.Key);
            PiConnectionState.Text = "Connected";
            NoteEngineState(state);
            var history = conversation.History;
            _feed.LoadHistory(history);
            _coordinator.RefreshMemory();
            _feed.SetActivity(null);
            RebuildConversationList();
            RebuildSessionMap();
            UpdateTabDots();
            RefreshOverviewAndSettings();
            PiRunStatus.Text = history.Count == 0
                ? $"New session for '{choice.Label}' — no previous conversation."
                : $"Restored '{choice.Label}' with {history.Count} message(s). Send a prompt to continue.";
            StatusText.Text = $"Pi runs in {workspace.WorkingDirectory} with your user authority.";
            RefreshDirectoryLabel();
        }
        catch (Exception ex)
        {
            conversation.ActivityChanged -= Conversation_ActivityChanged;
            conversation.ToolActivityChanged -= Conversation_ToolActivityChanged;
            conversation.ConnectionLost -= Conversation_ConnectionLost;
            _bridge.Detach();
            await conversation.DisposeAsync();
            PiConnectionState.Text = "Connection failed";
            StatusText.Text = $"Connection failed: {ex.Message} — nothing connected.";
            ClearEngineState();
        }
        finally
        {
            _connecting = false;
        }
    }

    private async void DisconnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Workers have no disconnect in Core (dispose or process death ends
        // them): removal is the honest stop and keeps session files on disk.
        // The button is also disabled for worker tabs; this guards the path.
        if (SelectedWorker is { } worker)
        {
            StatusText.Text = $"Worker '{WorkerViewKey.DisplayLabel(worker.Id, worker.Label)}' has no disconnect — Remove worker stops its process.";
            return;
        }

        await DisconnectPiAsync();
    }

    private async void ReconnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Worker tabs reconnect the selected worker only — Core swaps the
        // process, keeps the session and preserves the feed itself.
        if (SelectedWorker is { } worker)
        {
            await WorkerReconnectAsync(worker);
            return;
        }

        var conversation = _conversation;
        if (conversation is null)
        {
            StatusText.Text = "Connect to Pi first — nothing to reconnect.";
            return;
        }

        if (_piBusy)
        {
            StatusText.Text = "A run is in progress — cancel it before reconnecting.";
            return;
        }

        if (_connecting)
        {
            StatusText.Text = "A connection is already underway.";
            return;
        }

        if (conversation.IsConnected)
        {
            StatusText.Text = "Already connected — nothing to reconnect.";
            return;
        }

        _connecting = true;
        PiConnectionState.Text = "Reconnecting…";
        // Owner lock: reconnect restores the workspace this conversation was
        // opened for, even if the picker selection moved meanwhile. A selection
        // change mid-reconnect can neither redirect nor falsely fail it.
        var owner = _connectedWorkspace;
        if (owner is null)
        {
            _connecting = false;
            StatusText.Text = "No workspace owns this connection — disconnect and reconnect.";
            return;
        }

        try
        {
            var state = await conversation.ReconnectAsync();
            owner.ValidateSessionMatch(state.SessionId);

            PiConnectionState.Text = "Connected";
            NoteEngineState(state);
            // The feed keeps whatever it already holds (including any
            // preserved partial text); the notice records the restore truth.
            _feed.AddSystemNotice(
                $"Reconnected — restored {conversation.History.Count} session message(s) (reconnect #{conversation.ReconnectCount}). Partial streamed text above is preserved as-is.");
            _feed.SetActivity(null);
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            MaybeAutoScroll();
            PiRunStatus.Text = $"Reconnected to '{_connectedLabel ?? "Pi"}' — session intact.";
        }
        catch (Exception ex)
        {
            PiConnectionState.Text = "Reconnect failed";
            PiRunStatus.Text = $"Reconnect failed: {ex.Message} — session is persisted; press Reconnect to retry.";
        }
        finally
        {
            _connecting = false;
        }
    }

    private bool IsSelectedWorker(string id) => SelectedWorker?.Id == id;

    /// Send on the selected worker only. Core creates and closes the turn
    /// (AddUser/BeginAssistantTurn/Reconcile/EndAssistantTurn all inside
    /// worker.SendAsync) — the GUI must not duplicate any of it. Progress
    /// streams through a run-local sink for the status line; Core already
    /// appended every delta to the worker feed on the RPC thread.
    private async Task WorkerSendAsync(PiWorker worker)
    {
        var id = worker.Id;
        if (_busyWorkers.Contains(id) || _connectingWorkers.Contains(id))
        {
            return; // One run per worker; a second send would throw in Core.
        }

        if (!worker.IsConnected)
        {
            if (IsSelectedWorker(id))
            {
                PiRunStatus.Text = "Connect to Pi first.";
            }

            return;
        }

        var message = PromptInput.Prepare(PiPromptBox.Text);
        if (message is null)
        {
            if (IsSelectedWorker(id))
            {
                PiRunStatus.Text = "Type or paste a prompt first — empty prompts are not sent.";
            }

            return;
        }

        var sink = new RunProgressSink();
        _runSinks[id] = sink;
        _runSeqs[id] = worker.Feed.Snapshot().LastOrDefault()?.Seq ?? -1;
        _busyWorkers.Add(id);
        IProgress<string> progress = sink;
        if (IsSelectedWorker(id))
        {
            PiConnectionState.Text = "Working…";
        }

        RefreshWorkingRow();

        if (!_flushTimer.IsEnabled)
        {
            _flushTimer.Start();
        }

        UpdateTabDots();
        try
        {
            var (text, receipt) = await worker.SendAsync(message, progress);
            if (!IsSelectedWorker(id))
            {
                return; // Reader moved on: feed + dots are current; footer stays theirs.
            }

            PiConnectionState.Text = "Idle";
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            PiRunStatus.Text = $"Last run: {receipt.Display} ({receipt.AssistantChars:N0} chars in {sink.Accumulator.FlushCount:N0} UI updates, {worker.Feed.Count} feed entries.)";
            StatusText.Text = receipt.RunHealthy
                ? $"Pi replied with {text.Length:N0} characters — healthy run (correctness needs separate acceptance checks)."
                : $"Pi run finished — {receipt.Display}";
        }
        catch (OperationCanceledException)
        {
            // Core already closed the turn and kept partial text in the
            // feed; report the cancel truthfully, claim no health signal.
            if (!IsSelectedWorker(id))
            {
                return;
            }

            PiConnectionState.Text = "Cancelled";
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            PiRunStatus.Text = "Last run: cancelled — run did not complete and earned no health signal.";
            RebuildSessionMap();
            RefreshOverviewAndSettings();
        }
        catch (Exception ex)
        {
            // Core already closed the turn and recorded the failure in the
            // feed; report transport loss truthfully, anything else idle.
            if (!IsSelectedWorker(id))
            {
                return;
            }

            if (ex is PiRpcTransportException)
            {
                PiConnectionState.Text = "Connection lost";
                PiRunStatus.Text = $"Connection to Pi lost: {ex.Message} — session is persisted. Press Reconnect to restore.";
            }
            else
            {
                PiConnectionState.Text = "Idle";
                PiRunStatus.Text = $"Run failed: {ex.Message}";
            }

            RebuildSessionMap();
            RefreshOverviewAndSettings();
        }
        finally
        {
            sink.Close(); // Retire this run: late callbacks drop, never reroute.
            _runSinks.Remove(id);
            _runSeqs.Remove(id);
            _busyWorkers.Remove(id);
            RefreshWorkingRow();
            StopFlushTimerIfIdle();
            UpdateTabDots();
        }
    }

    /// Cancel the selected worker's run only. Core owns the cancellation
    /// (its wait plus best-effort abort) — no GUI lifetime, no other worker.
    private async Task WorkerCancelAsync(PiWorker worker)
    {
        var id = worker.Id;
        if (!_busyWorkers.Contains(id))
        {
            return;
        }

        if (IsSelectedWorker(id))
        {
            PiConnectionState.Text = "Cancelling…";
        }

        await worker.CancelAsync();
    }

    private async void SendPrompt_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Worker tabs send on the selected worker only — byte-identical
        // prompt content via the same composer. Never the workspace path.
        if (SelectedWorker is { } worker)
        {
            await WorkerSendAsync(worker);
            return;
        }

        if (_piBusy)
        {
            return;
        }

        if (_conversation?.IsConnected != true)
        {
            PiRunStatus.Text = "Connect to Pi first.";
            return;
        }

        var message = PromptInput.Prepare(PiPromptBox.Text);
        if (message is null)
        {
            PiRunStatus.Text = "Type or paste a prompt first — empty prompts are not sent.";
            return;
        }

        _piBusy = true;
        PiConnectionState.Text = "Working…";
        RefreshWorkingRow();
        UpdateTabDots();

        _feed.AddUser(message);
        _liveSeq = _feed.BeginAssistantTurn();
        _promptLifetime = new CancellationTokenSource();
        _flushTimer.Start();
        // Run-local sink (not a closure over a mutable field): deltas buffer
        // here with no UI work per delta, and Close retires the run so any
        // late callback is dropped instead of bleeding into the next run.
        var sink = new RunProgressSink();
        _liveAccumulator = sink.Accumulator;
        IProgress<string> progress = sink;

        try
        {
            var (text, receipt) = await _conversation.SendAsync(message, progress, _promptLifetime.Token);
            StopFlushTimerIfIdle();
            // Authoritative reconcile: the returned text wins over the
            // progressively flushed chunks (same deltas, race-free).
            _feed.ReconcileStreamingText(_liveSeq, text);
            _feed.ReconcileToolResults(_conversation.History);
            _feed.EndAssistantTurn(_liveSeq, receipt);
            // Settle first, render second: the map must never show a stale
            // "working" state once Pi is idle (EndAssistantTurn also clears
            // activity in Core as a backstop).
            PiConnectionState.Text = "Idle";
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null; // Tick applies the stash: clear it too.
            _feed.SetActivity(null);
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            var updates = _liveAccumulator?.FlushCount ?? 0;
            PiRunStatus.Text = $"Last run: {receipt.Display} ({receipt.AssistantChars:N0} chars in {updates:N0} UI updates, {_feed.Count} feed entries.)";
            StatusText.Text = receipt.RunHealthy
                ? $"Pi replied with {text.Length:N0} characters — healthy run (correctness needs separate acceptance checks)."
                : $"Pi run finished — {receipt.Display}";
        }
        catch (OperationCanceledException)
        {
            StopFlushTimerIfIdle();
            FlushLive(); // Keep whatever streamed before cancellation — never drop partial work.
            _feed.EndAssistantTurn(_liveSeq, null);
            _feed.AddSystemNotice("Run cancelled — partial streamed text above is kept as-is. History refreshes on next connect.");
            _feed.SetActivity(null);
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            PiConnectionState.Text = "Cancelled";
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            PiRunStatus.Text = "Last run: cancelled — run did not complete and earned no health signal. History refreshes on next connect.";
            if (_conversation is not null)
            {
                try
                {
                    await _conversation.RefreshHistoryAsync();
                }
                catch (Exception)
                {
                    // Best effort: the next connect restores history regardless.
                }
            }
        }
        catch (PiRpcCommandException ex)
        {
            StopFlushTimerIfIdle();
            _feed.EndAssistantTurn(_liveSeq, null);
            _feed.AddSystemNotice($"Pi rejected the prompt: {ex.Error} — partial streamed text above is kept as-is.");
            _feed.SetActivity(null);
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            PiConnectionState.Text = "Idle";
        }
        catch (PiRpcTransportException ex)
        {
            StopFlushTimerIfIdle();
            // Recorded as an error EVENT, never a synthetic receipt: the run
            // did not settle and earns no health signal either way.
            _feed.AddError($"Connection to Pi lost: {ex.Message}");
            _feed.SetActivity(null);
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            // The conversation is kept (dead process inside) for ReconnectAsync;
            // explicit Disconnect tears it down. The loss event below dedupes
            // against this entry, so one failure stays one entry.
            PiConnectionState.Text = "Connection lost";
            PiRunStatus.Text = $"Connection to Pi lost: {ex.Message} — session is persisted. Press Reconnect to restore.";
        }
        finally
        {
            sink.Close(); // Retire this run: late callbacks drop, never reroute.
            if (ReferenceEquals(_liveAccumulator, sink.Accumulator))
            {
                _liveAccumulator = null;
            }

            _piBusy = false;
            _liveSeq = -1;
            RefreshWorkingRow();
            UpdateTabDots();
            _promptLifetime?.Dispose();
            _promptLifetime = null;
            StopFlushTimerIfIdle();
        }
    }

    private void PiPromptBox_OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Predictable composer keys: Enter inserts a newline (multiline),
        // Ctrl+Enter sends exactly what is in the box. No other chords act.
        // Wired to PreviewKeyDown (tunnel): the TextBox consumes Enter in
        // its own class handling, so the bubble-phase KeyDown never sees
        // the chord reliably.
        if (e.Key == Avalonia.Input.Key.Enter
            && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            e.Handled = true;
            SendPrompt_OnClick(sender, new Avalonia.Interactivity.RoutedEventArgs());
        }

        // Escape interrupts the active run only: an idle Escape must not
        // touch the draft (no clear, no send). Cancellation stays truthful:
        // "Cancelling…" now, "Cancelled" only once Pi confirms or the
        // pending operation terminates in the existing handler below.
        // Escape interrupts the selected run only: a worker run, else the
        // workspace run. An idle Escape must not touch the draft.
        if (e.Key == Avalonia.Input.Key.Escape && (SelectedWorker?.Id is { } busyId ? _busyWorkers.Contains(busyId) : _piBusy))
        {
            e.Handled = true;
            CancelPrompt_OnClick(sender, new Avalonia.Interactivity.RoutedEventArgs());
        }
    }

    private async void CancelPrompt_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Worker tabs cancel the selected worker's run only — Core owns its
        // cancellation; no other worker is touched.
        if (SelectedWorker is { } worker)
        {
            await WorkerCancelAsync(worker);
            return;
        }

        if (!_piBusy)
        {
            return;
        }

        PiConnectionState.Text = "Cancelling…";
        await (_promptLifetime?.CancelAsync() ?? Task.CompletedTask);
        if (_conversation?.IsConnected == true)
        {
            try
            {
                await _conversation.AbortAsync();
            }
            catch (Exception)
            {
                // Cancellation already requested; the pending wait observes it.
            }
        }
    }

    /// Composer context menu (mouse right-click or Shift+F10/Menu key): the
    /// only visible Send/Stop surface. Both items reuse the single tested
    /// execution path above — no second implementation. Standard edit items
    /// are kept so the menu replaces nothing the mouse had. Focus returns to
    /// the box so the editor stays predictable before/during/after a run.
    /// Fires for mouse right-click AND keyboard menu keys. Computes Send/Stop
    /// enablement once for both, then shows the flyout at the CONTROL (not
    /// the pointer) so it flips above the composer with every item visible.
    private void ComposerMenu_OnContextRequested(object? sender, Avalonia.Input.ContextRequestedEventArgs e)
    {
        // Enablement follows the VIEWED tab: a worker's connection and run,
        // else the workspace connection and run. B4 chords preserved.
        var (connected, busy) = SelectedWorker is { } worker
            ? (worker.IsConnected, _busyWorkers.Contains(worker.Id))
            : (_conversation?.IsConnected == true, _piBusy);
        ComposerSendItem.IsEnabled = ComposerActionPolicy.CanSend(connected, busy, PiPromptBox.Text);
        ComposerStopItem.IsEnabled = ComposerActionPolicy.CanStop(busy);
        // The flyout root is not in the XAML namescope: resolve it from the
        // box it is attached to.
        if (PiPromptBox.ContextFlyout is MenuFlyout menu)
        {
            menu.ShowAt(PiPromptBox);
        }

        e.Handled = true;
    }

    private void ComposerCut_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PiPromptBox.Cut();

    private void ComposerCopy_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PiPromptBox.Copy();

    private void ComposerPaste_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PiPromptBox.Paste();

    private void ComposerSelectAll_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PiPromptBox.SelectAll();

    private void ComposerSend_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SendPrompt_OnClick(sender, e);
        PiPromptBox.Focus();
    }

    private void ComposerStop_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CancelPrompt_OnClick(sender, e);
        PiPromptBox.Focus();
    }

    // --- Conversation feed view: bounded display over exact Core text. ---

    /// <summary>
    /// Single incremental path from feed mutations to view rows. Appends new
    /// entries, refreshes rows whose Core text changed (tool close,
    /// reconcile), and builds settled assistant Markdown once. Per-delta
    /// streaming refreshes the live row only. Never rebuilds the list.
    /// </summary>
    private void SyncFeedView()
    {
        _syncRuns++;
        var feed = ActiveFeed;
        var snapshot = feed.Snapshot();
        var bySeq = new Dictionary<long, ConversationViewItem>(_conversationItems.Count);
        foreach (var existing in _conversationItems)
        {
            bySeq[existing.Seq] = existing;
        }

        var follow = false;
        // Structural = anything derived views must re-read (new rows, tool
        // closes, reconciles). Pure streaming growth skips the map/overview
        // rebuild: the tick already shows the text and the live activity.
        var structural = false;
        foreach (var entry in snapshot)
        {
            if (!bySeq.TryGetValue(entry.Seq, out var item))
            {
                if (entry.Seq <= _shownSeq)
                {
                    continue; // Reset path owns older seqs; never resurrect.
                }

                item = ToViewItem(entry);
                _conversationItems.Add(item);
                bySeq[entry.Seq] = item;
                _shownSeq = Math.Max(_shownSeq, entry.Seq);
                follow = true;
                structural = true;
            }
            else if (!ReferenceEquals(entry.Text, item.FullText)
                // A settled assistant turn keeps the SAME text reference (only
                // IsStreaming flips): without this clause its Markdown never
                // parses until the next full rebuild. Refresh then too.
                || (item.Kind == FeedBlockKind.Assistant && !entry.IsStreaming && item.Markdown is null))
            {
                // Core text replaced under a live row (tool close, reconcile,
                // streaming chunks, completion reconcile). Refresh in place.
                RefreshItem(item, entry);
                if (IsLiveRow(entry.Seq))
                {
                    follow = true;
                }

                if (!entry.IsStreaming)
                {
                    structural = true;
                }
            }
        }

        PiActivityText.Text = _pendingActivityText ?? string.Empty;
        if (structural)
        {
            // RefreshOverviewAndSettings rebuilds the map text itself.
            RefreshOverviewAndSettings();
        }

        if (follow)
        {
            MaybeAutoScroll();
        }
    }

    private static void RefreshItem(ConversationViewItem item, FeedEntry entry)
    {
        var cap = entry.IsStreaming ? LiveDisplayCap : HistoryDisplayCap;
        item.FullText = entry.Text;
        item.Header = BuildHeader(entry, item.Kind);
        if (item.Kind == FeedBlockKind.Tool)
        {
            item.StateBrush = ToolStateBrush(entry);
            item.EdgeBrush = ToolEdgeBrush(entry);
            item.RefreshToolDisplay();
        }
        var body = entry.IsStreaming && entry.Text.Length == 0 ? "Working…" : entry.Text;
        item.DisplayText = item.IsExpanded ? FeedEntry.TruncateExpanded(entry.Text) : FeedEntry.TruncateForDisplay(body, cap);
        if (item.Kind == FeedBlockKind.Assistant && !entry.IsStreaming && entry.Text.Length > 0 && item.Markdown is null)
        {
            item.Markdown = MarkdownLite.Parse(entry.Text);
        }
        // Rows created before the user-parse path (or skipped while empty)
        // get the same bounded rich render on the next sync. Parse-once:
        // user entry text is immutable after arrival, so DisplayText is
        // stable and this never re-parses on every tick.
        if (item.Kind == FeedBlockKind.User && item.Markdown is null && item.DisplayText.Length > 0)
        {
            item.Markdown = MarkdownLite.Parse(item.DisplayText);
        }
    }

    /// <summary>Visibility preference to bridge level (Detailed default).</summary>
    private void ApplyVisibility()
    {
        _bridge.Level = _prefs.Visibility switch
        {
            "Compact" => VisibilityLevel.Compact,
            "Full" => VisibilityLevel.Full,
            _ => VisibilityLevel.Detailed,
        };

        // Workers own their bridges in Core: push the same level to each.
        if (_manager is not null)
        {
            var level = _bridge.Level;
            foreach (var worker in _manager.Workers)
            {
                worker.Visibility = level;
            }
        }
    }

    /// Single bounded entry from feed Changed (any thread): at most one
    /// sync is ever queued; the run observes the latest snapshot.
    private void FeedChanged()
    {
        // The workspace feed changed while a worker tab is selected: the
        // background run keeps its feed, the view stays on the worker.
        // Fires on the RPC thread: read the mirrored id, not the control.
        if (_selectedWorkerId is not null)
        {
            return;
        }

        _syncCoalescer.Signal(SyncFeedView);
    }

    // B7 treatments: row backgrounds are the SHARED AxTheme brushes, so a
    // treatment switch repaints live rows in place (Color setters propagate).
    private static Avalonia.Media.IBrush UserBlockBrush => AxTheme.UserBgBrush;
    private static Avalonia.Media.IBrush ToolPendingBrush => AxTheme.ToolPendingBgBrush;
    private static Avalonia.Media.IBrush ToolSuccessBrush => AxTheme.ToolSuccessBgBrush;
    private static Avalonia.Media.IBrush ToolErrorBrush => AxTheme.ToolErrorBgBrush;
    private static readonly Avalonia.Media.IBrush ClearBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00000000"));

    private static ConversationViewItem ToViewItem(FeedEntry entry)
    {
        var kind = KindOf(entry);
        var body = entry.IsStreaming && entry.Text.Length == 0 ? "Working…" : entry.Text;
        var cap = entry.IsStreaming ? LiveDisplayCap : HistoryDisplayCap;
        var brush = kind switch
        {
            FeedBlockKind.User => UserBlockBrush,
            FeedBlockKind.Tool => ToolStateBrush(entry),
            _ => ClearBrush,
        };
        var item = new ConversationViewItem(
            entry.Seq,
            entry.Role,
            BuildHeader(entry, kind),
            FeedEntry.TruncateForDisplay(body, cap),
            entry.Text,
            kind,
            entry.IsStreaming,
            brush,
            cap);
        item.EdgeBrush = kind switch
        {
            FeedBlockKind.User => AxTheme.UserEdgeBrush,
            FeedBlockKind.Tool => ToolEdgeBrush(entry),
            _ => ClearBrush,
        };
        // A burst that settles before the first sync (reconcile, fixture,
        // back-to-back run completion) is first SEEN settled: parse its
        // Markdown here, or it stays raw until an unrelated later sync.
        if (kind == FeedBlockKind.Assistant && !entry.IsStreaming && entry.Text.Length > 0)
        {
            item.Markdown = MarkdownLite.Parse(entry.Text);
        }
        // User packets render through the same safe parser (Lucy B7 review):
        // parsed from the BOUNDED display text, so giant pastes keep today's
        // cap contract and layout cost. Copy stays byte-exact from FullText
        // via the Core feed — never from this rendered view.
        if (kind == FeedBlockKind.User && entry.Text.Length > 0 && item.DisplayText.Length > 0)
        {
            item.Markdown = MarkdownLite.Parse(item.DisplayText);
        }
        return item;
    }

    private static FeedBlockKind KindOf(FeedEntry entry)
        => entry.Role switch
        {
            "user" => FeedBlockKind.User,
            "assistant" => FeedBlockKind.Assistant,
            "tool" => FeedBlockKind.Tool,
            "receipt" => FeedBlockKind.Receipt,
            _ => FeedBlockKind.System,
        };

    private static string BuildHeader(FeedEntry entry, FeedBlockKind kind)
    {
        var label = entry.Role switch
        {
            "user" => "You",
            "assistant" => "Pi",
            "tool" => "Tool",
            "receipt" => "Run receipt",
            _ => entry.Role,
        };
        return entry.Note is null
            ? $"[{label} #{entry.Seq}]"
            : $"[{label} #{entry.Seq}] {entry.Note}";
    }

    /// <summary>One coherent tool-operation appearance: pending grey while
    /// open, green on success, red on failure. Read from the lifecycle note
    /// and error flag the feed maintains.</summary>
    private static Avalonia.Media.IBrush ToolStateBrush(FeedEntry entry)
    {
        if (entry.IsError)
        {
            return ToolErrorBrush;
        }

        return entry.Note is not null && entry.Note.StartsWith("started:", StringComparison.Ordinal)
            ? ToolPendingBrush
            : ToolSuccessBrush;
    }

    /// 3px state edge for the B7 tool surfaces: Pi semantic hues, shared
    /// instances so treatment switches propagate (see UserBlockBrush).
    private static Avalonia.Media.IBrush ToolEdgeBrush(FeedEntry entry)
    {
        if (entry.IsError)
        {
            return AxTheme.RedBrush;
        }

        return entry.Note is not null && entry.Note.StartsWith("started:", StringComparison.Ordinal)
            ? AxTheme.DimBrush
            : AxTheme.GreenBrush;
    }

    /// <summary>Refresh visible derived views after connection/feed changes.</summary>
    private void RefreshOverviewAndSettings()
    {
        UpdateTabDots();
        if (OverviewPanel.IsVisible)
        {
            RebuildOverview();
        }

        if (SettingsPanel.IsVisible)
        {
            RefreshSettings();
        }
    }

    private void RebuildConversationList()
    {
        _conversationItems.Clear();
        _shownSeq = 0;
        foreach (var entry in ActiveFeed.Snapshot())
        {
            _conversationItems.Add(ToViewItem(entry));
            _shownSeq = Math.Max(_shownSeq, entry.Seq);
        }

        // Settled assistant rows get rich Markdown here (streaming rows get
        // theirs when they settle, via SyncFeedView).
        foreach (var item in _conversationItems)
        {
            if (item.Kind == FeedBlockKind.Assistant && item.Markdown is null && item.FullText.Length > 0)
            {
                item.Markdown = MarkdownLite.Parse(item.FullText);
            }
        }

        if (_conversationItems.Count > 0)
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }

        UpdateEmptyState();
    }

    private void MaybeAutoScroll()
    {
        if (AutoScrollMenuItem.IsChecked == true && _conversationItems.Count > 0)
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }
    }

    /// Stop the flush tick only when nothing streams anywhere: concurrent
    /// worker runs share the tick with the workspace run.
    private void StopFlushTimerIfIdle()
    {
        if (_busyWorkers.Count == 0 && !_piBusy)
        {
            _flushTimer.Stop();
        }
    }

    /// <summary>Move buffered deltas into the feed (10 Hz, not per delta).
    /// The feed's Changed post drives SyncFeedView; no direct UI work here.</summary>
    private void FlushLive()
    {
        // Worker sinks: Core already appended every delta to the worker feed
        // on the RPC thread, so flushing here only bounds the buffers and
        // keeps lifetime totals for the status line. No feed writes from
        // the GUI on the worker path — never duplicate Core turns.
        foreach (var sink in _runSinks.Values)
        {
            sink.Accumulator.Flush();
        }

        if (SelectedWorker is { } worker)
        {
            if (_runSinks.TryGetValue(worker.Id, out var running))
            {
                StatusText.Text = $"Streaming… {running.Accumulator.CommittedChars:N0} chars in {running.Accumulator.FlushCount:N0} UI updates.";
            }

            return;
        }

        var accumulator = _liveAccumulator;
        if (accumulator is null || _liveSeq < 0)
        {
            return;
        }

        var chunk = accumulator.Flush();
        if (chunk.Length == 0)
        {
            return;
        }

        _feed.AppendDelta(_liveSeq, chunk);
        StatusText.Text = $"Streaming… {accumulator.CommittedChars:N0} chars in {accumulator.FlushCount:N0} UI updates.";
    }

    private void RebuildSessionMap()
    {
        SessionMapText.Text = ActiveFeed.BuildMap().ToDisplayText(_prefs.Visibility != "Compact");
    }

    private void ToolExpand_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Explicit user action reveals the retained text (or folds it back
        // to the bounded preview). Inline expansion itself is bounded
        // (100k-line outputs would otherwise freeze rendering); the notice
        // carries exact totals and copy always uses FullText regardless.
        if ((sender as Button)?.DataContext is not ConversationViewItem item)
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;
        if (item.IsExpanded)
        {
            item.DisplayText = FeedEntry.TruncateExpanded(item.FullText);
        }
        else if (ActiveFeed.GetEntryText(item.Seq) is { } current)
        {
            var entry = ActiveFeed.Snapshot().FirstOrDefault(x => x.Seq == item.Seq);
            var cap = entry?.IsStreaming == true ? LiveDisplayCap : HistoryDisplayCap;
            item.DisplayText = FeedEntry.TruncateForDisplay(current, cap);
        }
    }

    // --- Pages, Overview, Settings, empty state. ---

    private void ShowPage(string name)
    {
        var page = name switch
        {
            "Overview" => AppPage.Overview,
            "Settings" => AppPage.Settings,
            _ => AppPage.Live,
        };
        _page = page;
        _syncingRail = true;
        try
        {
            NavLive.IsChecked = page == AppPage.Live;
            NavOverview.IsChecked = page == AppPage.Overview;
            NavSettings.IsChecked = page == AppPage.Settings;
        }
        finally
        {
            _syncingRail = false;
        }

        SyncRailSelection();

        ApplyPageVisibility((int)page);
    }

    /// Rail selection is painted explicitly — never left to the Fluent
    /// checked style, which follows the Windows accent colour. The selected
    /// destination gets the surface pill; the others stay transparent.
    private void SyncRailSelection()
    {
        NavLive.Background = _page == AppPage.Live ? AxTheme.Surface2Brush : Avalonia.Media.Brushes.Transparent;
        NavOverview.Background = _page == AppPage.Overview ? AxTheme.Surface2Brush : Avalonia.Media.Brushes.Transparent;
        NavSettings.Background = _page == AppPage.Settings ? AxTheme.Surface2Brush : Avalonia.Media.Brushes.Transparent;
    }

    private void Nav_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_syncingRail)
        {
            return;
        }

        // ToggleButtons flip before the click lands; ShowPage re-syncs the
        // group, so the active page can never be left unchecked. Page
        // switches are presentation-only: feed, selection, draft and any
        // background run are untouched.
        if (sender == NavOverview)
        {
            ShowPage("Overview");
        }
        else if (sender == NavSettings)
        {
            ShowPage("Settings");
        }
        else
        {
            ShowPage("Live");
        }
    }

    private void NavCollapse_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewPrefs.RailExpanded = !_viewPrefs.RailExpanded;
        ApplyRailWidth();
        try
        {
            SaveViewPrefs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save view preferences: {ex.Message}";
        }
    }

    /// <summary>
    /// Rail geometry: 56px icon rail collapsed (the default — transcript
    /// keeps the room), 180px with labels expanded. Instant switch, no
    /// animation, never on hover. Expansion never touches the feed or any
    /// live process; it only reclaims label space from the centre.
    /// </summary>
    private void ApplyRailWidth()
    {
        var expanded = _viewPrefs.RailExpanded;
        NavRail.Width = expanded ? 180 : 56;
        NavLiveLabel.IsVisible = expanded;
        NavOverviewLabel.IsVisible = expanded;
        NavSettingsLabel.IsVisible = expanded;
        NavCollapseLabel.IsVisible = expanded;
        NavCollapseLabel.Text = expanded ? "Collapse" : "Expand";
        NavCollapseGlyph.Text = expanded ? "\u00AB" : "\u00BB";
        Avalonia.Controls.ToolTip.SetTip(NavCollapse,
            expanded ? "Collapse navigation" : "Expand navigation");
        NavCollapse.SetValue(Avalonia.Automation.AutomationProperties.NameProperty,
            expanded ? "Collapse navigation" : "Expand navigation");
    }

    /// Persist view preferences. Prunes drafts ONLY against healthy sources:
    /// a degraded catalog or an unavailable worker store must never delete
    /// drafts for temporarily unknown tabs. Throws on IO failure (callers
    /// keep memory and mark a persist retry); returns silently when
    /// preferences recovery disabled persistence (the startup status stands).
    private void SaveViewPrefs()
    {
        var catalogHealthy = _catalog is not null && !_catalog.IsRecovery;
        var healthy = catalogHealthy && _manager is not null;
        _viewPrefs.Save(
            ManagedRoot,
            knownKeys: healthy
                ? WorkerViewKey.MergeKnownKeys(
                    _catalog!.Entries.Select(e => e.Key),
                    _manager!.Workers.Select(w => w.Id))
                : null,
            pruneDrafts: healthy);
    }

    private void ApplyPageVisibility(int index)
    {
        OverviewPanel.IsVisible = index == 0;
        LivePanel.IsVisible = index == 1;
        SettingsPanel.IsVisible = index == 2;
        if (index == 0)
        {
            RebuildOverview();
        }
        else if (index == 2)
        {
            RefreshSettings();
        }
    }

    /// <summary>
    /// Overview shows only observed worker facts: identity, connection,
    /// session file, map-derived counts and milestones. Nothing invented;
    /// model/thinking show the last confirmed values (selection pending).
    private void RebuildWorkerOverview(PiWorker worker)
    {
        var label = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
        WorkspaceDetailText.Text = WorkerViewKey.Tooltip(worker.Id, worker.Label, worker.SessionId, worker.ProjectRoot);
        var state = worker.LastConfirmedState;
        OverviewConnectionText.Text = worker.IsConnected && state is not null
            ? $"Worker '{label}' connected. {EngineSummary()}"
            : $"Worker '{label}' not connected. Press Connect on the Live page.";
        var sessionFile = state?.SessionFile;
        OverviewSessionFileText.Text = sessionFile is not null && File.Exists(sessionFile)
            ? $"{sessionFile} ({new FileInfo(sessionFile).Length:N0} bytes)"
            : "No session file yet — Pi persists on the first completed run.";
        var map = ActiveFeed.BuildMap();
        var changed = map.ChangedFiles.Count == 0 ? "none observed" : string.Join(", ", map.ChangedFiles);
        var tests = map.TestCommands.Count == 0 ? "none observed" : string.Join(" | ", map.TestCommands);
        OverviewFactsText.Text =
            $"Entries: {ActiveFeed.Count} · error events: {map.ErrorCount} · " +
            $"changed files: {changed} · test commands (heuristic): {tests} · " +
            $"last run: {map.LastReceipt ?? "none"} · view syncs: {_syncRuns:N0}.";
        OverviewMilestones.Children.Clear();
        if (map.Milestones.Count == 0)
        {
            OverviewMilestones.Children.Add(new TextBlock
            {
                Text = "No milestones yet — they appear as you work.",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#AAB2BF")),
                FontSize = 12,
            });
        }

        foreach (var milestone in map.Milestones.TakeLast(8))
        {
            var button = new Button
            {
                Content = $"[#{milestone.Seq} {milestone.Kind}] {milestone.Title}",
                Tag = milestone.Seq,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            button.Click += Milestone_OnClick;
            OverviewMilestones.Children.Add(button);
        }

        RebuildSessionMap();
    }

    /// Overview shows only observed facts: workspace identity, connection,
    /// session file, map-derived counts and milestones. Nothing invented.
    /// </summary>
    private void RebuildOverview()
    {
        if (SelectedWorker is { } worker)
        {
            RebuildWorkerOverview(worker);
            return;
        }

        var entry = SelectedEntry;
        if (entry is null)
        {
            return;
        }

        WorkspaceDetailText.Text = entry.Detail;
        var viewingOwn = _conversation is null || entry.Key == _connectedKey;
        OverviewConnectionText.Text = _conversation?.IsConnected == true
            ? viewingOwn
                ? $"Connected to '{_connectedLabel}'. {EngineSummary()}"
                : $"Connected to '{_connectedLabel}' — viewing '{entry.Label}' (connect it to work here)."
            : $"Not connected. {(viewingOwn ? "Press Connect on the Live page." : "This is not the connected workspace.")}";
        try
        {
            var workspace = entry.ToWorkspace(ManagedRoot);
            var sessionFile = workspace.FindSessionFile();
            OverviewSessionFileText.Text = sessionFile is { } found
                ? $"{found.Path} ({found.Bytes:N0} bytes)"
                : "No session file yet — Pi persists on the first completed run.";
        }
        catch (Exception ex)
        {
            OverviewSessionFileText.Text = $"Session file lookup failed: {ex.Message}";
        }

        var map = _feed.BuildMap();
        var changed = map.ChangedFiles.Count == 0 ? "none observed" : string.Join(", ", map.ChangedFiles);
        var tests = map.TestCommands.Count == 0 ? "none observed" : string.Join(" | ", map.TestCommands);
        OverviewFactsText.Text =
            $"Entries: {_feed.Count} · error events: {map.ErrorCount} · " +
            $"changed files: {changed} · test commands (heuristic): {tests} · " +
            $"last run: {map.LastReceipt ?? "none"} · view syncs: {_syncRuns:N0}.";
        OverviewMilestones.Children.Clear();
        if (map.Milestones.Count == 0)
        {
            OverviewMilestones.Children.Add(new TextBlock
            {
                Text = "No milestones yet — they appear as you work.",
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#AAB2BF")),
                FontSize = 12,
            });
        }

        foreach (var milestone in map.Milestones.TakeLast(8))
        {
            var button = new Button
            {
                Content = $"[#{milestone.Seq} {milestone.Kind}] {milestone.Title}",
                Tag = milestone.Seq,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            button.Click += Milestone_OnClick;
            OverviewMilestones.Children.Add(button);
        }

        RebuildSessionMap();
    }

    private void Milestone_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not long seq)
        {
            return;
        }

        ShowPage("Live");
        var item = _conversationItems.FirstOrDefault(i => i.Seq == seq);
        if (item is not null)
        {
            ConversationList.SelectedItem = item;
            ConversationList.ScrollIntoView(item);
            ConversationList.Focus();
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _conversationItems.Count == 0;
        EmptyStatePanel.IsVisible = empty;
        if (empty && SelectedWorker is { } worker)
        {
            var label = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
            EmptyStateText.Text = worker.IsConnected
                ? $"'{label}' is quiet. Ask anything below to begin."
                : $"Worker '{label}' is not connected yet. Connect, then ask your first question.";
            return;
        }

        if (empty && SelectedEntry is { } entry)
        {
            var name = AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label);
            EmptyStateText.Text = _conversation is null
                ? $"No conversation in '{name}' yet. Connect, then ask your first question — or try a suggestion below."
                : $"'{name}' is quiet. Ask anything below to begin.";
        }
    }

    private void Suggestion_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string prompt)
        {
            PiPromptBox.Text = prompt;
            PiPromptBox.Focus();
            PiPromptBox.CaretIndex = prompt.Length;
        }
    }

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = _prefs.Theme switch
            {
                "Light" => Avalonia.Styling.ThemeVariant.Light,
                "Dark" => Avalonia.Styling.ThemeVariant.Dark,
                _ => Avalonia.Styling.ThemeVariant.Default,
            };
        }
    }

    private void SavePrefs()
    {
        try
        {
            _prefs.Save(ManagedRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save preferences: {ex.Message}";
        }
    }

    private void RefreshSettings()
    {
        if (_syncingPrefs)
        {
            return;
        }

        _syncingPrefs = true;
        try
        {
            SettingsAutoScrollCheck.IsChecked = _prefs.AutoScroll;
            VisibilityCombo.ItemsSource = AgentXPreferences.KnownVisibility;
            VisibilityCombo.SelectedItem = _prefs.Visibility;
            ThemeCombo.SelectedItem = _prefs.Theme;
            TreatmentCombo.SelectedItem = AxTreatments.DisplayName(AxTreatments.Parse(_viewPrefs.Treatment));
            ManagedPathsText.Text =
                $"Sessions: {Path.Combine(ManagedRoot, "sessions")}\n" +
                $"Registry: {WorkspaceRegistry.RegistryPath(ManagedRoot)}\n" +
                $"Workers: {PiWorkerManager.StorePath(ManagedRoot)}{(_manager is null ? " (unavailable — file preserved)" : "")}\n" +
                $"Preferences: {AgentXPreferences.PreferencesPath(ManagedRoot)}\n" +
                (_catalog.IsRecovery ? "Registry state: RECOVERY (registration disabled)." : "Registry state: healthy.");
            PiInfoText.Text = SelectedWorker is { } worker
                ? worker.IsConnected && worker.LastConfirmedState is not null
                    ? $"{EngineSummary()} Session: {worker.SessionId}."
                    : $"Worker '{WorkerViewKey.DisplayLabel(worker.Id, worker.Label)}' registered — press Connect on the Live page."
                : _conversation?.IsConnected == true
                    ? $"{EngineSummary()} Session: {_connectedWorkspace?.SessionId ?? _connectedKey}."
                    : "Pi engine not connected. Provider authentication lives in Pi itself; Agent X never copies credentials.";
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AppVersionText.Text = $"Agent X desktop (Avalonia UI) — app version {version?.ToString() ?? "dev"}. Pi engine 1.1.0 external, unmodified.";
        }
        finally
        {
            _syncingPrefs = false;
        }
    }

    private void SettingsAutoScrollCheck_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_syncingPrefs)
        {
            return;
        }

        _prefs = _prefs with { AutoScroll = SettingsAutoScrollCheck.IsChecked == true };
        SavePrefs();
        _syncingPrefs = true;
        try
        {
            AutoScrollMenuItem.IsChecked = _prefs.AutoScroll;
        }
        finally
        {
            _syncingPrefs = false;
        }
    }

    /// Overflow-menu auto-scroll toggle (replaces the old permanent toolbar
    /// checkbox): same pref, same sync with the Settings checkbox.
    private void AutoScrollMenu_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_syncingPrefs)
        {
            return;
        }

        _prefs = _prefs with { AutoScroll = AutoScrollMenuItem.IsChecked };
        SavePrefs();
        _syncingPrefs = true;
        try
        {
            SettingsAutoScrollCheck.IsChecked = _prefs.AutoScroll;
        }
        finally
        {
            _syncingPrefs = false;
        }
    }

    private void VisibilityCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPrefs)
        {
            return;
        }

        _prefs = _prefs with { Visibility = VisibilityCombo.SelectedItem is string visibility ? visibility : "Detailed" };
        _prefs = _prefs.Normalized();
        SavePrefs();
        ApplyVisibility();
        _syncingPrefs = true;
        try
        {
            VisibilityCombo.SelectedItem = _prefs.Visibility;
        }
        finally
        {
            _syncingPrefs = false;
        }

        RebuildSessionMap();
        if (OverviewPanel.IsVisible)
        {
            RebuildOverview();
        }
    }

    private void ThemeCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPrefs || ThemeCombo.SelectedItem is not string theme)
        {
            return;
        }

        _prefs = _prefs with { Theme = theme };
        _prefs = _prefs.Normalized();
        SavePrefs();
        ApplyTheme();
    }

    /// B7 appearance treatment: same layout and behaviour, three finishes.
    /// Brushes are shared live instances, so the switch repaints in place;
    /// the choice persists in view prefs (Core preferences untouched).
    private void TreatmentCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPrefs || TreatmentCombo.SelectedItem is not string name)
        {
            return;
        }

        var treatment = AxTreatments.Parse(name);
        AxTheme.Apply(treatment);
        AxTheme.EnsureDarkTitleBar(this);
        _viewPrefs.Treatment = AxTreatments.DisplayName(treatment);
        try
        {
            SaveViewPrefs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save view preferences: {ex.Message}";
        }
    }

    /// Native dark title bar once the handle exists; all window behaviour
    /// stays native and untouched. Best-effort (see AxTheme). Applied at
    /// Opened, re-asserted on every activation and twice more shortly after
    /// Opened: DWM attributes proved flaky across identical runs (bright
    /// system-blue captions in some captures), consistent with a handle that
    /// is not ready yet or attributes dropped on re-activation.
    private void MainWindow_OnOpened(object? sender, EventArgs e) => ApplyTitleBar();

    private void MainWindow_OnActivated(object? sender, EventArgs e)
        => AxTheme.EnsureDarkTitleBar(this);

    private void ApplyTitleBar()
    {
        AxTheme.EnsureDarkTitleBar(this);
        var attempts = 0;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        retry.Tick += (_, _) =>
        {
            attempts++;
            AxTheme.EnsureDarkTitleBar(this);
            if (attempts >= 2)
            {
                retry.Stop();
            }
        };
        retry.Start();
    }

    private void OpenSessions_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenFolder(Path.Combine(ManagedRoot, "sessions"));
    }

    private void OpenRegistry_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OpenFolder(ManagedRoot);
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open folder: {ex.Message}";
        }
    }

    private async void CopySelectedMessage_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationViewItem item)
        {
            StatusText.Text = "Select a conversation message first.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard access is unavailable on this platform.";
            return;
        }

        // Authoritative Core text by Seq — never the possibly stale view
        // snapshot: live rows are constructed empty and only the feed sees
        // every chunk, the completion reconcile and cancellation partials.
        var text = ActiveFeed.GetEntryText(item.Seq) ?? item.FullText;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = $"Copied message #{item.Seq} ({text.Length:N0} chars) byte-for-byte.";
    }

    private async void CopyFullConversation_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ActiveFeed.Count == 0)
        {
            StatusText.Text = "Connect to Pi first — the conversation is empty.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard access is unavailable on this platform.";
            return;
        }

        var transcript = ActiveFeed.BuildTranscriptText();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(transcript));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = $"Copied the full conversation ({transcript.Length:N0} chars, {ActiveFeed.Count} entries).";
    }

    private async void CopySessionMap_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            StatusText.Text = "Clipboard access is unavailable on this platform.";
            return;
        }

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(SessionMapText.Text ?? string.Empty));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = "Copied the session map.";
    }

    private void Conversation_ActivityChanged(object? sender, string activity)
    {
        // Stale-callback guard: only the CURRENT conversation may touch the
        // feed or the map. A queued event from a previous workspace (or a
        // disposed conversation) is dropped, never mixed into the new one.
        // Runs on the event thread: only lock-protected feed state is
        // touched here; the coalesced tick applies text and derived views.
        // No per-event UI post — event bursts collapse into pending syncs.
        if (!ReferenceEquals(sender, _conversation))
        {
            return;
        }

        // Stash the footer text only while the workspace feed is viewed:
        // a background workspace run must not overwrite a worker reader.
        // Fires on the RPC thread: read the mirrored id, not the control.
        // The session map still shows real observed activity, never stale idle.
        if (_selectedWorkerId is null)
        {
            _pendingActivityText = activity;
        }

        _feed.SetActivity(string.IsNullOrWhiteSpace(activity) ? null : activity);

    }

    private void Conversation_ToolActivityChanged(object? sender, PiToolActivity activity)
    {
        // Same stale-callback guard, same thread: feed pairing is
        // lock-protected and order-safe on the event thread, so tool bursts
        // need no per-event post either. Derived views (map, milestones)
        // refresh once in the coalesced tick, not once per tool event.
        if (!ReferenceEquals(sender, _conversation))
        {
            return;
        }

        // Feed rows (grouped start/end, detail rows) arrive via the
        // single SyncFeedView path on feed Changed; this handler only
        // records the pairing-relevant mutation.
        if (activity.IsStart)
        {
            _feed.AddToolStart(activity.ToolName, activity.Summary, activity.Path, activity.IsMutating, activity.CallId);
        }
        else
        {
            _feed.AddToolEnd(activity.ToolName, activity.IsError, activity.CallId);
        }
    }

    private void Conversation_ConnectionLost(object? sender, Exception error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _conversation))
            {
                return;
            }

            // Dedupe against the active run's catch, which records the same
            // loss: one failure stays one feed entry regardless of order.
            var last = _feed.Snapshot().LastOrDefault();
            if (last?.Role != "system" || !last.IsError || !last.Text.StartsWith("Connection to Pi lost", StringComparison.Ordinal))
            {
                _feed.AddError($"Connection to Pi lost: {error.Message}");
            }

            _feed.SetActivity(null);
            if (SelectedWorker is not null)
            {
                // Workspace connection died while a worker tab is selected:
                // the error row is in the workspace feed; the reader's
                // footer stays on the worker. Dots refresh below.
                RefreshOverviewAndSettings();
                return;
            }

            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            PiConnectionState.Text = "Connection lost";
            PiRunStatus.Text = "Connection to Pi lost — session is persisted. Press Reconnect to restore.";
            RebuildSessionMap();
            RefreshOverviewAndSettings();
        });
    }

    /// <summary>
    /// Genuine engine values for the viewed tab: the selected worker's last
    /// confirmed state, else the workspace connection's noted values.
    /// Model/thinking selection stays pending (adapter) — display only.
    private (string? Provider, string? Model, string? Thinking, int? Messages) ActiveEngine()
    {
        if (SelectedWorker is { } worker)
        {
            // No cross-identity fallback: an unconnected worker with no
            // confirmed state shows unknown, never legacy's values.
            return WorkerDisplayPolicy.EngineForWorker(worker.LastConfirmedState);
        }

        return _knownModelDisplay is null
            ? (null, null, null, null)
            : (_knownProvider, _knownModelDisplay, _knownThinking, _knownMessages);
    }

    /// Remember the genuine engine values from get_state (connect and
    /// reconnect). The footer shows these verbatim; usage shows the message
    /// count only, because token/cost context does not exist on this path.
    /// </summary>
    private void NoteEngineState(PiEngineState state)
    {
        _knownProvider = state.Provider;
        _knownModelId = state.ModelId;
        _knownModelDisplay = state.ModelName ?? state.ModelId ?? "none";
        _knownThinking = state.ThinkingLevel;
        _knownMessages = state.MessageCount;
        RefreshEngineSegments();
    }

    private void ClearEngineState()
    {
        _knownProvider = null;
        _knownModelId = null;
        _knownModelDisplay = null;
        _knownThinking = null;
        _knownMessages = null;
        RefreshEngineSegments();
    }

    /// <summary>One-line engine summary for Overview/Settings (genuine values only, from the viewed tab).</summary>
    private string EngineSummary()
    {
        var engine = ActiveEngine();
        return engine.Model is null
            ? "Pi engine not connected."
            : $"Model: {engine.Model} ({engine.Provider ?? "?"}) · thinking {engine.Thinking ?? "?"} · messages {engine.Messages?.ToString("N0") ?? "?"}";
    }

    /// <summary>Footer engine segments from the last known genuine values of the viewed tab.</summary>
    private void RefreshEngineSegments()
    {
        var engine = ActiveEngine();
        ModelButton.Content = engine.Model is null
            ? "Model: —"
            : $"({engine.Provider ?? "?"}) {engine.Model}";
        ThinkingButton.Content = engine.Thinking is null ? "Thinking: —" : engine.Thinking;
        UsageText.Text = engine.Messages is { } messages ? $"messages {messages:N0}" : "messages —";
        var engineTip = _settingsRuntime.CanConfigure
            ? "Click to change (verified Core operation)"
            : $"Selection pending: {_settingsRuntime.UnavailableReason}";
        Avalonia.Controls.ToolTip.SetTip(ModelButton, $"Engine model. {engineTip}");
        Avalonia.Controls.ToolTip.SetTip(ThinkingButton, $"Thinking level. {engineTip}");
    }

    /// <summary>
    /// Footer directory: the connected worker's cwd, else the selected
    /// tab's. Never a branch (not tracked) and never invented.
    /// </summary>
    private void RefreshDirectoryLabel()
    {
        try
        {
            if (SelectedWorker is { } worker)
            {
                DirectoryButton.Content = worker.ProjectRoot;
                return;
            }

            AgentXWorkspace? workspace = _connectedWorkspace;
            if (workspace is null && SelectedEntry is { } entry)
            {
                workspace = entry.ToWorkspace(ManagedRoot);
            }

            DirectoryButton.Content = workspace?.WorkingDirectory ?? "Select a workspace…";
        }
        catch
        {
            DirectoryButton.Content = "Select a workspace…";
        }
    }

    private void DirectoryButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Guard first: the picker must never disturb a live run. Past the
        // guard, the existing Add flow stages the folder and requires
        // explicit confirmation — registration alone never retargets live
        // work, and the staged confirm panel is surfaced on Settings.
        // An idle workspace connection does not block: registering (or
        // creating a worker) cannot steal its view, and workers are
        // independent processes by design.
        if (_piBusy || _connecting
            || (SelectedWorker?.Id is { } busyId
                && (_busyWorkers.Contains(busyId) || _connectingWorkers.Contains(busyId))))
        {
            StatusText.Text = "A run or connection is active — wait for it to settle before opening the picker.";
            return;
        }

        AddWorkspace_OnClick(sender, e);
    }

    private Avalonia.Controls.Flyout? _modelFlyout;
    private Avalonia.Controls.Flyout? _thinkingFlyout;

    /// <summary>
    /// Honest engine flyout: today's values are real (get_state); selection
    /// itself is pending the Core contract, stated plainly instead of
    /// presented as functional. When CanConfigure flips true, the
    /// adapter's real lists render in this same surface. One Flyout per
    /// button, content refreshed on every open — repeated clicks never
    /// stack popups (screen readers would hear each copy).
    /// </summary>
    private Avalonia.Controls.StackPanel BuildEngineFlyoutPanel(string title, string current)
    {
        var panel = new Avalonia.Controls.StackPanel { Spacing = 6 };
        panel.Children.Add(new Avalonia.Controls.TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.Bold,
        });
        panel.Children.Add(new Avalonia.Controls.TextBlock
        {
            Text = current,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(new Avalonia.Controls.TextBlock
        {
            Text = _settingsRuntime.CanConfigure
                ? "Choose:"
                : _settingsRuntime.UnavailableReason,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#82889A")),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return panel;
    }

    private void ModelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _modelFlyout ??= new Avalonia.Controls.Flyout();
        _modelFlyout.Content = BuildEngineFlyoutPanel("Engine model",
            ActiveEngine().Model is null
                ? "No engine connected."
                : $"Current: ({ActiveEngine().Provider ?? "?"}) {ActiveEngine().Model}");
        _modelFlyout.ShowAt(ModelButton);
    }

    private void ThinkingButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _thinkingFlyout ??= new Avalonia.Controls.Flyout();
        _thinkingFlyout.Content = BuildEngineFlyoutPanel("Thinking level",
            ActiveEngine().Thinking is null ? "No engine connected." : $"Current: {ActiveEngine().Thinking}");
        _thinkingFlyout.ShowAt(ThinkingButton);
    }

    private async Task DisconnectPiAsync()
    {
        StopFlushTimerIfIdle();
        if (_promptLifetime is not null)
        {
            await _promptLifetime.CancelAsync();
        }

        if (_conversation is not null)
        {
            _conversation.ActivityChanged -= Conversation_ActivityChanged;
            _conversation.ToolActivityChanged -= Conversation_ToolActivityChanged;
            _conversation.ConnectionLost -= Conversation_ConnectionLost;
            _bridge.Detach();
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        _connectedKey = null;
        _connectedLabel = null;
        _connectedWorkspace = null;
        _coordinator.NoteDisconnected();
        // Session boundary: drop in-flight tool pairing (never rows). A
        // later reconnect resumes the same session with fresh call IDs;
        // stale indexes must not pair across the boundary.
        _feed.AbandonOpenTools();
        _pendingActivityText = null;

        Dispatcher.UIThread.Post(() =>
        {
            PiConnectionState.Text = "Disconnected";
            ClearEngineState();
            PiActivityText.Text = string.Empty;
            _feed.SetActivity(null);
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            PiRunStatus.Text = "Session is persisted — reconnect to restore the conversation.";
            RefreshDirectoryLabel();
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        _flushTimer.Stop();
        _bridge.Detach();
        SaveWorkerViewState(_lastViewKey);
        if (_conversation is not null)
        {
            _conversation.ActivityChanged -= Conversation_ActivityChanged;
            _conversation.ToolActivityChanged -= Conversation_ToolActivityChanged;
            _conversation.ConnectionLost -= Conversation_ConnectionLost;
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        // Retire every worker process: no orphans past app exit. A failed
        // retirement is swallowed — the window is closing and no surface is
        // left to report on; Core keeps ownership for a retry.
        if (_manager is not null)
        {
            try
            {
                await _manager.DisposeAsync();
            }
            catch
            {
            }
        }

        _promptLifetime?.Dispose();
        _promptLifetime = null;
    }
}
