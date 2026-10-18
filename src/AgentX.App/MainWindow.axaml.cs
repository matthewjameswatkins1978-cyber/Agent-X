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
    // and thinking selection flow ONLY through this adapter. Activated:
    // CoreEngineSettingsRuntime over the validated PiWorker operations
    // (issue #23 Core half) with the native chooser below rendering the
    // real lists. Availability and reasons are live per call, never cached.
    private readonly IEngineSettingsRuntime _settingsRuntime;
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
    // Which legacy workspace the in-flight connect/reconnect belongs to:
    // cards must show Connecting only on that entry, never on bystanders.
    // Set and cleared together with _connecting, UI thread only.
    private string? _connectingKey;

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

    /// <summary>
    /// Where the app keeps sessions, view prefs, catalog and workers.
    /// AGENTX_MANAGED_ROOT (process-scoped, rooted paths only) redirects them
    /// to an isolated directory, so automated evidence runs and smoke tests
    /// never touch the real %LocalAppData%/AgentX profile. Unset - the normal
    /// case - is exactly PiSessionOptions.Default().
    /// </summary>
    private static string ManagedRoot
        => Environment.GetEnvironmentVariable("AGENTX_MANAGED_ROOT") is { Length: > 0 } root
            && Path.IsPathRooted(root)
            ? root
            : PiSessionOptions.Default().ManagedRoot;

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
        _settingsRuntime = new CoreEngineSettingsRuntime(
            () => SelectedWorker is { } w ? new PiWorkerEngineSettings(w) : null);
        InitializeComponent();
        ConversationList.ItemsSource = _conversationItems;
        // Empty state follows the collection itself: any add/clear on the UI
        // thread updates it, on every page, with no scattered call sites.
        _conversationItems.CollectionChanged += (_, _) => UpdateEmptyState();
        // The run-status line vacates the composer when empty: one
        // watcher covers every writer, so the empty composer stays
        // compact with no scattered visibility calls.
        PiRunStatus.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                PiRunStatus.IsVisible = !string.IsNullOrEmpty(PiRunStatus.Text);
            }
        };
        PiRunStatus.IsVisible = !string.IsNullOrEmpty(PiRunStatus.Text);
        BuildAddFlyout();
        // Feed menu opens programmatically at the list (composer
        // pattern): right-click AND keyboard menu keys, exactly once.
        ConversationList.ContextRequested += (_, e) =>
        {
            if (ConversationList.ContextFlyout is MenuFlyout feedMenu)
            {
                feedMenu.ShowAt(ConversationList);
            }

            e.Handled = true;
        };
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
        SizeChanged += (_, _) => PostLayoutRefresh();
        // Developer Layout Inspector (B2): F12 toggles the guide overlay.
        // Tunnel so the key works from anywhere, including the composer;
        // the handler only ever acts on F12 itself.
        AddHandler(
            InputElement.KeyDownEvent,
            new EventHandler<Avalonia.Input.KeyEventArgs>(Window_OnPreviewKeyDown),
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var (prefs, note) = AgentXPreferences.Load(ManagedRoot);
        _prefs = prefs;
        ApplyVisibility();
        _viewPrefs = ViewPrefs.Load(ManagedRoot);
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
        FeedCombo.ItemsSource = AxFeed.Names;
        FeedSizeCombo.ItemsSource = AxFeed.Sizes;
        AxTheme.Apply(AxTreatments.Parse(_viewPrefs.Treatment));
        AxTheme.ApplyAppearance(AxFeed.Parse(_viewPrefs.FeedAppearance), _viewPrefs.FeedFontSize);
        _syncingPrefs = true;
        try
        {
            SettingsAutoScrollCheck.IsChecked = _prefs.AutoScroll;
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

        // Worker tabs are part of LoadWorkspaceTabs itself now (merged
        // strip order): no separate append step to forget on later rebuilds.
        UpdateTabDots();
        if (_managerError is not null)
        {
            StatusText.Text = _viewPrefs.IsRecovery
                ? $"{_viewPrefs.RecoveryMessage} {_managerError}"
                : _managerError;
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
        // Both populations merge through one tested order (catalog first,
        // then workers): a catalog rebuild after a workspace registration
        // can never drop an existing worker tab again. Keys materialize
        // to catalog tabs or worker tabs; unknown keys are impossible by
        // construction but skipped rather than crashed on.
        var workerIds = _manager?.Workers.Select(w => w.Id).ToArray() ?? [];
        var order = WorkerDisplayPolicy.StripOrder(
            resolved.Entries.Select(e => e.Key).ToArray(), workerIds);
        var entriesByKey = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        foreach (var entry in resolved.Entries)
        {
            entriesByKey[entry.Key] = entry;
        }

        var workersById = new Dictionary<string, PiWorker>(StringComparer.Ordinal);
        if (_manager is not null)
        {
            foreach (var worker in _manager.Workers)
            {
                workersById[worker.Id] = worker;
            }
        }

        WorkspaceTabs.Items.Clear();
        var tintIndex = 0;
        foreach (var key in order)
        {
            if (entriesByKey.TryGetValue(key, out var entry))
            {
                WorkspaceTabs.Items.Add(MakeTab(entry,
                    isConnected: connectedLive && entry.Key == _connectedKey,
                    tintIndex: tintIndex++));
            }
            else if (workersById.TryGetValue(key, out var worker))
            {
                WorkspaceTabs.Items.Add(MakeWorkerTab(
                    worker, worker.IsConnected, _busyWorkers.Contains(worker.Id), tintIndex++));
            }
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
        CloseEngineChoosers();
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
        // Every selection/busy/connection change flows through here, so
        // the empty-state Connect can never linger into a connected run.
        RefreshEmptyConnect();
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
        // Approved shell mirrors: worker cards and the compact footer follow
        // every selection, busy and connection change through this fan-out.
        RebuildContextCards();
        RefreshCompactFooter();
    }

    /// Working indicator follows the VIEWED run only — never another
    /// worker's activity. Background runs keep their tab dots.
    private void RefreshWorkingRow()
    {
        var viewedBusy = SelectedWorker is { } worker ? _busyWorkers.Contains(worker.Id) : _piBusy;
        WorkingRow.IsVisible = WorkerDisplayPolicy.ShowWorking(viewedBusy);
        // Stop lives only on the composer Send/Stop button (plus Escape):
        // the rail icon is gone, one guarded surface remains.
        RefreshComposerSendButton();
    }

    /// Connection affordances follow the selection (card menus own their
    /// states at open time; the empty-state Connect owns its visibility
    /// here). Refreshed with the tab dots, so every selection and state
    /// change carries it — no scattered call sites.
    private void RefreshConnectionButtons()
    {
        RefreshEmptyConnect();
        RefreshComposerSendButton();
        // B1: the empty composer names its destination (approved Feed design).
        PiPromptBox.PlaceholderText = ComposerPlaceholder.For(
            SelectedWorker is { } worker ? WorkerViewKey.DisplayLabel(worker.Id, worker.Label) : null,
            SelectedEntry is { } entry ? AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label) : null);
    }

    private void WorkspaceTabs_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents)
        {
            return;
        }

        // End any bound chooser interaction: the open chooser belongs to
        // the previous tab and must not offer its choices to this one.
        CloseEngineChoosers();

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
    /// Retire a successfully sent draft that settled while another view
    /// was selected (send-then-switch-before-settle). Only vacates when
    /// the stored draft is still exactly the sent message: a newer user
    /// edit is never clobbered. The visible box is untouched — another
    /// view owns it. Both the in-memory store and the persisted prefs
    /// move together so the phantom cannot resurrect on return.
    /// </summary>
    private void VacateSentDraft(string key, string sentMessage)
    {
        var store = _viewLifecycle.Store;
        if (!store.TryGet(key, out var state) || state is null
            || !string.Equals(state.DraftText, sentMessage, StringComparison.Ordinal))
        {
            return;
        }

        if (!store.Save(key, state with { DraftText = string.Empty }))
        {
            return;
        }

        _viewPrefs.Drafts[key] = string.Empty;
        try
        {
            SaveViewPrefs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            store.MarkDirty(key);
        }
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

    private void AddWorkspace_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_catalog.IsRecovery)
        {
            StatusText.Text = $"Registration disabled while the registry is unreadable — repair or back up {WorkspaceRegistry.RegistryPath(ManagedRoot)}, then restart.";
            return;
        }

        // Choice-first (P0): + offers Existing Project vs Quick Setup.
        // No picker opens here; ExistingProject_OnClick runs the picker
        // flow and QuickSetupReveal starts the name flow.
        _pendingWorkspacePath = null;
        ShowAddMode("choice");
        _addConfirmNote.Text = "Choose how to add the agent — nothing has been registered yet.";
        StatusText.Text = "Choose how to add the agent — nothing has been registered yet.";
        _addConfirmFlyout.ShowAt(AddContextButton);
    }

    // Pending explicit confirmation: the picker result is staged here and
    // opened ONLY by OpenProject_OnClick. A phantom or dismissed
    // picker can never create a workspace by itself.
    private string? _pendingWorkspacePath;

    /// Add modes for the single Add flyout: choice (Existing vs Quick),
    /// confirm (staged folder facts + Open), quick (editable name + Create).
    /// Only one mode is ever visible; switching modes never registers or
    /// creates anything — the mode buttons do that explicitly.
    private void ShowAddMode(string mode)
    {
        var choice = mode == "choice";
        var confirm = mode == "confirm";
        var quick = mode == "quick";
        _addTitle.Text = choice ? "Add agent" : confirm ? "Add project" : "Quick setup";
        _addConfirmPath.IsVisible = confirm;
        _choiceRow.IsVisible = choice;
        _quickSetupRow.IsVisible = quick;
        _confirmRow.IsVisible = confirm || quick;
        _openButton.IsVisible = confirm;
        if (choice)
        {
            _existingButton.Focus();
        }
        else if (quick)
        {
            _quickNameBox.Focus();
        }
        else
        {
            _openButton.Focus();
        }
    }

    /// Existing Project choice: the folder picker flow. Staging switches
    /// the flyout to confirm mode; only Open project acts on the result.
    private async void ExistingProject_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
    }

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
        // flyout's own buttons turn a staged path into a workspace or
        // worker; closing the flyout any other way cancels (OnClosed).
        _pendingWorkspacePath = path;
        _addConfirmPath.Text = path;
        Avalonia.Controls.ToolTip.SetTip(_addConfirmPath, path);
        var registeredNote = IsAlreadyRegistered(path)
            ? "Already registered — opening selects the existing project. Nothing is duplicated."
            : "Nothing has been registered yet. Open to continue, or close to cancel.";
        _addConfirmNote.Text = registeredNote + " Reading Git state…";
        StatusText.Text = "Confirm at the Add panel — nothing has been registered yet.";
        ShowAddMode("confirm");
        _addConfirmFlyout.ShowAt(AddContextButton);
        // Git discovery runs off the UI thread (P0): the note paints only
        // when this same path is still staged, so a fast re-pick can never
        // show another folder's facts. States are loading / observed /
        // unavailable — never invented.
        var facts = await Task.Run(() => DescribeStagedProject(path)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_pendingWorkspacePath != path)
            {
                return;
            }

            _addConfirmNote.Text = registeredNote + " " + facts;
        });
        return;
    }

    /// Duplicate check for the Add confirmation note: same canonical root
    /// already in the registry selects rather than duplicates (Core stays
    /// idempotent; the user sees which one it is before confirming).
    private bool IsAlreadyRegistered(string path)
    {
        try
        {
            var canonical = AgentXWorkspace.CanonicalizeProjectRoot(path);
            return WorkspaceRegistry.Load(ManagedRoot).List()
                .Any(r => string.Equals(
                    AgentXWorkspace.CanonicalizeProjectRoot(r.ProjectRoot),
                    canonical, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private Flyout _addConfirmFlyout = null!;

    private TextBlock _addConfirmPath = null!;

    private TextBlock _addConfirmNote = null!;

    private TextBox _quickNameBox = null!;

    private StackPanel _quickSetupRow = null!;

    private TextBlock _addTitle = null!;

    private Button _existingButton = null!;

    private Button _openButton = null!;

    private StackPanel _choiceRow = null!;

    private StackPanel _confirmRow = null!;

    /// Inline Add confirmation (repair gate): code-built so the window
    /// owns every control — XAML flyout content is outside the window
    /// name-scope and cannot be wired by name. Charcoal AxAction buttons
    /// match the repaired grey-button language everywhere else.
    private void BuildAddFlyout()
    {
        _addConfirmPath = new TextBlock
        {
            FontSize = 12,
            Foreground = AxTheme.MutedBrush,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            MaxLines = 3,
        };
        _addConfirmNote = new TextBlock
        {
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        _addTitle = new TextBlock { FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold };
        _existingButton = new Button { Content = "Existing Project" };
        _existingButton.Classes.Add("AxAction");
        Avalonia.Controls.ToolTip.SetTip(_existingButton,
            "Choose a folder — Git and project context discovered, worker created or reused");
        _existingButton.Click += ExistingProject_OnClick;
        var choiceSetupButton = new Button { Content = "Quick Setup" };
        choiceSetupButton.Classes.Add("AxAction");
        Avalonia.Controls.ToolTip.SetTip(choiceSetupButton,
            "New folder under the projects root + local git init (name editable), then the same worker path");
        choiceSetupButton.Click += QuickSetupReveal_OnClick;
        _choiceRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        _choiceRow.Children.Add(_existingButton);
        _choiceRow.Children.Add(choiceSetupButton);
        _quickNameBox = new TextBox
        {
            FontSize = 12,
            PlaceholderText = "Project name (editable)",
            MinWidth = 200,
        };
        Avalonia.Controls.ToolTip.SetTip(_quickNameBox,
            "Editable project name; blank generates one (Core NewProject; no remote, no commit)");
        var quickCreateButton = new Button { Content = "Create project" };
        quickCreateButton.Classes.Add("AxAction");
        Avalonia.Controls.ToolTip.SetTip(quickCreateButton,
            "Create the named folder with git init and open its worker");
        quickCreateButton.Click += QuickSetupCreate_OnClick;
        _quickSetupRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, IsVisible = false };
        _quickSetupRow.Children.Add(_quickNameBox);
        _quickSetupRow.Children.Add(quickCreateButton);
        _openButton = new Button { Content = "Open project" };
        _openButton.Classes.Add("AxAction");
        Avalonia.Controls.ToolTip.SetTip(_openButton,
            "Register the staged folder and open its persistent Pi worker (reused when present)");
        _openButton.Click += OpenProject_OnClick;
        var cancelButton = new Button { Content = "Cancel" };
        cancelButton.Classes.Add("AxAction");
        cancelButton.Click += CancelWorkspace_OnClick;
        _confirmRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        _confirmRow.Children.Add(_openButton);
        _confirmRow.Children.Add(cancelButton);
        var body = new StackPanel { Spacing = 8, MinWidth = 320, MaxWidth = 460 };
        body.Children.Add(_addTitle);
        body.Children.Add(_addConfirmPath);
        body.Children.Add(_addConfirmNote);
        body.Children.Add(_choiceRow);
        body.Children.Add(_quickSetupRow);
        body.Children.Add(_confirmRow);
        _addConfirmFlyout = new Flyout
        {
            Content = body,
            // The + button sits at the window's right edge: aligning the
            // flyout's right edge to the anchor's right grows it leftward
            // into the window instead of off-screen. Constraint adjustment
            // (slide/flip/resize) is the supported fallback for narrow
            // windows — no hardcoded offsets.
            Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight,
            PlacementConstraintAdjustment = Avalonia.Controls.Primitives.PopupPositioning.PopupPositionerConstraintAdjustment.All,
        };
        _addConfirmFlyout.Closed += AddConfirmFlyout_OnClosed;
        // Keyboard users land on the first action when the flyout opens;
        // tab order then walks the actions, Esc/outside-click dismisses.
        _addConfirmFlyout.Opened += (_, _) => FocusAddFlyout();
        AddContextButton.Flyout = _addConfirmFlyout;
    }

    /// Focus follows the visible Add mode (never an invisible control):
    /// choice → Existing Project, confirm → Open project, quick → name box.
    /// Routing logic is unit-tested via <see cref="AddFlyoutFocus"/>.
    private void FocusAddFlyout()
    {
        switch (AddFlyoutFocus.Pick(
            _choiceRow.IsVisible,
            _openButton.IsVisible && _confirmRow.IsVisible,
            _quickSetupRow.IsVisible))
        {
            case AddFlyoutFocus.Target.Open:
                _openButton.Focus();
                break;
            case AddFlyoutFocus.Target.Name:
                _quickNameBox.Focus();
                break;
            default:
                _existingButton.Focus();
                break;
        }
    }

    /// Dismissing the Add flyout without confirming is a cancellation:
    /// the staged path is dropped and nothing changes. Confirm/Create
    /// clear the staging first, so their own flyout close is a no-op.
    private void AddConfirmFlyout_OnClosed(object? sender, EventArgs e)
    {
        // Visibility resets on the next open via ShowAddMode; closing only
        // drops unconfirmed staging.
        if (_pendingWorkspacePath is not null)
        {
            _pendingWorkspacePath = null;
            StatusText.Text = "Registration cancelled — nothing changed.";
        }
    }

    /// Quick Setup (Core NewProject): an editable name, a new folder under
    /// the projects root, local git init — no remote, no commit. Then the
    /// same worker path as Existing Project. Partial states report the
    /// folder path; existing work is never overwritten (Core suffixes -2…).
    private void QuickSetupReveal_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _quickNameBox.Text ??= ProjectSetupFacts.SuggestName();
        _addConfirmNote.Text = $"New folder under {ProjectSetupFacts.DefaultProjectsParent()} + local git init (no remote, no commit). Edit the name, then Create project — or close to cancel.";
        ShowAddMode("quick");
    }

    private async void QuickSetupCreate_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var hint = _quickNameBox.Text;
        _pendingWorkspacePath = null;
        _addConfirmFlyout.Hide();
        if (_manager is null)
        {
            StatusText.Text = $"Workers unavailable ({_managerError ?? "store failed to load"}) — nothing created.";
            return;
        }

        var parent = ProjectSetupFacts.DefaultProjectsParent();
        try
        {
            Directory.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Cannot create projects folder {parent}: {ex.Message} — nothing created.";
            return;
        }

        string created;
        try
        {
            created = await Task.Run(() => NewProject.Create(parent, hint)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = ProjectSetupFacts.DescribeSetupFailure(parent, hint, ex));
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var worker = _manager.CreateWorker(created, Path.GetFileName(created));
                SaveWorkerViewState(_lastViewKey);
                _manager.SelectWorker(worker.Id);
                SelectWorkerTab(worker.Id);
                SwitchToWorker(worker);
                StatusText.Text = $"Quick setup '{Path.GetFileName(created)}' ready at {created} (git init done, no remote/commit) — session {worker.SessionId}. Press Connect.";
            }
            catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                StatusText.Text = $"Quick setup folder created at {created} but worker failed: {ex.Message}";
            }
        });
    }

    /// Git + instruction facts for the staged folder (Existing Project).
    /// A missing git binary or timeout is reported, never guessed; a
    /// non-repo is stated plainly and never initialised here.
    private static string DescribeStagedProject(string path)
    {
        ProjectGitState git;
        try
        {
            git = ProjectGit.ReadState(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
        {
            return $"Git state unavailable ({ex.Message}) — confirm anyway or cancel.";
        }

        return ProjectSetupFacts.Describe(git, ProjectSetupFacts.InstructionFiles(git.ProjectRoot));
    }

    /// Async footer tooltip (P0): Git discovery runs off the UI thread and
    /// paints only while the same worker is still viewed — a project switch
    /// mid-discovery can never paint another project's facts.
    private async void UpdateGitTooltipAsync(string workerId, string projectRoot)
    {
        var tip = await Task.Run(() => GitTooltipFor(projectRoot)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedWorker?.Id != workerId)
            {
                return;
            }

            Avalonia.Controls.ToolTip.SetTip(DirectoryButton, tip);
        });
    }

    /// Git tooltip for the footer directory button: branch + dirty state
    /// for the viewed worker, or honest unavailability. Never blocks.
    private static string GitTooltipFor(string projectRoot)
    {
        try
        {
            var git = ProjectGit.ReadState(projectRoot);
            if (!git.IsGitRepo)
            {
                return $"{projectRoot} (not a Git repository)";
            }

            var branch = ProjectGit.DescribeBranch(git);
            return $"{projectRoot} • {branch} • {ProjectGit.DescribeStatus(git)}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or ArgumentException)
        {
            return $"{projectRoot} (Git state unavailable: {ex.Message})";
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

    /// Unified open (P1): one coherent route — the staged folder is
    /// registered (idempotent, legacy-compatible) and its persistent Pi
    /// worker is selected or created, then its Feed opens. No split
    /// worker-vs-register choice for ordinary use.
    private void OpenProject_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = _pendingWorkspacePath;
        _pendingWorkspacePath = null;
        _addConfirmFlyout.Hide();
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
            var already = registry.List()
                .Any(r => string.Equals(
                    AgentXWorkspace.CanonicalizeProjectRoot(r.ProjectRoot),
                    AgentXWorkspace.CanonicalizeProjectRoot(path), StringComparison.OrdinalIgnoreCase));
            var workspace = registry.GetOrRegister(path);
            registry.Save();
            LoadWorkspaceTabs(selectKey: workspace.Slug);
            // The visible card strip is not part of LoadWorkspaceTabs:
            // without this fan-out the new card never appears (registration
            // persisted, UI stale). Selection already moved; just mirror.
            UpdateTabDots();
            if (_manager is null)
            {
                StatusText.Text = already
                    ? $"Workspace '{workspace.Slug}' was already registered — selected it. Nothing duplicated."
                    : _conversation is not null && workspace.Slug != _connectedKey
                        ? $"Workspace '{workspace.Slug}' registered — disconnect from '{_connectedLabel}' to open it."
                        : $"Workspace '{workspace.Slug}' ready — session {workspace.SessionId}. Press Connect.";
                return;
            }

            var existingId = ProjectSetupFacts.FindWorkerIdByRoot(
                _manager.Workers.Select(w => (w.Id, w.ProjectRoot)), workspace.ProjectRoot);
            if (existingId is not null)
            {
                SaveWorkerViewState(_lastViewKey);
                var existing = _manager.SelectWorker(existingId);
                SelectWorkerTab(existing.Id);
                SwitchToWorker(existing);
                StatusText.Text = $"Project '{workspace.Slug}' open — worker '{WorkerViewKey.DisplayLabel(existing.Id, existing.Label)}' already exists here; selected it. Press Connect.";
                return;
            }

            try
            {
                var worker = _manager.CreateWorker(workspace.ProjectRoot, workspace.Slug);
                SaveWorkerViewState(_lastViewKey);
                _manager.SelectWorker(worker.Id);
                SelectWorkerTab(worker.Id);
                SwitchToWorker(worker);
                StatusText.Text = $"Project '{workspace.Slug}' open — worker created, session {worker.SessionId}. Press Connect.";
            }
            catch (Exception ex) when (ex is ArgumentException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                StatusText.Text = $"Project '{workspace.Slug}' registered, but worker creation failed: {ex.Message} — nothing else changed; retry or remove the registration.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Cannot open project: {ex.Message}";
        }
    }

    private void CancelWorkspace_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _pendingWorkspacePath = null;
        _addConfirmFlyout.Hide();
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
            // Same composer-compact rule as workspaces: the restore note
            // rides the footer transient, never the composer row.
            StatusText.Text = (count == 0
                ? $"New worker session for '{label}' — no previous conversation."
                : $"Worker '{label}' connected — restored {count} message(s). Send a prompt to continue.")
                + $" Pi runs in {worker.ProjectRoot} with your user authority.";
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
            UpdateTabDots();
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
            UpdateTabDots();
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
        _connectingKey = choice.Key;
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
        // Complete the observability binding: post-settle stats/state polls
        // record through the conversation side (the tap side attaches in the
        // factory above). Without this, LatestStats stays null and Usage
        // telemetry never reaches the Overview.
        conversation.AttachBridge(_bridge);
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
            // The restore notice rides the footer transient (auto-collapsed
            // routine), never the composer: PiRunStatus stays empty so the
            // empty composer keeps its compact height after a restore.
            StatusText.Text = (history.Count == 0
                ? $"New session for '{choice.Label}' — no previous conversation."
                : $"Restored '{choice.Label}' with {history.Count} message(s). Send a prompt to continue.")
                + $" Pi runs in {workspace.WorkingDirectory} with your user authority.";
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
            _connectingKey = null;
            // Cards and footer read the flags: refresh AFTER clearing, or a
            // finished connect keeps showing Connecting (the mid-flight
            // UpdateTabDots above runs while the flags are still set).
            UpdateTabDots();
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
        _connectingKey = _connectedKey;
        PiConnectionState.Text = "Reconnecting…";
        // Owner lock: reconnect restores the workspace this conversation was
        // opened for, even if the picker selection moved meanwhile. A selection
        // change mid-reconnect can neither redirect nor falsely fail it.
        var owner = _connectedWorkspace;
        if (owner is null)
        {
            _connecting = false;
            _connectingKey = null;
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
            _connectingKey = null;
            UpdateTabDots();
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
            // A new run owns the status line: stale reconnect/restore
            // notes vacate when work begins (the row collapses empty).
            PiRunStatus.Text = string.Empty;
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
            // Background settle: the reader may have moved on, but the
            // successfully sent draft must still retire under the WORKER's
            // key — otherwise the pre-switch snapshot restores the sent
            // text as a phantom draft on return (resendable verbatim).
            VacateSentDraft(WorkerViewKey.ForWorker(id), message);
            if (!IsSelectedWorker(id))
            {
                return; // Reader moved on: feed + dots are current; footer stays theirs.
            }

            PiConnectionState.Text = "Idle";
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            RebuildSessionMap();
            RefreshOverviewAndSettings();
            // Routine success leaves no composer row: the footer StatusText
            // already carries the healthy summary, and the empty status
            // collapses the line. Warnings keep their row (failures never
            // reach here). The composer-clear below still vacates the box.
            PiRunStatus.Text = string.Empty;
            if (ComposerActionPolicy.ShouldClearComposer(PiPromptBox.Text, message))
            {
                PiPromptBox.Text = string.Empty;
                // Same phantom-draft guard as the workspace path: the
                // cleared box must win over the persisted draft.
                SaveWorkerViewState(_lastViewKey);
            }
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
        // A new run owns the status line: stale reconnect/restore notes
        // vacate when work begins (the row collapses empty).
        PiRunStatus.Text = string.Empty;
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
            // Routine success leaves no composer row: the footer StatusText
            // already carries the healthy summary, and the empty status
            // collapses the line. Warnings keep their row (failures never
            // reach here). The composer-clear below still vacates the box.
            PiRunStatus.Text = string.Empty;
            if (ComposerActionPolicy.ShouldClearComposer(PiPromptBox.Text, message))
            {
                PiPromptBox.Text = string.Empty;
                // Persist the vacated box: without this the sent text
                // survives in the view prefs and the next tab switch
                // restores it as a phantom draft (resendable verbatim).
                SaveWorkerViewState(_lastViewKey);
            }
            StatusText.Text = receipt.RunHealthy
                ? $"Pi replied with {text.Length:N0} characters — healthy run (correctness needs separate acceptance checks)."
                : $"Pi run finished — {receipt.Display}";
        }
        catch (OperationCanceledException)
        {
            StopFlushTimerIfIdle();
            FlushLive(); // Keep whatever streamed before cancellation — never drop partial work.
            _feed.EndAssistantTurn(_liveSeq, null);
            _feed.AddCancelNotice("Run cancelled — partial streamed text above is kept as-is. History refreshes on next connect.");
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

    /// Send/Stop composer button (B1): one control, two modes. Enablement
    /// mirrors the context-menu guards exactly (ComposerActionPolicy); the
    /// click reuses the single tested handlers, so the button, the menu and
    /// the chords share one execution path. Refreshed on text, selection,
    /// connection and run-state changes.
    private void RefreshComposerSendButton()
    {
        var (connected, busy) = SelectedWorker is { } worker
            ? (worker.IsConnected, _busyWorkers.Contains(worker.Id))
            : (_conversation?.IsConnected == true, _piBusy);
        var canSend = ComposerActionPolicy.CanSend(connected, busy, PiPromptBox.Text);
        ComposerSendButton.IsEnabled = canSend || ComposerActionPolicy.CanStop(busy);
        if (busy)
        {
            ComposerSendIcon.Kind = AxIconKind.Stop;
            ComposerSendIcon.Filled = true;
            Avalonia.Controls.ToolTip.SetTip(ComposerSendButton, "Stop current run (Escape)");
            ComposerSendButton.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Stop current run");
        }
        else
        {
            ComposerSendIcon.Kind = AxIconKind.Send;
            ComposerSendIcon.Filled = false;
            Avalonia.Controls.ToolTip.SetTip(ComposerSendButton, "Send message (Ctrl+Enter)");
            ComposerSendButton.SetValue(Avalonia.Automation.AutomationProperties.NameProperty, "Send message");
        }
    }

    private void ComposerSendButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var busy = SelectedWorker is { } worker ? _busyWorkers.Contains(worker.Id) : _piBusy;
        if (busy)
        {
            ComposerStop_OnClick(sender, e);
        }
        else
        {
            ComposerSend_OnClick(sender, e);
        }
    }

    private void PiPromptBox_OnTextChanged(object? sender, Avalonia.Controls.TextChangedEventArgs e)
    {
        RefreshComposerSendButton();
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
        var showDiagnostics = ShowDiagnostics();
        // Structural = anything derived views must re-read (new rows, tool
        // closes, reconciles). Pure streaming growth skips the map/overview
        // rebuild: the tick already shows the text and the live activity.
        var structural = false;
        for (var si = 0; si < snapshot.Count; si++)
        {
            var entry = snapshot[si];
            if (!ConversationViewItem.ShouldShow(entry, showDiagnostics))
            {
                // Folded transport detail: no view row, but the sequence
                // frontier still advances past it.
                _shownSeq = Math.Max(_shownSeq, entry.Seq);
                continue;
            }

            if (!bySeq.TryGetValue(entry.Seq, out var item))
            {
                if (entry.Seq <= _shownSeq)
                {
                    continue; // Reset path owns older seqs; never resurrect.
                }

                item = ToViewItem(entry);
                // Group rhythm from VISIBLE neighbours (folded detail rows
                // take no space): one operation joins tight, turn anchors
                // keep air.
                item.RowMargin = ConversationViewItem.GroupMargins(
                    VisibleNeighborRole(snapshot, si, -1, showDiagnostics),
                    entry,
                    VisibleNeighborRole(snapshot, si, 1, showDiagnostics));
                item.IsFinalReport = ConversationViewItem.ClosesReport(snapshot, si, showDiagnostics);
                _conversationItems.Add(item);
                bySeq[entry.Seq] = item;
                // A fresh receipt closes its cluster's assistant (the row
                // opened before the tools ran, possibly far above); the
                // row above also gained a neighbour below: rejoin it.
                if (entry.Role == "receipt")
                {
                    for (var pi = si - 1; pi >= 0; pi--)
                    {
                        if (!ConversationViewItem.ShouldShow(snapshot[pi], showDiagnostics))
                        {
                            continue;
                        }

                        if (snapshot[pi].Role == "user")
                        {
                            break;
                        }

                        if (snapshot[pi].Role == "assistant"
                            && bySeq.TryGetValue(snapshot[pi].Seq, out var prev)
                            && prev.Kind == FeedBlockKind.Assistant)
                        {
                            prev.IsFinalReport = true;
                            break;
                        }
                    }
                }

                for (var pi = si - 1; pi >= 0; pi--)
                {
                    if (!ConversationViewItem.ShouldShow(snapshot[pi], showDiagnostics))
                    {
                        continue;
                    }

                    if (bySeq.TryGetValue(snapshot[pi].Seq, out var above)
                        && !ReferenceEquals(above, item))
                    {
                        above.RowMargin = ConversationViewItem.GroupMargins(
                            VisibleNeighborRole(snapshot, pi, -1, showDiagnostics),
                            snapshot[pi],
                            entry.Role);
                    }

                    break;
                }

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
        // Same history-prefix strip as ToViewItem (expanded and copy paths
        // keep the full text).
        var displaySource = item.Kind == FeedBlockKind.Tool
            && ConversationViewItem.HistoryToolPresentation(entry.Note, entry.Text) is { } historyStripped
            ? historyStripped.DisplayBody
            : body;
        item.DisplayText = item.IsExpanded ? FeedEntry.TruncateExpanded(entry.Text) : ConversationViewItem.StreamingDisplay(FeedEntry.TruncateForDisplay(displaySource, cap), entry.IsStreaming && item.Kind == FeedBlockKind.Assistant);
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
    /// The view additionally folds historic detail rows unless Full (see
    /// ConversationViewItem.ShouldShow); the combo handler rebuilds the
    /// list so the change applies at once.
    private bool ShowDiagnostics() => _prefs.Visibility == "Full";

    /// Previous/next VISIBLE feed role around an index (folded transport
    /// detail takes no space in rhythm or report detection).
    private static string? VisibleNeighborRole(
        IReadOnlyList<FeedEntry> snapshot, int index, int step, bool showDiagnostics)
    {
        for (var i = index; i >= 0 && i < snapshot.Count; i += step)
        {
            if (ConversationViewItem.ShouldShow(snapshot[i], showDiagnostics))
            {
                return snapshot[i].Role;
            }
        }

        return null;
    }

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
        // History tool rows render without their redundant first line (the
        // name/outcome prefix promoted into the header above); truncation
        // and copy still see the full text.
        var displaySource = kind == FeedBlockKind.Tool
            && ConversationViewItem.HistoryToolPresentation(entry.Note, entry.Text) is { } historyStripped
            ? historyStripped.DisplayBody
            : body;
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
            ConversationViewItem.StreamingDisplay(FeedEntry.TruncateForDisplay(displaySource, cap), entry.IsStreaming && kind == FeedBlockKind.Assistant),
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
        // History-restored tool rows carry no Note: the name and outcome live
        // in the body's first line. Promote them into the header so the row
        // reads as one coherent unit ("powershell · finished #27") instead of
        // echoing the seq ("[Tool #27] #27"). Full text and copy untouched.
        if (entry.Note is null && kind == FeedBlockKind.Tool
            && ConversationViewItem.HistoryToolPresentation(null, entry.Text) is { } history)
        {
            return $"[{label} #{entry.Seq}] {history.Note}";
        }

        return entry.Note is null
            ? $"[{label} #{entry.Seq}]"
            : $"[{label} #{entry.Seq}] {entry.Note}";
    }

    /// <summary>One coherent tool-operation appearance: pending grey while
    /// open, green on success, red on failure. Read from the lifecycle note
    /// and error flag the feed maintains.</summary>
    private static Avalonia.Media.IBrush ToolStateBrush(FeedEntry entry)
    {
        // History-restored failures carry no error flag (only the "[tool X
        // failed]" first line), so sniff the same presentation parse.
        if (entry.IsError
            || (entry.Note is null
                && ConversationViewItem.HistoryToolPresentation(null, entry.Text) is { IsError: true }))
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
        // Same history-failure sniff as the state brush (see above).
        if (entry.IsError
            || (entry.Note is null
                && ConversationViewItem.HistoryToolPresentation(null, entry.Text) is { IsError: true }))
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
        // Preserve reading position across rebuilds (tab switches and
        // appearance changes recreate every row): only a first fill jumps
        // to the end. A posted per-tab offset restore may still override
        // this afterwards; without one, the reader stays where they were.
        var wasEmpty = _conversationItems.Count == 0;
        var savedOffset = wasEmpty ? 0 : ConversationScrollOffset();
        _conversationItems.Clear();
        _shownSeq = 0;
        var entries = ActiveFeed.Snapshot();
        var showDiagnostics = ShowDiagnostics();
        var visible = new List<FeedEntry>(entries.Count);
        foreach (var candidate in entries)
        {
            if (ConversationViewItem.ShouldShow(candidate, showDiagnostics))
            {
                visible.Add(candidate);
            }
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var item = ToViewItem(visible[i]);
            item.RowMargin = ConversationViewItem.GroupMargins(
                i > 0 ? visible[i - 1].Role : null,
                visible[i],
                i + 1 < visible.Count ? visible[i + 1].Role : null);
            item.IsFinalReport = ConversationViewItem.ClosesReport(visible, i, showDiagnostics);
            _conversationItems.Add(item);
            _shownSeq = Math.Max(_shownSeq, visible[i].Seq);
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
            if (wasEmpty)
            {
                ConversationList.ScrollIntoView(_conversationItems[^1]);
            }
            else
            {
                SetConversationScrollOffset(savedOffset);
            }
        }

        EnsureFeedScroller();
        UpdateEmptyState();
        UpdateJumpToLiveVisibility();
    }

    /// Follow-logic: honour the AutoScroll preference but never yank the
    /// reader away from older messages. Pure for tests; the host supplies
    /// live viewer metrics and falls back to the preference alone while
    /// the viewer is not realized.
    internal static bool ShouldFollow(bool autoScrollPref, double offsetY, double viewportH, double extentH)
    {
        if (!autoScrollPref)
        {
            return false;
        }

        if (viewportH <= 0 || extentH <= 0)
        {
            return true;
        }

        return offsetY + viewportH >= extentH - 32;
    }

    private (double Viewport, double Extent) ConversationScrollMetrics()
    {
        try
        {
            var viewer = ConversationList
                .GetVisualDescendants()
                .OfType<Avalonia.Controls.ScrollViewer>()
                .FirstOrDefault();
            if (viewer is null)
            {
                return (0, 0);
            }

            return (viewer.Viewport.Height, viewer.Extent.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    /// Jump-to-Live visibility rule: offer the jump whenever rows exist
    /// and the reader is not pinned to the bottom. Pure for tests.
    internal static bool ShouldShowJumpToLive(int itemCount, double offsetY, double viewportH, double extentH)
        => itemCount > 0 && !ShouldFollow(true, offsetY, viewportH, extentH);

    private Avalonia.Controls.ScrollViewer? _feedScroller;

    /// Resolve the feed scroller once it exists and follow its movement so
    /// the Jump control tracks manual scrolling without polling.
    private void EnsureFeedScroller()
    {
        if (_feedScroller is not null)
        {
            return;
        }

        var viewer = ConversationList
            .GetVisualDescendants()
            .OfType<Avalonia.Controls.ScrollViewer>()
            .FirstOrDefault();
        if (viewer is null)
        {
            return;
        }

        _feedScroller = viewer;
        _feedScroller.ScrollChanged += (_, _) => UpdateJumpToLiveVisibility();
    }

    private void UpdateJumpToLiveVisibility()
    {
        var metrics = ConversationScrollMetrics();
        var show = ShouldShowJumpToLive(
            _conversationItems.Count, ConversationScrollOffset(), metrics.Viewport, metrics.Extent);
        JumpToLiveButton?.IsVisible = show;
    }

    private void MaybeAutoScroll()
    {
        if (_conversationItems.Count == 0)
        {
            UpdateJumpToLiveVisibility();
            return;
        }

        EnsureFeedScroller();
        var metrics = ConversationScrollMetrics();
        if (ShouldFollow(_prefs.AutoScroll, ConversationScrollOffset(), metrics.Viewport, metrics.Extent))
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }

        UpdateJumpToLiveVisibility();
    }

    /// Explicit reader action: jump to the latest message. Following
    /// resumes by position when the preference allows — nothing is
    /// latched, so this never fights manual scrolling.
    private void JumpToLive_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_conversationItems.Count == 0)
        {
            return;
        }

        ConversationList.ScrollIntoView(_conversationItems[^1]);
        UpdateJumpToLiveVisibility();
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
        PaintRailButton(NavOverview, NavOverviewIcon, _page == AppPage.Overview);
        PaintRailButton(NavLive, NavLiveIcon, _page == AppPage.Live);
        PaintRailButton(NavSettings, NavSettingsIcon, _page == AppPage.Settings);
    }

    /// Approved shell selection: dark-teal pill plus the green icon.
    /// Painted in code (Fluent checked styles lose to the theme); icon
    /// strokes persist because AxIcon only refreshes on Kind/Filled change.
    private static void PaintRailButton(Avalonia.Controls.Primitives.ToggleButton button, AxIcon icon, bool selected)
    {
        button.Background = selected ? AxTheme.RailSelectedBrush : Avalonia.Media.Brushes.Transparent;
        icon.Stroke = selected ? AxTheme.GreenBrush : AxTheme.IconBrush;
    }

    /// Developer Layout Inspector (B2): F12 toggles the guide overlay.
    /// No chord conflicts (Ctrl+Enter/Escape/find are untouched); the
    /// overlay is hit-test invisible, so Pi operation is unaffected.
    private void Window_OnPreviewKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.F12)
        {
            LayoutOverlay.IsVisible = !LayoutOverlay.IsVisible;
            if (LayoutOverlay.IsVisible)
            {
                PostLayoutRefresh();
            }

            e.Handled = true;
        }
    }

    /// Measure the registered regions in overlay coordinates and draw the
    /// datum guides plus the pass/fail readout. Overlay-only: no control
    /// is moved, resized or restyled. Refreshed on toggle, resize and page
    /// switch — never continuously, so it cannot drive a layout loop.
    private void RefreshLayoutOverlay()
    {
        LayoutOverlay.Children.Clear();
        var rects = new Dictionary<string, Avalonia.Rect>();
        void Add(string name, Avalonia.Controls.Control? element)
        {
            // Hidden pages keep stale bounds, and Avalonia does not
            // propagate IsVisible down the tree — so walk the ancestors:
            // any explicitly hidden panel disqualifies the whole subtree.
            if (element is null || !IsEffectivelyVisible(element))
            {
                return;
            }

            rects[name] = new Avalonia.Rect(ToOverlay(element), element.Bounds.Size);
        }

        Add("cards", CardStrip);
        Add("cardsDiv", CardsDivider);
        Add("feed", ConversationList);
        Add("composer", ComposerBox);
        Add("footer", FooterBar);
        Add("rabbit", BrandIcon);
        Add("nav", NavLiveIcon);
        Add("brand", BrandText);
        Add("send", ComposerSendButton);
        Add("input", PiPromptBox);
        Add("headerDiv", HeaderDivider);
        Add("overview", OverviewPanel);
        Add("overviewContent", OverviewContent);
        Add("status", OvStateText);
        Add("engine", FooterEngineText);
        Add("session", OvSessionName);
        Add("add", AddContextButton);
        var infos = new List<string>();
        Ink(infos, rects, "rabbit", BrandIcon);
        Ink(infos, rects, "nav-overview", NavOverviewIcon);
        Ink(infos, rects, "nav-feed", NavLiveIcon);
        Ink(infos, rects, "nav-settings", NavSettingsIcon);
        Ink(infos, rects, "add", AxAddIcon);
        Ink(infos, rects, "search", AxSearchIcon);
        Ink(infos, rects, "send", ComposerSendIcon);
        rects["overlay"] = new Avalonia.Rect(new Avalonia.Point(0, 0), LayoutOverlay.Bounds.Size);

        var deviations = LayoutAudit.Check(rects).ToList();
        if (rects.TryGetValue("overviewContent", out var contentRect)
            && rects.TryGetValue("overview", out var viewportRect)
            && LayoutAudit.CheckFill(contentRect, viewportRect, AxLayout.ContentMaxWidth) is { } fill)
        {
            // Capped-panel fill (B2 regression net): empty and populated
            // share one width at and above the reading cap.
            deviations.Add(fill);
        }
        // Right-hand metadata never crosses the content datum (ellipsis
        // owns the text; the layout never overflows). Bound to the cards
        // edge: the shared content-right datum.
        deviations.AddRange(LayoutAudit.CheckClipping(rects, "cards", "status", "engine", "session", "add"));
        // Global ink verdict: the rabbit's visible centre against the
        // navigation ink below it (see CheckRailInk).
        if (LayoutAudit.CheckRailInk(rects) is { } railInk)
        {
            deviations.Add(railInk);
        }
        foreach (var region in new[] { "cards", "feed", "composer", "footer", "rabbit", "send", "input" })
        {
            if (rects.TryGetValue(region, out var frame))
            {
                Frame(frame, AxTheme.DimBrush);
            }
        }

        GuideFor("content-left", deviations, rects);
        GuideFor("content-right", deviations, rects);
        GuideFor("rail-brand", deviations, rects);
        GuideFor("send-input", deviations, rects);

        var lines = deviations
            .Select(d => $"{(d.Delta <= LayoutAudit.Tolerance ? "PASS" : "FAIL")} {d.Name} Δ{d.Delta:F1}")
            .Concat(infos);
        var report = new TextBlock
        {
            Text = "Guides (F12)\n" + string.Join("\n", lines),
            FontFamily = AxTheme.MonoFont,
            FontSize = 11,
            Foreground = AxTheme.TextBrush,
            Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(210, 10, 14, 20)),
            Padding = new Avalonia.Thickness(8),
        };
        Canvas.SetLeft(report, Math.Max(8, LayoutOverlay.Bounds.Width - 330));
        Canvas.SetTop(report, 8);
        LayoutOverlay.Children.Add(report);
    }

    /// Optical-ink rect of one icon in overlay coordinates. MEASURED
    /// rendering fits the geometry box to the slot from its TOP-LEFT
    /// (Uniform scales to fit but does not centre — verified pixel by
    /// pixel: full-bleed icons look centred, narrow art hugs the left).
    /// AxIcon.RenderTransform (optical centering) then corrects per icon,
    /// which this models by adding the live translation. The rect feeds
    /// both the per-icon INFO line and the global rail-ink comparison,
    /// so F12 reports rendered truth, never the layout assumption.
    /// Hidden icons contribute nothing (stale bounds would indict the
    /// wrong page).
    private void Ink(List<string> infos, Dictionary<string, Avalonia.Rect> rects, string name, AxIcon? icon)
    {
        if (icon?.Data is null || !IsEffectivelyVisible(icon))
        {
            return;
        }

        var origin = ToOverlay(icon);
        var size = icon.Bounds.Size;
        var inkBox = icon.Data.Bounds;
        if (inkBox.Width <= 0 || inkBox.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(size.Width / inkBox.Width, size.Height / inkBox.Height);
        if (scale <= 0)
        {
            return;
        }

        var inkSize = new Avalonia.Size(inkBox.Width * scale, inkBox.Height * scale);
        var ink = new Avalonia.Rect(origin, inkSize);
        // Render-time optical centering (AxIcon.RenderTransform) moves
        // the ink after layout: the global verdict must see the final
        // pixels, not the unshifted slot math.
        if (icon.RenderTransform is Avalonia.Media.TranslateTransform shift)
        {
            ink = new Avalonia.Rect(ink.X + shift.X, ink.Y + shift.Y, ink.Width, ink.Height);
        }

        rects["ink-" + name] = ink;
        var (dx, dy) = LayoutAudit.InkOffset(new Avalonia.Rect(origin, size), ink);
        infos.Add($"INFO ink-{name} ({dx:+0.0;-0.0},{dy:+0.0;-0.0}) box={inkBox.X:F1},{inkBox.Y:F1},{inkBox.Width:F1},{inkBox.Height:F1}");
    }

    /// One datum guide per check: verticals for column edges and the rail
    /// datum, a horizontal for the Send/input centres. Colour carries the
    /// verdict (green within tolerance, red beyond).
    /// Origin of an element in overlay coordinates: sum layout bounds to
    /// the visual root for both the element and the overlay, then take
    /// the difference (window screen offset cancels). All registered
    /// targets are non-scrolling containers with no render transforms, so
    /// the sum is exact.
    private Avalonia.Point ToOverlay(Avalonia.Controls.Control element)
    {
        var e = ToRoot(element);
        var o = ToRoot(LayoutOverlay);
        return new Avalonia.Point(e.X - o.X, e.Y - o.Y);
    }

    private static Avalonia.Point ToRoot(Avalonia.Visual node)
    {
        var x = 0.0;
        var y = 0.0;
        Avalonia.Visual? current = node;
        while (current is not null)
        {
            x += current.Bounds.X;
            y += current.Bounds.Y;
            current = current.GetVisualParent();
        }

        return new Avalonia.Point(x, y);
    }

    /// Effective visibility: Avalonia keeps IsVisible true on children of
    /// a hidden panel (only rendering skips them), while their bounds go
    /// stale — so any explicitly hidden ancestor disqualifies the subtree.
    private static new bool IsEffectivelyVisible(Avalonia.Controls.Control element)
    {
        Avalonia.Visual? node = element;
        while (node is not null)
        {
            if (node is Avalonia.Controls.Control control && !control.IsVisible)
            {
                return false;
            }

            node = node.GetVisualParent();
        }

        return true;
    }

    private void GuideFor(
        string name,
        IReadOnlyList<GuideDeviation> deviations,
        IReadOnlyDictionary<string, Avalonia.Rect> rects)
    {
        var deviation = deviations.FirstOrDefault(d => d.Name == name);
        if (deviation is null)
        {
            return;
        }

        var ok = deviation.Delta <= LayoutAudit.Tolerance;
        var brush = ok ? AxTheme.GreenBrush : AxTheme.RedBrush;
        switch (name)
        {
            case "content-left":
            case "content-right":
                Guide(deviation.Expected, TopOf(rects, "cards"), deviation.Expected, BottomOf(rects, "footer"), brush);
                break;
            case "rail-brand":
                Guide(deviation.Expected, TopOf(rects, "rabbit"), deviation.Expected, BottomOf(rects, "nav"), brush);
                break;
            case "rail-ink":
                Guide(deviation.Expected, TopOf(rects, "rabbit"), deviation.Expected, BottomOf(rects, "nav"), brush);
                break;
            case "send-input":
                Guide(LeftOf(rects, "input"), deviation.Expected, RightOf(rects, "send"), deviation.Expected, brush);
                break;
        }
    }

    private void Guide(double x1, double y1, double x2, double y2, Avalonia.Media.IBrush brush)
    {
        LayoutOverlay.Children.Add(new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Avalonia.Point(x1, y1),
            EndPoint = new Avalonia.Point(x2, y2),
            Stroke = brush,
            StrokeThickness = 1,
            Opacity = 0.85,
        });
    }

    private void Frame(Avalonia.Rect frame, Avalonia.Media.IBrush brush)
    {
        var box = new Avalonia.Controls.Shapes.Rectangle
        {
            Width = frame.Width,
            Height = frame.Height,
            Stroke = brush,
            StrokeThickness = 1,
            Opacity = 0.45,
        };
        Canvas.SetLeft(box, frame.X);
        Canvas.SetTop(box, frame.Y);
        LayoutOverlay.Children.Add(box);
    }

    private static double TopOf(IReadOnlyDictionary<string, Avalonia.Rect> rects, string name)
        => rects.TryGetValue(name, out var r) ? r.Top : 0;

    private static double BottomOf(IReadOnlyDictionary<string, Avalonia.Rect> rects, string name)
        => rects.TryGetValue(name, out var r) ? r.Bottom : LayoutOverlayFallbackHeight(rects);

    private static double LeftOf(IReadOnlyDictionary<string, Avalonia.Rect> rects, string name)
        => rects.TryGetValue(name, out var r) ? r.Left : 0;

    private static double RightOf(IReadOnlyDictionary<string, Avalonia.Rect> rects, string name)
        => rects.TryGetValue(name, out var r) ? r.Right : 0;

    /// Measure after the layout pass: a synchronous refresh would read
    /// zero bounds from just-shown or just-switched panels (the overlay
    /// itself included). Posted work is dropped when the window is gone.
    private void PostLayoutRefresh()
    {
        // Background priority: layout and render passes triggered by the
        // resize or page switch complete first, so no member is ever
        // measured from a half-arranged tree (historic content-right
        // regression showed exactly the resize delta). Dropped when gone.
        Dispatcher.UIThread.Post(() =>
        {
            if (_closed)
            {
                return;
            }

            if (OverviewPanel.IsVisible)
            {
                ArrangeOverviewCards();
            }

            if (LayoutOverlay.IsVisible)
            {
                RefreshLayoutOverlay();
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private static double LayoutOverlayFallbackHeight(IReadOnlyDictionary<string, Avalonia.Rect> rects)
        => rects.TryGetValue("overlay", out var o) ? o.Height : 0;

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

    // Checkpoint A shell: the rail is fixed icon-only (Overview / Feed /
    // Settings); the expandable labeled rail and its preference are gone.
    // B2 returned the feed actions to the in-column toolbar, so the rail
    // is navigation only again. ViewPrefs.RailExpanded stays persisted
    // but unused.

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
        // Navigation rail holds destinations only (Overview, Feed,
        // Settings): actions live on cards, the empty state, the feed
        // menu and the composer — never as rail buttons.
        if (LayoutOverlay.IsVisible)
        {
            PostLayoutRefresh();
        }
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
    /// Overview dashboard (B2): every control shows observed values only —
    /// identity, connection, attention, milestones, engine, telemetry and
    /// activity. Quiet states wherever data is absent; nothing invented.
    /// </summary>
    private void RebuildWorkerOverview(PiWorker worker)
    {
        var label = WorkerViewKey.DisplayLabel(worker.Id, worker.Label);
        var state = worker.LastConfirmedState;
        string? sessionFile = state?.SessionFile is { } file && File.Exists(file) ? file : null;
        FillOverview(
            label,
            worker.IsConnected && state is not null,
            _busyWorkers.Contains(worker.Id),
            ActiveFeed,
            sessionFile,
            ActiveEngine(),
            worker.LatestStats);
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

        var viewingOwn = _conversation is null || entry.Key == _connectedKey;
        var sessionFile = SessionFileFor(entry);
        var name = AxTheme.WorkspaceTabLabel(entry.IsLegacy, entry.Label);
        FillOverview(
            name,
            viewingOwn && _conversation?.IsConnected == true,
            viewingOwn && _piBusy,
            _feed,
            sessionFile,
            ActiveEngine(),
            _bridge.LatestStats);
        RebuildSessionMap();
    }

    /// Session file path for an entry, or null when absent or unreadable.
    /// The card shows the file name plus size; lookup noise stays out.
    private string? SessionFileFor(CatalogEntry entry)
    {
        try
        {
            return entry.ToWorkspace(ManagedRoot).FindSessionFile()?.Path;
        }
        catch
        {
            return null;
        }
    }

    /// Fill every Overview control from observed values. Telemetry comes
    /// from the post-settle poll Core already runs (never a new RPC here);
    /// without a snapshot the Usage card says so instead of inventing.
    private void FillOverview(
        string label,
        bool connected,
        bool busy,
        ConversationFeed feed,
        string? sessionFile,
        (string? Provider, string? Model, string? Thinking, int? Messages) engine,
        StatsSnapshot? stats)
    {
        OvStateText.Text = connected
            ? (busy ? $"Working · {label}" : $"Idle · {label}")
            : $"Not connected · {label}";
        OvStateDot.Fill = connected
            ? (busy ? AxTheme.CyanBrush : AxTheme.GreenBrush)
            : AxTheme.MutedBrush;

        var entries = feed.Snapshot();
        var map = feed.BuildMap();
        var attention = OverviewSummary.AttentionFor(connected, busy, entries, map.ErrorCount);
        OvAttentionText.Text = attention.Text;
        // Quiet normal: only genuine attention takes colour (amber review,
        // red failure); the idle state stays muted, never success-green.
        OvAttentionText.Foreground = attention.Level switch
        {
            AttentionLevel.Warning => AxTheme.YellowBrush,
            AttentionLevel.Failed => AxTheme.RedBrush,
            _ => AxTheme.MutedBrush,
        };

        // Receipts and compactions are outcomes, not missions: the card
        // shows the latest work-driving milestone, or stays quiet.
        // No mission authority exists in the app (researched — tool
        // milestones are operations, not missions), so the card stays in
        // its restrained unconnected state. Tool work lives under Recent
        // Activity with the same evidence jumps.
        OvMissionTitle.Text = "No mission connected.";
        OvMissionDetail.Text = "Mission identity needs an authoritative source; none is available.";
        OvMissionEvidence.Children.Clear();

        OvModelName.Text = engine.Model ?? "—";
        OvModelDetail.Text = engine.Model is null
            ? "Pi engine not connected."
            : $"({engine.Provider ?? "?"}) · thinking {engine.Thinking ?? "?"} · messages {engine.Messages?.ToString("N0") ?? "?"}";
        if (sessionFile is null)
        {
            OvSessionName.Text = "No session file yet.";
            Avalonia.Controls.ToolTip.SetTip(OvSessionName, "Pi persists the session on the first completed run.");
        }
        else
        {
            OvSessionName.Text = $"{System.IO.Path.GetFileName(sessionFile)} ({new FileInfo(sessionFile).Length:N0} bytes)";
            Avalonia.Controls.ToolTip.SetTip(OvSessionName, sessionFile);
        }

        var receipt = entries.LastOrDefault(e => e.Role == "receipt");
        if (receipt is null)
        {
            OvVerifyTitle.Text = "Awaiting evidence";
            OvVerifyIcon.Kind = AxIconKind.Check;
            OvVerifyIcon.Tint = AxTheme.MutedBrush;
            OvVerifyDetail.Text = map.TestCommands.Count == 0
                ? "No latest verified run."
                : $"No latest verified run. Test commands (heuristic): {string.Join(" | ", map.TestCommands)}";
        }
        else
        {
            // Health is literal: only a "healthy" receipt note earns the
            // title. A neutral settle ("no reply") is neither success
            // nor failure — it says so, quietly.
            var healthy = !receipt.IsError && receipt.Note == "healthy";
            var needsReview = receipt.Note == "needs review";
            OvVerifyTitle.Text = healthy ? "Latest run healthy"
                : receipt.IsError ? "Run failed"
                : needsReview ? "Run needs review"
                : "Turn complete — no reply text";
            OvVerifyIcon.Kind = healthy || (!receipt.IsError && !needsReview) ? AxIconKind.Check : AxIconKind.Warn;
            OvVerifyIcon.Tint = healthy ? AxTheme.GreenBrush
                : receipt.IsError ? AxTheme.RedBrush
                : needsReview ? AxTheme.YellowBrush
                : AxTheme.MutedBrush;
            OvVerifyDetail.Text = receipt.Text;
        }

        // Historical errors are never discarded: they ride along here even
        // when the latest receipt quiets the title row.
        if (map.ErrorCount > 0)
        {
            OvVerifyDetail.Text += $" Session errors observed: {map.ErrorCount}.";
        }

        // Health is not verification: a clean Pi settle earns no
        // independent-verified label. Genuine verification evidence is the
        // observed test-command heuristic only — and its absence is stated,
        // never glossed.
        OvVerifyDetail.Text += map.TestCommands.Count == 0
            ? " No independent verification evidence recorded."
            : $" Observed test commands (heuristic, outcome unknown): {string.Join(" | ", map.TestCommands)}.";

        if (stats is null)
        {
            OvUsageLine.Text = "Awaiting first settled run — Pi reports telemetry after a run completes.";
            OvContextBar.IsVisible = false;
            OvUsageGauge.Percent = null;
            ToolTip.SetTip(OvUsageGauge, "Context unknown — Pi reports telemetry after a run completes.");
            OvCostLine.Text = "Est. cost: not available.";
        }
        else
        {
            OvUsageLine.Text = $"Context {stats.ContextPercent:F1}% · {stats.TotalTokens:N0} tokens · {stats.ToolCalls} tool calls.";
            OvContextBar.Value = Math.Clamp(stats.ContextPercent, 0, 100);
            OvContextBar.IsVisible = true;
            OvUsageGauge.Percent = stats.ContextPercent;
            ToolTip.SetTip(OvUsageGauge, $"Pi context usage: {stats.ContextPercent:F1}% of {stats.TotalTokens:N0} tokens.");
            OvCostLine.Text = stats.Cost > 0
                ? $"Est. cost (Pi-reported): ${stats.Cost:F4}."
                : "Est. cost: not available.";
        }

        OvActivityList.Children.Clear();
        const int collapsedMax = 3;
        const int expandedMax = 8;
        var total = OverviewSummary.GroupCount(entries);
        var groups = OverviewSummary.GroupActivity(entries, busy, _overviewActivityExpanded ? expandedMax : collapsedMax);
        if (groups.Count == 0)
        {
            OvActivityList.Children.Add(new TextBlock
            {
                Text = "No activity yet.",
                Foreground = AxTheme.MutedBrush,
                FontSize = 12,
            });
        }

        for (var gi = 0; gi < groups.Count; gi++)
        {
            OvActivityList.Children.Add(ActivityGroupRow(groups[gi], gi == 0, gi == groups.Count - 1));
        }

        if (total > groups.Count)
        {
            var more = new Button
            {
                Content = _overviewActivityExpanded
                    ? "Show fewer runs"
                    : $"Show {total - groups.Count} earlier run{(total - groups.Count == 1 ? string.Empty : "s")}",
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                Foreground = AxTheme.BlueBrush,
                Padding = new Avalonia.Thickness(4, 2, 4, 2),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            more.Click += OvActivityMore_OnClick;
            OvActivityList.Children.Add(more);
        }

        // Arrange and overlay follow through the posted refresh (never
        // synchronously: bounds are stale on the show/resize that
        // triggers this rebuild).
        PostLayoutRefresh();
    }

    private bool _overviewActivityExpanded;

    private void OvActivityMore_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _overviewActivityExpanded = !_overviewActivityExpanded;
        RebuildOverview();
    }

    /// One timeline group: a rail cell (guide segment + outcome dot) beside
    /// headline (jump to the run start), coloured status and muted tool
    /// summary. First/last cells trim the guide so it never overshoots.
    private Avalonia.Controls.Control ActivityGroupRow(OverviewSummary.ActivityGroup group, bool first, bool last)
    {
        var (dot, status) = group.Outcome switch
        {
            ActivityOutcome.Healthy => (AxTheme.GreenBrush, "Healthy"),
            ActivityOutcome.NeedsReview => (AxTheme.YellowBrush, "Needs review"),
            ActivityOutcome.Failed => (AxTheme.RedBrush, "Failed"),
            ActivityOutcome.InProgress => (AxTheme.CyanBrush, "In progress"),
            _ => (AxTheme.MutedBrush, "No outcome recorded"),
        };
        var cell = new Grid
        {
            ColumnDefinitions = new Avalonia.Controls.ColumnDefinitions("16,*"),
        };
        var rail = new Grid();
        if (!first || !last)
        {
            var guide = new Border
            {
                Width = 2,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Background = AxTheme.DimBrush,
                Opacity = 0.5,
            };
            if (first)
            {
                guide.Margin = new Avalonia.Thickness(0, 8, 0, 0);
            }
            else if (last)
            {
                guide.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                guide.Height = 8;
            }

            rail.Children.Add(guide);
        }

        rail.Children.Add(new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = dot,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(0, 4, 0, 0),
        });
        Grid.SetColumn(rail, 0);
        cell.Children.Add(rail);

        // Bottom margin lives inside the cell so the rail guide segments
        // of adjacent groups join into one continuous line.
        var content = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(0, 0, 0, 8) };
        var headline = new Button
        {
            // Long headlines wrap inside the button instead of clipping
            // past the card at narrow widths; the 100-char Headline bound
            // still caps pathological prompts.
            Content = new TextBlock
            {
                Text = OverviewSummary.Headline(group.Prompt),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            },
            Tag = group.Prompt.Seq,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = AxTheme.TextBrush,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            FontSize = 13,
            Padding = new Avalonia.Thickness(4, 2, 4, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        headline.Click += Milestone_OnClick;
        Avalonia.Controls.ToolTip.SetTip(headline, $"Open run start in Feed (#{group.Prompt.Seq})");
        content.Children.Add(headline);

        var receiptSeq = group.Items.LastOrDefault(e => e.Role == "receipt")?.Seq ?? group.Prompt.Seq;
        var statusRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
        statusRow.Children.Add(new TextBlock { Text = status, Foreground = dot, FontSize = 12 });
        statusRow.Children.Add(new TextBlock { Text = $"· #{receiptSeq}", Foreground = AxTheme.MutedBrush, FontSize = 12 });
        content.Children.Add(statusRow);

        content.Children.Add(new TextBlock
        {
            Text = group.Summary,
            Foreground = AxTheme.MutedBrush,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        Grid.SetColumn(content, 1);
        cell.Children.Add(content);
        return cell;
    }

    /// Responsive dual cards (refinement): side by side when the page is
    /// wide, stacked when narrow. Idempotent; runs on every rebuild and on
    /// window resize while Overview shows.
    private void ArrangeOverviewCards()
    {
        if (!OverviewPanel.IsVisible)
        {
            return;
        }

        var stacked = OverviewPanel.Bounds.Width < 700;
        OvDualGrid.ColumnDefinitions = stacked
            ? new Avalonia.Controls.ColumnDefinitions("*")
            : new Avalonia.Controls.ColumnDefinitions("*,*");
        OvDualGrid.RowDefinitions = stacked
            ? new Avalonia.Controls.RowDefinitions("Auto,Auto")
            : new Avalonia.Controls.RowDefinitions();
        Grid.SetRow(OvVerifyCard, stacked ? 1 : 0);
        Grid.SetColumn(OvVerifyCard, stacked ? 0 : 1);
    }

    /// Quiet evidence-jump link: transparent, left-aligned, opens the feed
    /// row on click (Milestone_OnClick jumps by evidence seq).
    private Button JumpButton(string label, long seq)
    {
        var jump = new Button
        {
            Content = label,
            Tag = seq,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground = AxTheme.BlueBrush,
            Padding = new Avalonia.Thickness(4, 2, 4, 2),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        jump.Click += Milestone_OnClick;
        return jump;
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

    /// The disconnected Feed offers Connect where the work starts.
    /// Empty feed: the centered empty-state button. Non-empty feed (a
    /// dropped connection mid-history): the composer Reconnect button
    /// beside the status line — reachable by mouse, keyboard tab and
    /// UIA, never menu-only. Never a Pi launch without the explicit
    /// press (same guarded handlers).
    private void RefreshEmptyConnect()
    {
        var empty = _conversationItems.Count == 0;
        var connected = SelectedWorker is { } worker
            ? worker.IsConnected
            : _conversation?.IsConnected == true;
        var busy = SelectedWorker is { } w
            ? _busyWorkers.Contains(w.Id) || _connectingWorkers.Contains(w.Id)
            : _piBusy || _connecting;
        EmptyConnectButton.IsVisible = empty && !connected && !busy;
        ComposerReconnectButton.IsVisible = !empty && !connected && !busy;
    }

    private void UpdateEmptyState()
    {
        var empty = _conversationItems.Count == 0;
        EmptyStatePanel.IsVisible = empty;
        RefreshEmptyConnect();
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
                ? $"No conversation in '{name}' yet. Connect, then ask your first question."
                : $"'{name}' is quiet. Ask anything below to begin.";
        }
    }

    private void ApplyTheme()
    {
        // The composer carries no focus adorner (FocusAdorner unset in
        // XAML): the caret and the composer border carry focus state, so
        // the system-blue ring never appears. The Fluent focus visual
        // lives in the adorner layer, where element resources cannot
        // reach it — hence removal, not recolouring.
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
            FeedCombo.SelectedItem = AxFeed.DisplayName(AxFeed.Parse(_viewPrefs.FeedAppearance));
            FeedSizeCombo.SelectedItem = AxFeed.ClampSize(_viewPrefs.FeedFontSize);
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

        // The view folds historic detail rows (see ShouldShow): rebuild so
        // the new level applies to existing rows at once, not just new ones.
        RebuildConversationList();
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

    /// Beautiful Feed appearance + size: same content and Pi colours, only
    /// the voice changes. Fonts repoint live; rows rebuild through the
    /// existing RebuildConversationList path (same cost profile as a tab
    /// switch — a rare explicit action, never the streaming path). The
    /// choice persists in view prefs (Core preferences untouched).
    private void FeedCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPrefs || FeedCombo.SelectedItem is not string name)
        {
            return;
        }

        ApplyFeedAppearance(AxFeed.Parse(name), _viewPrefs.FeedFontSize);
    }

    private void FeedSizeCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingPrefs || FeedSizeCombo.SelectedItem is not double size)
        {
            return;
        }

        ApplyFeedAppearance(AxFeed.Parse(_viewPrefs.FeedAppearance), size);
    }

    private void ApplyFeedAppearance(AxFeedAppearance appearance, double fontSize)
    {
        AxTheme.ApplyAppearance(appearance, fontSize);
        _viewPrefs.FeedAppearance = AxFeed.DisplayName(appearance);
        _viewPrefs.FeedFontSize = AxTheme.FeedFontSize;
        try
        {
            SaveViewPrefs();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save view preferences: {ex.Message}";
        }

        RebuildConversationList();
    }

    /// Native dark title bar once the handle exists; all window behaviour
    /// stays native and untouched. Best-effort (see AxTheme). Applied at
    /// Opened, re-asserted on every activation and twice more shortly after
    /// Opened: DWM attributes proved flaky across identical runs (bright
    /// system-blue captions in some captures), consistent with a handle that
    /// is not ready yet or attributes dropped on re-activation.
    private void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        ApplyTitleBar();
        InitApprovedShell();
    }

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
        var modelTip = _settingsRuntime.CanConfigureModels
            ? "Click to change (verified Core operation)"
            : $"Selection unavailable: {_settingsRuntime.ModelUnavailableReason}";
        var thinkingTip = _settingsRuntime.CanConfigureThinking
            ? "Click to change (verified Core operation)"
            : $"Selection unavailable: {_settingsRuntime.ThinkingUnavailableReason}";
        Avalonia.Controls.ToolTip.SetTip(ModelButton, $"Engine model. {modelTip}");
        Avalonia.Controls.ToolTip.SetTip(ThinkingButton, $"Thinking level. {thinkingTip}");
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
                DirectoryButtonText.Text = worker.ProjectRoot;
                Avalonia.Controls.ToolTip.SetTip(DirectoryButton, $"{worker.ProjectRoot} (reading Git state…)");
                UpdateGitTooltipAsync(worker.Id, worker.ProjectRoot);
                return;
            }

            AgentXWorkspace? workspace = _connectedWorkspace;
            if (workspace is null && SelectedEntry is { } entry)
            {
                workspace = entry.ToWorkspace(ManagedRoot);
            }

            DirectoryButtonText.Text = workspace?.WorkingDirectory ?? "Select a workspace…";
        }
        catch
        {
            DirectoryButtonText.Text = "Select a workspace…";
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
    /// Real engine flyout: current value is genuine (get_state); when the
    /// Core runtime reports configurable, the live lists render as real
    /// choices in this same surface. Otherwise the honest Core reason is
    /// shown instead of a nonfunctional picker. One Flyout per button,
    /// content refreshed on every open — repeated clicks never stack popups.
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
                ? "Loading options…"
                : _settingsRuntime.UnavailableReason,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#82889A")),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return panel;
    }

    /// <summary>
    /// Close any open engine chooser: a tab switch ends the bound
    /// interaction, so a stale choice can never apply to another worker.
    /// The click-time <see cref="EngineChooserPolicy"/> check is the
    /// backstop; this is the primary close on selection change.
    /// </summary>
    private void CloseEngineChoosers()
    {
        _modelFlyout?.Hide();
        _thinkingFlyout?.Hide();
    }

    private static Avalonia.Controls.StackPanel EngineNotePanel(string title, string current, string note)
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
            Text = note,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#82889A")),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        return panel;
    }

    private async void ModelButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Capture the worker for the FULL interaction: every later list
        // and select call goes through this bound instance, never through
        // the current-tab provider, so a tab switch mid-load or
        // pre-selection cannot retarget the choice onto another worker.
        var captured = SelectedWorker;
        var engine = ActiveEngine();
        var current = engine.Model is null
            ? "No engine connected."
            : $"Current: ({engine.Provider ?? "?"}) {engine.Model}";
        _modelFlyout ??= new Avalonia.Controls.Flyout();
        _modelFlyout.Content = BuildEngineFlyoutPanel("Engine model", current);
        _modelFlyout.ShowAt(ModelButton);
        if (captured is null)
        {
            return;
        }

        var bound = new PiWorkerEngineSettings(captured);
        var capturedId = captured.Id;
        if (CoreEngineSettingsRuntime.DescribeModelsUnavailable(bound) is { } unavailable)
        {
            _modelFlyout.Content = EngineNotePanel("Engine model", current, unavailable);
            return;
        }

        IReadOnlyList<PiModelOption> options;
        try
        {
            options = await bound.GetModelsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"Cannot list models: {ex.Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!EngineChooserPolicy.MayApplyTo(capturedId, SelectedWorker?.Id))
            {
                _modelFlyout.Content = EngineNotePanel("Engine model", current,
                    "Worker changed while loading — reopen the chooser for the current worker. Nothing applied.");
                return;
            }

            var panel = BuildEngineFlyoutPanel("Engine model", current);
            // Replace the trailing "Loading…" row with the real choices.
            panel.Children.RemoveAt(panel.Children.Count - 1);
            if (options.Count == 0)
            {
                panel.Children.Add(new Avalonia.Controls.TextBlock { Text = "This connection reported no models.", FontSize = 12 });
            }

            foreach (var m in options)
            {
                var caption = $"({m.Provider}) {m.Name}";
                var item = new Button { Content = caption };
                item.Classes.Add("AxAction");
                var provider = m.Provider;
                var modelId = m.Id;
                item.Click += async (_, _) =>
                {
                    if (!EngineChooserPolicy.MayApplyTo(capturedId, SelectedWorker?.Id))
                    {
                        _modelFlyout.Hide();
                        StatusText.Text = "Worker changed — reopen the chooser for the current worker. Nothing applied.";
                        return;
                    }

                    try
                    {
                        await bound.SetModelAsync(provider, modelId, CancellationToken.None).ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _modelFlyout.Hide();
                            RefreshEngineSegments();
                            RefreshOverviewAndSettings();
                            StatusText.Text = $"Model switched to ({provider}) {modelId} — confirmed by engine state.";
                        });
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"Model switch failed: {ex.Message}");
                    }
                };
                panel.Children.Add(item);
            }

            _modelFlyout.Content = panel;
        });
    }

    private async void ThinkingButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Same worker binding as the model chooser: capture once, verify
        // at render and at selection, never resolve the current tab late.
        var captured = SelectedWorker;
        var engine = ActiveEngine();
        var current = engine.Thinking is null ? "No engine connected." : $"Current: {engine.Thinking}";
        _thinkingFlyout ??= new Avalonia.Controls.Flyout();
        _thinkingFlyout.Content = BuildEngineFlyoutPanel("Thinking level", current);
        _thinkingFlyout.ShowAt(ThinkingButton);
        if (captured is null)
        {
            return;
        }

        var bound = new PiWorkerEngineSettings(captured);
        var capturedId = captured.Id;
        if (CoreEngineSettingsRuntime.DescribeThinkingUnavailable(bound) is { } unavailable)
        {
            _thinkingFlyout.Content = EngineNotePanel("Thinking level", current, unavailable);
            return;
        }

        IReadOnlyList<string> levels;
        try
        {
            levels = await bound.GetThinkingLevelsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"Cannot list thinking levels: {ex.Message}");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!EngineChooserPolicy.MayApplyTo(capturedId, SelectedWorker?.Id))
            {
                _thinkingFlyout.Content = EngineNotePanel("Thinking level", current,
                    "Worker changed while loading — reopen the chooser for the current worker. Nothing applied.");
                return;
            }

            var panel = BuildEngineFlyoutPanel("Thinking level", current);
            panel.Children.RemoveAt(panel.Children.Count - 1);
            if (levels.Count == 0)
            {
                panel.Children.Add(new Avalonia.Controls.TextBlock { Text = "This model reported no thinking levels.", FontSize = 12 });
            }

            foreach (var level in levels)
            {
                var item = new Button { Content = level };
                item.Classes.Add("AxAction");
                var capturedLevel = level;
                item.Click += async (_, _) =>
                {
                    if (!EngineChooserPolicy.MayApplyTo(capturedId, SelectedWorker?.Id))
                    {
                        _thinkingFlyout.Hide();
                        StatusText.Text = "Worker changed — reopen the chooser for the current worker. Nothing applied.";
                        return;
                    }

                    try
                    {
                        await bound.SetThinkingLevelAsync(capturedLevel, CancellationToken.None).ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            _thinkingFlyout.Hide();
                            RefreshEngineSegments();
                            RefreshOverviewAndSettings();
                            StatusText.Text = $"Thinking level switched to {capturedLevel} — confirmed by engine state.";
                        });
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => StatusText.Text = $"Thinking switch failed: {ex.Message}");
                    }
                };
                panel.Children.Add(item);
            }

            _thinkingFlyout.Content = panel;
        });
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
