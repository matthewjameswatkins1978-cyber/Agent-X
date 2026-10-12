using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AgentX.Core;

namespace AgentX.App;

public partial class MainWindow : Window
{
    private TranscriptImportResult? _current;
    private PiConversation? _conversation;
    private CancellationTokenSource? _promptLifetime;
    private readonly StringBuilder _streamedResponse = new();
    private bool _piBusy;

    public MainWindow()
    {
        InitializeComponent();
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
        if (_conversation?.IsConnected == true)
        {
            return;
        }

        PiConnectionState.Text = "Connecting…";
        var conversation = new PiConversation(new PiRpcClient(PiRpcOptions.Persistent(PiSessionOptions.Default())));
        conversation.ActivityChanged += Conversation_ActivityChanged;
        try
        {
            // Same persistent Pi-native session on every launch: history is restored below.
            var state = await conversation.ConnectAsync();
            _conversation = conversation;
            PiConnectionState.Text = "Connected";
            PiModelText.Text = DescribeState(state);
            var history = conversation.History;
            _streamedResponse.Clear();
            PiResponseBox.Text = PiConversation.FormatHistoryForDisplay(history);
            PiResponseBox.CaretIndex = 0;
            PiRunStatus.Text = history.Count == 0
                ? "New persistent session — no previous conversation."
                : $"Restored session with {history.Count} message(s). Send a prompt to continue.";
        }
        catch (Exception ex)
        {
            conversation.ActivityChanged -= Conversation_ActivityChanged;
            await conversation.DisposeAsync();
            PiConnectionState.Text = "Connection failed";
            PiModelText.Text = ex.Message;
        }
    }

    private async void DisconnectPi_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DisconnectPiAsync();
    }

    private async void SendPrompt_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_piBusy)
        {
            return;
        }

        if (_conversation?.IsConnected != true)
        {
            PiResponseBox.Text = "Connect to Pi first.";
            return;
        }

        var message = PiPromptBox.Text?.Trim();
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        _piBusy = true;
        PiSendButton.IsEnabled = false;
        PiCancelButton.IsEnabled = true;
        _streamedResponse.Clear();
        PiResponseBox.Text = string.Empty;
        PiConnectionState.Text = "Working…";
        _promptLifetime = new CancellationTokenSource();
        var progress = new Progress<string>(delta =>
            Dispatcher.UIThread.Post(() =>
            {
                _streamedResponse.Append(delta);
                PiResponseBox.Text = _streamedResponse.ToString();
                PiResponseBox.CaretIndex = PiResponseBox.Text.Length;
            }));

        try
        {
            var (text, receipt) = await _conversation.SendAsync(message, progress, _promptLifetime.Token);
            // Reconcile with the authoritative accumulated text on completion.
            _streamedResponse.Clear();
            _streamedResponse.Append(text);
            PiResponseBox.Text = _streamedResponse.ToString();
            PiResponseBox.CaretIndex = PiResponseBox.Text.Length;
            PiConnectionState.Text = "Idle";
            PiActivityText.Text = string.Empty;
            PiRunStatus.Text = $"Last run: {receipt.Display} ({receipt.AssistantChars:N0} chars, {receipt.MessageCount} session messages.)";
            StatusText.Text = receipt.RunHealthy
                ? $"Pi replied with {_streamedResponse.Length:N0} characters — healthy run (output not externally verified)."
                : $"Pi run finished — {receipt.Display}";
        }
        catch (OperationCanceledException)
        {
            PiConnectionState.Text = "Cancelled";
            PiRunStatus.Text = "Last run: cancelled — not healthy and not verified. Reconnect state is unchanged; history refreshes on next connect.";
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
            PiResponseBox.Text = $"Pi rejected the prompt: {ex.Error}";
            PiConnectionState.Text = "Idle";
        }
        catch (PiRpcTransportException ex)
        {
            PiResponseBox.Text = $"Connection to Pi lost: {ex.Message}";
            PiConnectionState.Text = "Disconnected";
            PiRunStatus.Text = "Session is persisted — reconnect to restore the conversation.";
            await DisconnectPiAsync();
        }
        finally
        {
            _piBusy = false;
            PiSendButton.IsEnabled = true;
            PiCancelButton.IsEnabled = false;
            _promptLifetime?.Dispose();
            _promptLifetime = null;
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

    private void Conversation_ActivityChanged(object? sender, string activity)
    {
        // Compact tool/error/processing signal; streamed text flows via IProgress.
        Dispatcher.UIThread.Post(() => PiActivityText.Text = activity);
    }

    private static string DescribeState(PiEngineState state)
        => $"Model: {state.ModelName ?? state.ModelId ?? "none"} ({state.Provider ?? "?"}) · thinking {state.ThinkingLevel} · messages {state.MessageCount}";

    private async Task DisconnectPiAsync()
    {
        if (_promptLifetime is not null)
        {
            await _promptLifetime.CancelAsync();
        }

        if (_conversation is not null)
        {
            _conversation.ActivityChanged -= Conversation_ActivityChanged;
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        Dispatcher.UIThread.Post(() =>
        {
            PiConnectionState.Text = "Disconnected";
            PiModelText.Text = "Pi engine not connected.";
            PiActivityText.Text = string.Empty;
            PiRunStatus.Text = "Session is persisted — reconnect to restore the conversation.";
        });
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_conversation is not null)
        {
            _conversation.ActivityChanged -= Conversation_ActivityChanged;
            await _conversation.DisposeAsync();
            _conversation = null;
        }

        _promptLifetime?.Dispose();
        _promptLifetime = null;
    }
}