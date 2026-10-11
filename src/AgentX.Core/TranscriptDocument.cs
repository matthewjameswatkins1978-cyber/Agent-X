using System.Collections;
using System.Text;

namespace AgentX.Core;

/// <summary>Owns the exact imported text and a compact index used to render individual lines.</summary>
public sealed class TranscriptDocument
{
    private readonly int[] _lineStarts;
    private readonly int[] _lineLengths;

    private TranscriptDocument(string text)
    {
        Text = text;
        (_lineStarts, _lineLengths) = IndexLines(text);
        Utf8ByteCount = Encoding.UTF8.GetByteCount(text);
        Lines = new TranscriptLineList(this);
    }

    /// <summary>The exact original text, including its line endings and final newline.</summary>
    public string Text { get; }

    public int LineCount => _lineStarts.Length;

    public int Utf8ByteCount { get; }

    /// <summary>A read-only, lazy line view. The source is not copied into one string per line.</summary>
    public IReadOnlyList<TranscriptLine> Lines { get; }

    public static TranscriptDocument FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TranscriptDocument(text);
    }

    private TranscriptLine GetLine(int index)
    {
        if ((uint)index >= (uint)_lineStarts.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new TranscriptLine(index + 1, Text.Substring(_lineStarts[index], _lineLengths[index]));
    }

    private static (int[] Starts, int[] Lengths) IndexLines(string text)
    {
        if (text.Length == 0)
        {
            return (Array.Empty<int>(), Array.Empty<int>());
        }

        var starts = new List<int>();
        var lengths = new List<int>();
        var lineStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('\r' or '\n'))
            {
                continue;
            }

            starts.Add(lineStart);
            lengths.Add(i - lineStart);

            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            lineStart = i + 1;
        }

        if (lineStart < text.Length)
        {
            starts.Add(lineStart);
            lengths.Add(text.Length - lineStart);
        }

        return (starts.ToArray(), lengths.ToArray());
    }

    private sealed class TranscriptLineList(TranscriptDocument document) : IReadOnlyList<TranscriptLine>, IList
    {
        public int Count => document._lineStarts.Length;
        public bool IsReadOnly => true;
        bool IList.IsReadOnly => true;
        bool IList.IsFixedSize => true;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        public TranscriptLine this[int index] => document.GetLine(index);
        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException("Transcript lines are read-only.");
        }

        public IEnumerator<TranscriptLine> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        int IList.Add(object? value) => throw new NotSupportedException("Transcript lines are read-only.");
        void IList.Clear() => throw new NotSupportedException("Transcript lines are read-only.");
        bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

        int IList.IndexOf(object? value)
        {
            if (value is not TranscriptLine line || line.Number < 1 || line.Number > Count)
            {
                return -1;
            }

            var index = line.Number - 1;
            return this[index] == line ? index : -1;
        }
        void IList.Insert(int index, object? value) => throw new NotSupportedException("Transcript lines are read-only.");
        void IList.Remove(object? value) => throw new NotSupportedException("Transcript lines are read-only.");
        void IList.RemoveAt(int index) => throw new NotSupportedException("Transcript lines are read-only.");

        public void CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            for (var i = 0; i < Count; i++)
            {
                array.SetValue(this[i], index + i);
            }
        }
    }
}