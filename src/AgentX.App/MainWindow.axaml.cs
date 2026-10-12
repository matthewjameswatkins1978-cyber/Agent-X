using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AgentX.Core;

namespace AgentX.App;

public partial class MainWindow : Window
{
    private const int HistoryDisplayCap = 2000;
    private const int LiveDisplayCap = 32_000;

    private TranscriptImportResult? _current;
    private PiConversation? _conversation;
    private CancellationTokenSource? _promptLifetime;
    private bool _piBusy;

    // Workspace identity: one project root, one Pi session, one execution
    // root. Switching requires disconnect first, so histories, working
    // directories and tool paths can never mix across projects. The bound
    // list comes from WorkspaceCatalog (Core): exactly the legacy entry plus
    // validated registrations, or legacy-only with a recovery reason.
    private WorkspaceCatalog _catalog = null!;
    private string? _connectedKey;
    private string? _connectedLabel;
    // Immutable owner of the live conversation: reconnect restores THIS
    // workspace even if the picker selection moved meanwhile. Set on
    // connect, cleared only on disconnect — never by selection changes.
    private AgentXWorkspace? _connectedWorkspace;
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
    private StreamingTextAccumulator? _liveAccumulator;
    private long _liveSeq = -1;
    private readonly DispatcherTimer _flushTimer;

    public MainWindow()
    {
        InitializeComponent();
        ConversationList.ItemsSource = _conversationItems;
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => FlushLive();
        LoadWorkspaceChoices(selectKey: "legacy");
    }

    // --- Workspace selector: project-aware session identity. ---

    private void LoadWorkspaceChoices(string? selectKey = null)
    {
        // Never throws for registry problems: the catalog degrades to
        // legacy-only with a recovery reason instead of crashing init.
        _catalog = WorkspaceCatalog.Load(ManagedRoot);
        AddWorkspaceButton.IsEnabled = !_catalog.IsRecovery;
        WorkspaceCombo.ItemsSource = _catalog.Entries.ToArray();
        var index = _catalog.Entries.ToList().FindIndex(c => c.Key == selectKey);
        WorkspaceCombo.SelectedIndex = index >= 0 ? index : 0;
        if (_catalog.RecoveryError is { } recovery)
        {
            StatusText.Text = recovery;
        }
    }

    private void NavCollapse_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // True collapse: hiding the panel lets the Auto column shrink to zero
        // and returns the full width to the conversation. The splitter hides
        // with it (nothing to resize) and both return on expand. No forced
        // widths anywhere, so user splitter sizing is never fought.
        var show = !NavPanel.IsVisible;
        NavPanel.IsVisible = show;
        NavSplitter.IsVisible = show;
        NavCollapseButton.Content = show ? "«" : "»";
    }

    private CatalogEntry? SelectedChoice => WorkspaceCombo.SelectedItem as CatalogEntry;

    private void WorkspaceCombo_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SelectedChoice is { } choice)
        {
            WorkspaceDetailText.Text = choice.Detail;
        }
    }

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
    }

    // Pending explicit confirmation: the picker result is staged here and
    // registered ONLY by ConfirmWorkspace_OnClick. A phantom or dismissed
    // picker can never create a workspace by itself.
    private string? _pendingWorkspacePath;

    private async Task AddWorkspaceCoreAsync()
    {
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
            LoadWorkspaceChoices(selectKey: SelectedChoice?.Key);
            StatusText.Text = $"Registry became unreadable — registration disabled, file preserved. {ex.Message}";
            return;
        }

        try
        {
            var workspace = registry.GetOrRegister(path);
            registry.Save();
            LoadWorkspaceChoices(selectKey: workspace.Slug);
            StatusText.Text = $"Workspace '{workspace.Slug}' ready — session {workspace.SessionId}. Press Connect.";
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
            if (_conversation.IsConnected && SelectedChoice?.Key == _connectedKey)
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

        if (SelectedChoice is not { } choice)
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
        var conversation = new PiConversation(() => new PiRpcClient(workspace.ToPiRpcOptions()));
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
            PiConnectionState.Text = "Connected";
            PiModelText.Text = DescribeState(state);
            var history = conversation.History;
            _feed.LoadHistory(history);
            _feed.SetActivity(null);
            RebuildConversationList();
            RebuildSessionMap();
            PiRunStatus.Text = history.Count == 0
                ? $"New session for '{choice.Label}' — no previous conversation."
                : $"Restored '{choice.Label}' with {history.Count} message(s). Send a prompt to continue.";
            StatusText.Text = $"Pi runs in {workspace.WorkingDirectory} with your user authority.";
        }
        catch (Exception ex)
        {
            conversation.ActivityChanged -= Conversation_ActivityChanged;
            conversation.ToolActivityChanged -= Conversation_ToolActivityChanged;
            conversation.ConnectionLost -= Conversation_ConnectionLost;
            await conversation.DisposeAsync();
            PiConnectionState.Text = "Connection failed";
            PiModelText.Text = ex.Message;
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
            PiModelText.Text = DescribeState(state);
            // The feed keeps whatever it already holds (including any
            // preserved partial text); the notice records the restore truth.
            var noticeSeq = _feed.AddSystemNotice(
                $"Reconnected — restored {conversation.History.Count} session message(s) (reconnect #{conversation.ReconnectCount}). Partial streamed text above is preserved as-is.");
            _conversationItems.Add(ToViewItem(LookupEntry(noticeSeq)));
            _feed.SetActivity(null);
            RebuildSessionMap();
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
        PiSendButton.IsEnabled = false;
        PiCancelButton.IsEnabled = true;
        PiConnectionState.Text = "Working…";

        var userSeq = _feed.AddUser(message);
        _conversationItems.Add(ToViewItem(LookupEntry(userSeq)));
        _liveSeq = _feed.BeginAssistantTurn();
        _conversationItems.Add(ToViewItem(LookupEntry(_liveSeq)));
        MaybeAutoScroll();
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
            _feed.EndAssistantTurn(_liveSeq, receipt);
            AddMissingReceiptItem();
            UpdateStreamingItem();
            // Settle first, render second: the map must never show a stale
            // "working" state once Pi is idle (EndAssistantTurn also clears
            // activity in Core as a backstop).
            PiConnectionState.Text = "Idle";
            PiActivityText.Text = string.Empty;
            _feed.SetActivity(null);
            RebuildSessionMap();
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
            var noticeSeq = _feed.AddSystemNotice("Run cancelled — partial streamed text above is kept as-is. History refreshes on next connect.");
            _conversationItems.Add(ToViewItem(LookupEntry(noticeSeq)));
            UpdateStreamingItem();
            _feed.SetActivity(null);
            RebuildSessionMap();
            PiConnectionState.Text = "Cancelled";
            PiActivityText.Text = string.Empty;
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
            var noticeSeq = _feed.AddSystemNotice($"Pi rejected the prompt: {ex.Error} — partial streamed text above is kept as-is.");
            _conversationItems.Add(ToViewItem(LookupEntry(noticeSeq)));
            UpdateStreamingItem();
            _feed.SetActivity(null);
            PiActivityText.Text = string.Empty;
            RebuildSessionMap();
            PiConnectionState.Text = "Idle";
        }
        catch (PiRpcTransportException ex)
        {
            _flushTimer.Stop();
            // Recorded as an error EVENT, never a synthetic receipt: the run
            // did not settle and earns no health signal either way.
            var errorSeq = _feed.AddError($"Connection to Pi lost: {ex.Message}");
            _conversationItems.Add(ToViewItem(LookupEntry(errorSeq)));
            _feed.SetActivity(null);
            RebuildSessionMap();
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
            PiSendButton.IsEnabled = true;
            PiCancelButton.IsEnabled = false;
            _promptLifetime?.Dispose();
            _promptLifetime = null;
        }
    }

    private void PiPromptBox_OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        // Predictable composer keys: Enter inserts a newline (multiline),
        // Ctrl+Enter sends exactly what is in the box. No other chords act.
        if (e.Key == Avalonia.Input.Key.Enter
            && e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control))
        {
            e.Handled = true;
            SendPrompt_OnClick(sender, new Avalonia.Interactivity.RoutedEventArgs());
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

    // --- Conversation feed view: bounded display over exact Core text. ---

    private FeedEntry LookupEntry(long seq)
        => _feed.Snapshot().First(e => e.Seq == seq);

    private static ConversationViewItem ToViewItem(FeedEntry entry)
    {
        var label = entry.Role switch
        {
            "user" => "You",
            "assistant" => "Pi",
            "tool" => "Tool",
            "receipt" => "Run receipt",
            _ => entry.Role,
        };
        var header = entry.Note is null
            ? $"[{label} #{entry.Seq}]"
            : $"[{label} #{entry.Seq}] {entry.Note}";
        var body = entry.IsStreaming && entry.Text.Length == 0 ? "Working…" : entry.Text;
        var cap = entry.IsStreaming ? LiveDisplayCap : HistoryDisplayCap;
        return new ConversationViewItem(
            entry.Seq,
            entry.Role,
            header,
            FeedEntry.TruncateForDisplay(body, cap),
            entry.Text);
    }

    private void RebuildConversationList()
    {
        _conversationItems.Clear();
        foreach (var entry in _feed.Snapshot())
        {
            _conversationItems.Add(ToViewItem(entry));
        }

        if (_conversationItems.Count > 0)
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }
    }

    private void UpdateStreamingItem()
    {
        if (_liveSeq < 0)
        {
            return;
        }

        var entry = _feed.Snapshot().FirstOrDefault(e => e.Seq == _liveSeq);
        var item = _conversationItems.FirstOrDefault(i => i.Seq == _liveSeq);
        if (entry is null || item is null)
        {
            return;
        }

        item.DisplayText = FeedEntry.TruncateForDisplay(
            entry.Text.Length == 0 ? "Working…" : entry.Text, LiveDisplayCap);
        // Keep the copy source in lockstep with the display: copy actions
        // additionally resolve the authoritative feed text by Seq.
        item.FullText = entry.Text;
        MaybeAutoScroll();
    }

    private void AddMissingReceiptItem()
    {
        var receipt = _feed.Snapshot().LastOrDefault(e => e.Role == "receipt");
        if (receipt is not null && _conversationItems.All(i => i.Seq != receipt.Seq))
        {
            _conversationItems.Add(ToViewItem(receipt));
            MaybeAutoScroll();
        }
    }

    private void MaybeAutoScroll()
    {
        if (AutoScrollCheck.IsChecked == true && _conversationItems.Count > 0)
        {
            ConversationList.ScrollIntoView(_conversationItems[^1]);
        }
    }

    /// <summary>Move buffered deltas into the feed and refresh the live view (10 Hz, not per delta).</summary>
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
        UpdateStreamingItem();
        StatusText.Text = $"Streaming… {accumulator.CommittedChars:N0} chars in {accumulator.FlushCount:N0} UI updates.";
    }

    private void RebuildSessionMap()
    {
        var verbose = VerboseMapCheck.IsChecked == true;
        SessionMapText.Text = _feed.BuildMap().ToDisplayText(verbose);
    }

    private void VerboseMapCheck_OnChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => RebuildSessionMap();

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
        // feed or the map. A queued post from a previous workspace (or a
        // disposed conversation) is dropped, never mixed into the new one.
        var source = sender as PiConversation;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(source, _conversation))
            {
                return;
            }

            PiActivityText.Text = activity;
            // The session map shows real observed activity, never stale idle.
            _feed.SetActivity(string.IsNullOrWhiteSpace(activity) ? null : activity);
            RebuildSessionMap();
        });
    }

    private void Conversation_ToolActivityChanged(object? sender, PiToolActivity activity)
    {
        var source = sender as PiConversation;
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(source, _conversation))
            {
                return;
            }

            var seq = activity.IsStart
                ? _feed.AddToolStart(activity.ToolName, activity.Summary, activity.Path, activity.IsMutating)
                : _feed.AddToolEnd(activity.ToolName, activity.IsError);
            _conversationItems.Add(ToViewItem(LookupEntry(seq)));
            MaybeAutoScroll();
            RebuildSessionMap();
        });
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
                var seq = _feed.AddError($"Connection to Pi lost: {error.Message}");
                _conversationItems.Add(ToViewItem(LookupEntry(seq)));
            }

            _feed.SetActivity(null);
            PiActivityText.Text = string.Empty;
            PiConnectionState.Text = "Connection lost";
            PiRunStatus.Text = "Connection to Pi lost — session is persisted. Press Reconnect to restore.";
            RebuildSessionMap();
        });
    }

    private static string DescribeState(PiEngineState state)
        => $"Model: {state.ModelName ?? state.ModelId ?? "none"} ({state.Provider ?? "?"}) · thinking {state.ThinkingLevel} · messages {state.MessageCount}";

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
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        _connectedKey = null;
        _connectedLabel = null;
        _connectedWorkspace = null;

        Dispatcher.UIThread.Post(() =>
        {
            PiConnectionState.Text = "Disconnected";
            PiModelText.Text = "Pi engine not connected.";
            PiActivityText.Text = string.Empty;
            _feed.SetActivity(null);
            RebuildSessionMap();
            PiRunStatus.Text = "Session is persisted — reconnect to restore the conversation.";
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _flushTimer.Stop();
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
