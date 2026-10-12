using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace AgentX.Core;

/// <summary>
/// Correlates Pi RPC commands with responses over a pair of byte streams and
/// delivers all other records as events. Framing is strict JSONL on LF.
/// The GUI stays independent: this type has no UI references and never blocks
/// the reader while a command is outstanding.
/// </summary>
public sealed class PiRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>> _pending = new();
    private readonly CancellationTokenSource _readerLifetime = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Task? _readerTask;
    private long _malformedLines;
    private bool _disposed;

    public PiRpcConnection(Stream input, Stream output, bool leaveOpen = true)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _leaveOpen = leaveOpen;
    }

    public event EventHandler<PiRpcEventArgs>? EventReceived;

    public long MalformedLineCount => Interlocked.Read(ref _malformedLines);

    public int PendingCommandCount => _pending.Count;

    /// <summary>Start the continuous stdout reader. Must be called before sending commands.</summary>
    public void Start()
    {
        if (_readerTask is not null)
        {
            return;
        }

        _readerTask = Task.Run(() => ReadLoopAsync(_readerLifetime.Token));
    }

    /// <summary>
    /// Send one command and await its correlated response. The pending entry is
    /// registered before writing so a fast completion cannot be missed.
    /// </summary>
    public async Task<JsonDocument> SendCommandAsync(
        string type,
        IDictionary<string, object?>? fields = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var id = Guid.NewGuid().ToString("N");
        var command = new Dictionary<string, object?> { ["id"] = id, ["type"] = type };
        if (fields is not null)
        {
            foreach (var (key, value) in fields)
            {
                command[key] = value;
            }
        }

        var completion = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!_pending.TryAdd(id, completion))
        {
            throw new PiRpcTransportException("Failed to register a pending Pi command.");
        }

        try
        {
            var framed = PiRpcFraming.Encode(command);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _output.WriteAsync(framed, cancellationToken).ConfigureAwait(false);
                await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            throw new PiRpcTransportException("Failed to write a Pi RPC command.", ex);
        }
    }

    /// <summary>
    /// Complete when the next agent_settled event arrives. Call only while a run
    /// is active; returns the event's aborted flag.
    /// </summary>
    public Task<bool> WaitForSettledAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        void Handler(object? sender, PiRpcEventArgs args)
        {
            if (PiRpcFraming.IsSettledEvent(args.Record, out var aborted))
            {
                EventReceived -= Handler;
                completion.TrySetResult(aborted);
            }
        }

        EventReceived += Handler;
        if (cancellationToken.IsCancellationRequested)
        {
            EventReceived -= Handler;
            return Task.FromCanceled<bool>(cancellationToken);
        }

        return completion.Task.ContinueWith(t =>
        {
            EventReceived -= Handler;
            return t.GetAwaiter().GetResult();
        }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Fail all outstanding commands, e.g. when the process exits.</summary>
    public void FailPendingCommands(Exception error)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    internal void HandleProtocolLine(string line)
    {
        var stripped = PiRpcFraming.StripRecordTerminator(line);
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return;
        }

        JsonDocument record;
        try
        {
            record = JsonDocument.Parse(stripped);
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref _malformedLines);
            return;
        }

        if (PiRpcFraming.IsResponseRecord(record, out var id, out _))
        {
            if (id is not null && _pending.TryRemove(id, out var completion))
            {
                completion.TrySetResult(record);
            }
            else
            {
                // Response without a known id (e.g. parse errors from Pi itself):
                // surface as an event so diagnostics are visible, then release.
                EventReceived?.Invoke(this, new PiRpcEventArgs(record, stripped));
                record.Dispose();
            }

            return;
        }

        EventReceived?.Invoke(this, new PiRpcEventArgs(record, stripped));
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        // Byte-level reader: split only on LF, decode each record as UTF-8.
        // No line-length limit so large messages stream correctly.
        var carry = new List<byte>(8 * 1024);
        var chunk = new byte[8 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await _input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break; // stdout closed: orderly or crashed shutdown.
                }

                int segmentStart = 0;
                for (var i = 0; i < read; i++)
                {
                    if (chunk[i] != (byte)'\n')
                    {
                        continue;
                    }

                    string line;
                    if (carry.Count == 0)
                    {
                        line = Encoding.UTF8.GetString(chunk, segmentStart, i - segmentStart);
                    }
                    else
                    {
                        carry.AddRange(new ArraySegment<byte>(chunk, segmentStart, i - segmentStart));
                        line = Encoding.UTF8.GetString(carry.ToArray());
                        carry.Clear();
                    }

                    segmentStart = i + 1;
                    HandleProtocolLine(line);
                }

                if (segmentStart < read)
                {
                    carry.AddRange(new ArraySegment<byte>(chunk, segmentStart, read - segmentStart));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Reader shutdown requested.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            FailPendingCommands(new PiRpcTransportException("Pi RPC stream closed unexpectedly.", ex));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _readerLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        if (_readerTask is not null)
        {
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        FailPendingCommands(new PiRpcTransportException("Pi RPC connection was disposed."));
        foreach (var pending in _pending.Values)
        {
            _ = pending.Task.ContinueWith(t => t.Result?.Dispose(), TaskScheduler.Default);
        }

        _readerLifetime.Dispose();
        _writeGate.Dispose();
        if (!_leaveOpen)
        {
            await _input.DisposeAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
