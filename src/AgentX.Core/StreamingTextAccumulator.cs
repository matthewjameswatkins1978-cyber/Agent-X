using System.Text;

namespace AgentX.Core;

/// <summary>
/// Buffers streamed assistant deltas so the UI can flush them in batches
/// instead of rebuilding the full text per delta.
///
/// The previous GUI appended each delta to a StringBuilder and then assigned
/// <c>TextBox.Text = builder.ToString()</c> on every delta: O(n^2) string
/// copies plus one layout pass per character. Here deltas accumulate in a
/// pending buffer; <see cref="Flush"/> moves them to the committed text in
/// one copy and returns only the new chunk for an incremental UI append.
/// The committed text is always the exact concatenation of every delta.
/// </summary>
public sealed class StreamingTextAccumulator
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _committed = new();
    private readonly StringBuilder _pending = new();
    private int _flushCount;

    /// <summary>Chars committed by Flush so far (the authoritative streamed text).</summary>
    public int CommittedChars
    {
        get
        {
            lock (_gate)
            {
                return _committed.Length;
            }
        }
    }

    /// <summary>Chars buffered since the last Flush.</summary>
    public int PendingChars
    {
        get
        {
            lock (_gate)
            {
                return _pending.Length;
            }
        }
    }

    public int TotalChars
    {
        get
        {
            lock (_gate)
            {
                return _committed.Length + _pending.Length;
            }
        }
    }

    /// <summary>How many times Flush moved text. Diagnostic only (batching evidence).</summary>
    public int FlushCount => _flushCount;

    public void Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Append(delta);
        }
    }

    /// <summary>
    /// Move all pending text to committed and return the new chunk
    /// (empty when nothing was pending). The caller appends the chunk to
    /// its live view instead of rebuilding the whole text.
    /// </summary>
    public string Flush()
    {
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                return string.Empty;
            }

            var chunk = _pending.ToString();
            _pending.Clear();
            _committed.Append(chunk);
            _flushCount++;
            return chunk;
        }
    }

    /// <summary>
    /// Flush, then return the complete committed text. Call sparingly
    /// (run completion, copy, reconcile) — not per delta.
    /// </summary>
    public string FlushAndGetCommitted()
    {
        Flush();
        return _committed.ToString();
    }

    /// <summary>Complete committed text. Prefer Flush's chunk for live updates.</summary>
    public string CommittedText
    {
        get
        {
            lock (_gate)
            {
                return _committed.ToString();
            }
        }
    }
}
