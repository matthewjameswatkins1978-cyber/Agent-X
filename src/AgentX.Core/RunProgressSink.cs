namespace AgentX.Core;

/// <summary>
/// Run-local progress sink for one prompt run. The GUI hands this (not a
/// closure over a mutable field) to the RPC client, so progress callbacks
/// queued by a previous run can never bleed into the next run's accumulator:
/// <see cref="Close"/> retires the sink and late reports are ignored.
/// Completion still reconciles against the authoritative returned text;
/// cancellation keeps whatever was flushed before Close.
/// </summary>
public sealed class RunProgressSink : IProgress<string>
{
    private readonly Lock _gate = new();
    private readonly StreamingTextAccumulator _accumulator = new();
    private bool _open = true;

    public StreamingTextAccumulator Accumulator => _accumulator;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    public void Report(string? delta)
    {
        lock (_gate)
        {
            if (_open)
            {
                _accumulator.Append(delta);
            }
        }
    }

    /// <summary>Retire the sink: further reports are dropped, never rerouted.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _open = false;
        }
    }

    void IProgress<string>.Report(string value) => Report(value);
}
