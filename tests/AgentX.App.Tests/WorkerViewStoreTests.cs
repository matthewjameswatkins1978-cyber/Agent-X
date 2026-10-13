using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// WorkerViewStore lifecycle regressions (Lucy B3 P1-1, state-ownership
/// half): the store underpins MainWindow's save-before-rebuild pairing —
/// snapshots stay under their owning key, restores report prior existence,
/// and a refused transition for one key cannot clobber another.
/// </summary>
public sealed class WorkerViewStoreTests
{
    private static WorkerViewState Snapshot(
        string draft, long? seq = null, double scroll = 0,
        string status = "", string run = "")
        => new() { DraftText = draft, SelectedSeq = seq, ScrollOffset = scroll, StatusText = status, RunStatusText = run };

    [Fact]
    public void Save_ReportsChangeAccurately()
    {
        var store = new WorkerViewStore();
        Assert.True(store.Save("a", Snapshot("one")));
        Assert.False(store.Save("a", Snapshot("one")));
        Assert.True(store.Save("a", Snapshot("two")));
    }

    [Fact]
    public void Snapshots_AreIsolatedPerKey()
    {
        var store = new WorkerViewStore();
        store.Save("a", Snapshot("draft-a", seq: 7, scroll: 120, status: "sa"));
        store.Save("b", Snapshot("draft-b"));

        Assert.True(store.TryGet("a", out var a));
        Assert.Equal("draft-a", a!.DraftText);
        Assert.Equal(7, a.SelectedSeq);
        Assert.Equal(120, a.ScrollOffset);
        Assert.Equal("sa", a.StatusText);
        Assert.True(store.TryGet("b", out var b));
        Assert.Equal("draft-b", b!.DraftText);
        Assert.Null(b.SelectedSeq);
    }

    [Fact]
    public void MissingKey_RestoreReportsAbsent()
    {
        var store = new WorkerViewStore();
        Assert.False(store.Contains("ghost"));
        Assert.False(store.TryGet("ghost", out var state));
        Assert.Null(state);
    }

    [Fact]
    public void GetOrCreate_ReturnsStableInstance()
    {
        var store = new WorkerViewStore();
        var first = store.GetOrCreate("a");
        Assert.Same(first, store.GetOrCreate("a"));
        Assert.True(store.Contains("a"));
        Assert.Equal(new WorkerViewState(), first);
    }

    [Fact]
    public void DirtyFlag_RetryAccounting()
    {
        var store = new WorkerViewStore();
        Assert.False(store.TakeDirty("a"));
        store.MarkDirty("a");
        Assert.True(store.TakeDirty("a"));
        Assert.False(store.TakeDirty("a"));
    }

    [Fact]
    public void RefusedReselect_DoesNotClobberActiveKey()
    {
        // Models the pinned-workspace refusal: the active tab's snapshot is
        // saved, the refused target is probed, the active snapshot is intact.
        var store = new WorkerViewStore();
        var active = Snapshot("unsent draft", seq: 42, status: "working");
        Assert.True(store.Save("active", active));

        Assert.False(store.Contains("pinned-other"));
        Assert.False(store.TryGet("pinned-other", out _));

        Assert.True(store.TryGet("active", out var kept));
        Assert.Equal(active, kept);
        Assert.Equal("unsent draft", kept!.DraftText);
        Assert.Equal(42, kept.SelectedSeq);
    }

    [Fact]
    public void RecordEquality_PinsTheDirtyCheckContract()
    {
        Assert.Equal(Snapshot("x", 1, 2, "s", "r"), Snapshot("x", 1, 2, "s", "r"));
        Assert.NotEqual(Snapshot("x"), Snapshot("y"));
    }
}
