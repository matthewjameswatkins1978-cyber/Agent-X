namespace AgentX.Core;

/// <summary>
/// Bounded UI dispatch for bursty feed notifications: <see cref="Signal"/>
/// from any thread ensures AT MOST ONE pending post, no matter how many
/// events arrive. The posted callback runs the LATEST work once (feed sync
/// always reads current state, so latest-wins preserves ordering and state
/// with no queue growth). Thread-safe; the post delegate owns threading
/// (the App wires Dispatcher.UIThread.Post).
/// </summary>
public sealed class FeedEventCoalescer
{
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private Action? _latest;
    private int _pending;

    public FeedEventCoalescer(Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        _post = post;
    }

    public void Signal(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            _latest = work;
        }

        if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
        {
            _post(Flush);
        }
    }

    private void Flush()
    {
        // Clear pending BEFORE taking work: a signal arriving mid-flush
        // either overwrites _latest before we take it (we run the newer
        // work) or queues exactly one follow-up flush. Work is never lost
        // and at most one callback is ever queued.
        Interlocked.Exchange(ref _pending, 0);
        Action? work;
        lock (_gate)
        {
            work = _latest;
            _latest = null;
        }

        work?.Invoke();
    }
}
