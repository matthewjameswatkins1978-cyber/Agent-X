using AgentX.App;

namespace AgentX.App.Tests;

/// <summary>
/// Startup draft-loss regression (Lucy follow-up P1 on head 327700e): the
/// first restore after construction must not be preceded by a snapshot of
/// the uninitialized (empty) composer. Drives the real <see cref="ViewPrefs"/>
/// on disposable temp dirs — including the disk write — through the exact
/// sequence MainWindow now uses, for the legacy key and a non-legacy key.
/// </summary>
public sealed class WorkerViewLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ax-lifecycle-" + Guid.NewGuid().ToString("N"));

    public WorkerViewLifecycleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void SeedDisk(string legacyDraft, string otherDraft)
    {
        var prefs = new ViewPrefs();
        prefs.Drafts["legacy"] = legacyDraft;
        prefs.Drafts["proj-x"] = otherDraft;
        Assert.True(prefs.Save(_root, ["legacy", "proj-x"], pruneDrafts: true));
    }

    private string? ReadPersisted(ViewPrefs prefs, string key)
        => prefs.Drafts.TryGetValue(key, out var draft) ? draft : null;

    private void CloseAndPersist(ViewPrefs prefs, WorkerViewLifecycle lifecycle, string key)
    {
        // Mirrors OnClosed + SaveWorkerViewState: snapshot the shown state,
        // persist drafts when changed.
        var shown = lifecycle.Store.GetOrCreate(key);
        if (lifecycle.Store.Save(key, shown))
        {
            prefs.Drafts[key] = shown.DraftText;
            Assert.True(prefs.Save(_root, ["legacy", "proj-x"], pruneDrafts: true));
        }
    }

    [Fact]
    public void StartupRestore_PreservesPersistedDrafts_AcrossCloseAndReopen()
    {
        SeedDisk("unsent legacy work", "unsent x work");

        // First startup: the composer is empty, so NO snapshot may precede
        // the restore. This is the exact fixed LoadWorkspaceTabs order.
        var first = new WorkerViewLifecycle();
        Assert.False(first.StartupComplete);
        var prefs = ViewPrefs.Load(_root);
        var (legacyState, legacyExisted) = first.Restore("legacy", k => ReadPersisted(prefs, k));
        Assert.True(first.StartupComplete);
        Assert.True(legacyExisted);
        Assert.Equal("unsent legacy work", legacyState.DraftText); // what the box shows

        // Non-legacy selected tab seeds from the same persisted prefs.
        var (xState, xExisted) = first.Restore("proj-x", k => ReadPersisted(prefs, k));
        Assert.True(xExisted);
        Assert.Equal("unsent x work", xState.DraftText);

        // Close: persisting the shown (unchanged) state is a no-op write;
        // disk keeps both drafts byte-for-byte.
        CloseAndPersist(prefs, first, "legacy");
        var disk = ViewPrefs.Load(_root);
        Assert.Equal("unsent legacy work", disk.Drafts["legacy"]);
        Assert.Equal("unsent x work", disk.Drafts["proj-x"]);

        // Reopen: a fresh lifecycle restores the same drafts, not "".
        var second = new WorkerViewLifecycle();
        var reopened = ViewPrefs.Load(_root);
        var (again, againExisted) = second.Restore("legacy", k => ReadPersisted(reopened, k));
        Assert.True(againExisted);
        Assert.Equal("unsent legacy work", again.DraftText);
    }

    [Fact]
    public void SnapshotBeforeRestore_WouldDestroyDraft_ProvingTheBugShape()
    {
        // Documents the 327700e defect shape: snapshotting the empty
        // composer first (the old unconditional order) clobbers the draft
        // and the follow-up restore can no longer recover it.
        SeedDisk("unsent legacy work", "x");
        var prefs = ViewPrefs.Load(_root);
        var lifecycle = new WorkerViewLifecycle();

        var emptyBox = new WorkerViewState { DraftText = string.Empty };
        Assert.True(lifecycle.Store.Save("legacy", emptyBox));
        prefs.Drafts["legacy"] = emptyBox.DraftText;
        Assert.True(prefs.Save(_root, ["legacy", "proj-x"], pruneDrafts: true));

        var (state, _) = lifecycle.Restore("legacy", k => ReadPersisted(prefs, k));
        Assert.Equal(string.Empty, state.DraftText);
        Assert.Equal(string.Empty, ViewPrefs.Load(_root).Drafts["legacy"]);
    }

    [Fact]
    public void LaterTransitions_SnapshotOutgoing_AndRefusalRoundTrips()
    {
        SeedDisk("legacy draft", "x draft");
        var prefs = ViewPrefs.Load(_root);
        var lifecycle = new WorkerViewLifecycle();
        lifecycle.Restore("legacy", k => ReadPersisted(prefs, k));

        // Genuine later transition: outgoing view snapshots first.
        Assert.True(lifecycle.StartupComplete);
        var edited = new WorkerViewState { DraftText = "edited before switch" };
        Assert.True(lifecycle.Store.Save("legacy", edited));
        var (x, _) = lifecycle.Restore("proj-x", k => ReadPersisted(prefs, k));
        Assert.Equal("x draft", x.DraftText);

        // Refused pinned switch probes another key; the active one is intact.
        Assert.False(lifecycle.Store.Contains("pinned-other"));
        Assert.True(lifecycle.Store.TryGet("legacy", out var kept));
        Assert.Equal("edited before switch", kept!.DraftText);
    }

    [Fact]
    public void NoPersistedDraft_StartsEmpty_AndCompletesStartup()
    {
        var prefs = ViewPrefs.Load(_root);
        var lifecycle = new WorkerViewLifecycle();
        var (state, existed) = lifecycle.Restore("legacy", k => ReadPersisted(prefs, k));
        Assert.False(existed);
        Assert.Equal(string.Empty, state.DraftText);
        Assert.True(lifecycle.StartupComplete);
    }
}
