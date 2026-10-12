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
    private int _eofSignaled;
    private long _subscriberErrors;
    private Exception? _lastSubscriberError;

    public PiRpcConnection(Stream input, Stream output, bool leaveOpen = true)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _leaveOpen = leaveOpen;
    }

    public event EventHandler<PiRpcEventArgs>? EventReceived;

    /// <summary>
    /// Raised when the transport dies (stdout EOF or stream error). Pending
    /// commands are already faulted; event-driven waiters should observe this
    /// to avoid hanging on records that will never arrive.
    /// </summary>
    public event EventHandler<Exception>? TransportFailed;

    /// <summary>
    /// Raised when an event subscriber throws. The reader isolates the fault,
    /// still notifies the remaining subscribers, and keeps running — a
    /// throwing observer must never strand outstanding requests.
    /// </summary>
    public event EventHandler<Exception>? SubscriberError;

    public long SubscriberErrorCount => Interlocked.Read(ref _subscriberErrors);

    public Exception? LastSubscriberError => Volatile.Read(ref _lastSubscriberError);

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
        if (Volatile.Read(ref _eofSignaled) != 0)
        {
            throw new PiRpcTransportException("Pi RPC stdout is closed; the connection cannot accept commands.");
        }

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
    /// is active; returns the event's aborted flag. Transport failure faults
    /// the returned task instead of leaving the caller hung.
    /// </summary>
    public Task<bool> WaitForSettledAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<bool>(cancellationToken);
        }

        if (Volatile.Read(ref _eofSignaled) != 0)
        {
            return Task.FromException<bool>(
                new PiRpcTransportException("Pi RPC stdout is closed; the run will never settle."));
        }

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(object? sender, PiRpcEventArgs args)
        {
            if (PiRpcFraming.IsSettledEvent(args.Record, out var aborted))
            {
                completion.TrySetResult(aborted);
            }
        }

        void Fault(object? sender, Exception error)
        {
            completion.TrySetException(error);
        }

        // The registration must live until the wait finishes: disposing it on
        // method return (as a `using` would) silently disables later
        // cancellation, and the orphaned handler would leak. Both the
        // registration and the subscriptions are released in the continuation.
        EventReceived += Handler;
        TransportFailed += Fault;
        var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        return completion.Task.ContinueWith(
            static (t, state) =>
            {
                var (self, reg, handler, fault) = ((PiRpcConnection, CancellationTokenRegistration, EventHandler<PiRpcEventArgs>, EventHandler<Exception>))state!;
                self.EventReceived -= handler;
                self.TransportFailed -= fault;
                reg.Dispose();
                return t.GetAwaiter().GetResult();
            },
            (this, registration, (EventHandler<PiRpcEventArgs>)Handler, (EventHandler<Exception>)Fault),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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

    /// <summary>
    /// Fault pending commands and notify event-driven waiters. Used for stdout
    /// EOF and stream errors so no caller hangs on records that will never arrive.
    /// </summary>
    private void FailTransport(Exception error)
    {
        if (Interlocked.Exchange(ref _eofSignaled, 1) == 0)
        {
            FailPendingCommands(error);
            NotifyTransportFailed(error);
        }
        else
        {
            FailPendingCommands(error);
        }
    }

    private void NotifyTransportFailed(Exception error)
    {
        var handlers = TransportFailed?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<Exception>)handler).Invoke(this, error);
            }
            catch (Exception subscriberError)
            {
                // A throwing transport observer is reported, never hidden and
                // never allowed to break fault propagation for the rest.
                RaiseSubscriberError(subscriberError);
            }
        }
    }

    /// <summary>
    /// Deliver one event to every subscriber without letting a throwing
    /// observer kill the protocol reader or starve the remaining observers.
    /// Faults are counted and reported via SubscriberError, never swallowed.
    /// </summary>
    private void RaiseEventReceived(PiRpcEventArgs args)
    {
        var handlers = EventReceived?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<PiRpcEventArgs>)handler).Invoke(this, args);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _subscriberErrors);
                Volatile.Write(ref _lastSubscriberError, ex);
                RaiseSubscriberError(ex);
            }
        }
    }

    private void RaiseSubscriberError(Exception error)
    {
        var handlers = SubscriberError?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler<Exception>)handler).Invoke(this, error);
            }
            catch (Exception)
            {
                // Already counted; never recurse and never break dispatch.
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

        JsonDocument? record;
        try
        {
            record = JsonDocument.Parse(stripped);
        }
        catch (JsonException)
        {
            Interlocked.Increment(ref _malformedLines);
            return;
        }

        // The connection owns every parsed record: correlated responses
        // transfer ownership to the awaiting command, while dispatched events
        // are disposed right after synchronous dispatch to return rented pool
        // buffers (critical for large-message streams). Subscribers must copy
        // what they need during the event: after dispatch the buffers may be
        // reused, so a retained document can observe corrupted data.
        try
        {
            if (PiRpcFraming.IsResponseRecord(record, out var id, out _))
            {
                if (id is not null && _pending.TryRemove(id, out var completion))
                {
                    var owned = record;
                    record = null;
                    completion.TrySetResult(owned);
                }
                else
                {
                    // Response without a known id (e.g. parse errors from Pi itself):
                    // surface as an event so diagnostics stay visible.
                    RaiseEventReceived(new PiRpcEventArgs(record, stripped));
                }

                return;
            }

            RaiseEventReceived(new PiRpcEventArgs(record, stripped));
        }
        finally
        {
            record?.Dispose();
        }
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
                    // Stdout closed while we may still owe callers responses or
                    // events: fault them instead of leaving them hung. This
                    // fires independently of any process-exit notification.
                    FailTransport(new PiRpcTransportException("Pi RPC stdout closed unexpectedly."));
                    break;
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
            FailTransport(new PiRpcTransportException("Pi RPC stream closed unexpectedly.", ex));
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
            _ = pending.Task.ContinueWith(
                static t =>
                {
                    if (t.IsCompletedSuccessfully)
                    {
                        t.Result?.Dispose();
                    }
                },
                TaskScheduler.Default);
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
