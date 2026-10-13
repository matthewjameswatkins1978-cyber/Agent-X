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
        ApplyTheme();
        ThemeCombo.ItemsSource = AgentXPreferences.KnownThemes;
        _syncingPrefs = true;
        try
        {
            AutoScrollCheck.IsChecked = _prefs.AutoScroll;
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
            SaveWorkerViewState(_coordinator.ActiveKey);
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
        RefreshDirectoryLabel();
        if (!result.Accepted)
        {
            StatusText.Text = $"Staying on '{_connectedLabel}' — {result.RefusalReason} Histories never mix.";
        }
        else if (_conversation is null && SelectedEntry is { } current)
        {
            StatusText.Text = current.IsLegacy
                ? "Legacy session selected — press Connect."
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
        var label = new TextBlock { Text = entry.Label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
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
                    return;
                }
            }

            WorkspaceTabs.SelectedIndex = 0;
        }
        finally
        {
            _suppressTabEvents = priorSuppress;
        }
    }

    private void UpdateTabDots()
    {
        var index = 0;
        foreach (var item in WorkspaceTabs.Items)
        {
            if (item is TabStripItem tab
                && tab.Content is StackPanel header
                && header.Children.Count > 0
                && header.Children[0] is Avalonia.Controls.Shapes.Ellipse dot
                && tab.Tag is CatalogEntry entry)
            {
                var on = entry.Key == _connectedKey && _conversation?.IsConnected == true;
                var busy = on && _piBusy;
                dot.Fill = busy
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5C07B"))
                    : on
                        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#7DD3A8"))
                        : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#6B7280"));
                // Identity tint follows tab position; selection brightens
                // the fill so the active worker reads at a glance. State
                // itself stays on the dot + footer text, never colour alone.
                var selected = ReferenceEquals(WorkspaceTabs.SelectedItem, tab);
                var tint = Avalonia.Media.Color.Parse(WorkerTabColors.TintFor(index));
                tab.Background = new Avalonia.Media.SolidColorBrush(tint, selected ? 0.55 : 0.28);
            }

            index++;
        }
    }

    private void WorkspaceTabs_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTabEvents)
        {
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
        // always lands on the key that owns them.
        SaveWorkerViewState(_coordinator.ActiveKey);
        var result = _coordinator.RequestTransition(entry.Key);
        if (!result.Accepted)
        {
            StatusText.Text = $"Disconnect from '{_connectedLabel}' before opening '{entry.Label}' — histories never mix.";
            SelectTab(_coordinator.ActiveKey);
            RefreshDirectoryLabel();
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
        UpdateTabDots();
        RefreshDirectoryLabel();
        if (_conversation is null && !restored)
        {
            StatusText.Text = entry.IsLegacy
                ? "Legacy session selected — press Connect."
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

    private async void ConnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        await DisconnectPiAsync();
    }

    private async void ReconnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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

    private async void SendPrompt_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        WorkingRow.IsVisible = true;
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
            _flushTimer.Stop();
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
            _flushTimer.Stop();
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
            _flushTimer.Stop();
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
            _flushTimer.Stop();
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
            WorkingRow.IsVisible = false;
            UpdateTabDots();
            _promptLifetime?.Dispose();
            _promptLifetime = null;
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
        if (e.Key == Avalonia.Input.Key.Escape && _piBusy)
        {
            e.Handled = true;
            CancelPrompt_OnClick(sender, new Avalonia.Interactivity.RoutedEventArgs());
        }
    }

    private async void CancelPrompt_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
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
        ComposerSendItem.IsEnabled = ComposerActionPolicy.CanSend(
            _conversation?.IsConnected == true, _piBusy, PiPromptBox.Text);
        ComposerStopItem.IsEnabled = ComposerActionPolicy.CanStop(_piBusy);
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
        var snapshot = _feed.Snapshot();
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
            else if (!ReferenceEquals(entry.Text, item.FullText))
            {
                // Core text replaced under a live row (tool close, reconcile,
                // streaming chunks, completion reconcile). Refresh in place.
                RefreshItem(item, entry);
                if (entry.Seq == _liveSeq)
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
            item.RefreshToolDisplay();
        }
        var body = entry.IsStreaming && entry.Text.Length == 0 ? "Working…" : entry.Text;
        item.DisplayText = item.IsExpanded ? FeedEntry.TruncateExpanded(entry.Text) : FeedEntry.TruncateForDisplay(body, cap);
        if (item.Kind == FeedBlockKind.Assistant && !entry.IsStreaming && entry.Text.Length > 0 && item.Markdown is null)
        {
            item.Markdown = MarkdownLite.Parse(entry.Text);
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
    }

    /// Single bounded entry from feed Changed (any thread): at most one
    /// sync is ever queued; the run observes the latest snapshot.
    private void FeedChanged() => _syncCoalescer.Signal(SyncFeedView);

    private static readonly Avalonia.Media.IBrush UserBlockBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(PiPalette.UserBg));
    private static readonly Avalonia.Media.IBrush ToolPendingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(PiPalette.ToolPendingBg));
    private static readonly Avalonia.Media.IBrush ToolSuccessBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(PiPalette.ToolSuccessBg));
    private static readonly Avalonia.Media.IBrush ToolErrorBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(PiPalette.ToolErrorBg));
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
        return new ConversationViewItem(
            entry.Seq,
            entry.Role,
            BuildHeader(entry, kind),
            FeedEntry.TruncateForDisplay(body, cap),
            entry.Text,
            kind,
            entry.IsStreaming,
            brush,
            cap);
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
        foreach (var entry in _feed.Snapshot())
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
        if (AutoScrollCheck.IsChecked == true && _conversationItems.Count > 0)
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }
    }

    /// <summary>Move buffered deltas into the feed (10 Hz, not per delta).
    /// The feed's Changed post drives SyncFeedView; no direct UI work here.</summary>
    private void FlushLive()
    {
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
        SessionMapText.Text = _feed.BuildMap().ToDisplayText(_prefs.Visibility != "Compact");
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
        else if (_feed.GetEntryText(item.Seq) is { } current)
        {
            var entry = _feed.Snapshot().FirstOrDefault(x => x.Seq == item.Seq);
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

        ApplyPageVisibility((int)page);
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

    /// Persist view preferences. Prunes drafts ONLY against a healthy
    /// catalog: registry recovery must never delete drafts for temporarily
    /// unavailable projects. Throws on IO failure (callers keep memory and
    /// mark a persist retry); returns silently when preferences recovery
    /// disabled persistence (the startup status stands).
    private void SaveViewPrefs()
    {
        var healthy = _catalog is not null && !_catalog.IsRecovery;
        _viewPrefs.Save(
            ManagedRoot,
            knownKeys: healthy ? _catalog!.Entries.Select(e => e.Key) : null,
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
    /// Overview shows only observed facts: workspace identity, connection,
    /// session file, map-derived counts and milestones. Nothing invented.
    /// </summary>
    private void RebuildOverview()
    {
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
        if (empty && SelectedEntry is { } entry)
        {
            EmptyStateText.Text = _conversation is null
                ? $"No conversation in '{entry.Label}' yet. Connect, then ask your first question — or try a suggestion below."
                : $"'{entry.Label}' is quiet. Ask anything below to begin.";
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
            ManagedPathsText.Text =
                $"Sessions: {Path.Combine(ManagedRoot, "sessions")}\n" +
                $"Registry: {WorkspaceRegistry.RegistryPath(ManagedRoot)}\n" +
                $"Preferences: {AgentXPreferences.PreferencesPath(ManagedRoot)}\n" +
                (_catalog.IsRecovery ? "Registry state: RECOVERY (registration disabled)." : "Registry state: healthy.");
            PiInfoText.Text = _conversation?.IsConnected == true
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
            AutoScrollCheck.IsChecked = _prefs.AutoScroll;
        }
        finally
        {
            _syncingPrefs = false;
        }
    }

    private void AutoScrollCheck_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_syncingPrefs)
        {
            return;
        }

        _prefs = _prefs with { AutoScroll = AutoScrollCheck.IsChecked == true };
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
        var text = _feed.GetEntryText(item.Seq) ?? item.FullText;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = $"Copied message #{item.Seq} ({text.Length:N0} chars) byte-for-byte.";
    }

    private async void CopyFullConversation_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_feed.Count == 0)
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

        var transcript = _feed.BuildTranscriptText();
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(transcript));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
        StatusText.Text = $"Copied the full conversation ({transcript.Length:N0} chars, {_feed.Count} entries).";
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

        _pendingActivityText = activity;
        // The session map shows real observed activity, never stale idle.
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
            PiActivityText.Text = string.Empty;
            _pendingActivityText = null;
            PiConnectionState.Text = "Connection lost";
            PiRunStatus.Text = "Connection to Pi lost — session is persisted. Press Reconnect to restore.";
            RebuildSessionMap();
            RefreshOverviewAndSettings();
        });
    }

    /// <summary>
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

    /// <summary>One-line engine summary for Overview/Settings (genuine values only).</summary>
    private string EngineSummary()
        => _knownModelDisplay is null
            ? "Pi engine not connected."
            : $"Model: {_knownModelDisplay} ({_knownProvider ?? "?"}) · thinking {_knownThinking ?? "?"} · messages {_knownMessages?.ToString("N0") ?? "?"}";

    /// <summary>Footer engine segments from the last known genuine values.</summary>
    private void RefreshEngineSegments()
    {
        ModelButton.Content = _knownModelDisplay is null
            ? "Model: —"
            : $"({_knownProvider ?? "?"}) {_knownModelDisplay}";
        ThinkingButton.Content = _knownThinking is null ? "Thinking: —" : _knownThinking;
        UsageText.Text = _knownMessages is { } messages ? $"messages {messages:N0}" : "messages —";
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
        // Guard first: the picker must never move a running agent. Past the
        // guard, the existing Add flow stages the folder and requires
        // explicit confirmation — registration alone never retargets live
        // work, and the staged confirm panel is surfaced on Settings.
        if (_conversation is not null || _piBusy || _connecting)
        {
            StatusText.Text = $"Disconnect from '{_connectedLabel}' first — the folder picker never moves a running agent.";
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
            _knownModelDisplay is null
                ? "No engine connected."
                : $"Current: ({_knownProvider ?? "?"}) {_knownModelDisplay}");
        _modelFlyout.ShowAt(ModelButton);
    }

    private void ThinkingButton_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _thinkingFlyout ??= new Avalonia.Controls.Flyout();
        _thinkingFlyout.Content = BuildEngineFlyoutPanel("Thinking level",
            _knownThinking is null ? "No engine connected." : $"Current: {_knownThinking}");
        _thinkingFlyout.ShowAt(ThinkingButton);
    }

    private async Task DisconnectPiAsync()
    {
        _flushTimer.Stop();
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
        _flushTimer.Stop();
        _bridge.Detach();
        SaveWorkerViewState(_coordinator.ActiveKey);
        if (_conversation is not null)
        {
            _conversation.ActivityChanged -= Conversation_ActivityChanged;
            _conversation.ToolActivityChanged -= Conversation_ToolActivityChanged;
            _conversation.ConnectionLost -= Conversation_ConnectionLost;
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        _promptLifetime?.Dispose();
        _promptLifetime = null;
    }
}
