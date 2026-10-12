using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// Regression tests for the PR #10 workspace-ownership repair: every
/// identity transition — user tab click or programmatic reselect — flows
/// through WorkspaceSessionCoordinator, so the guarded switch and the
/// feed-memory sync cannot be bypassed.
/// </summary>
public sealed class CoordinatorTests
{
    private static (WorkspaceSessionCoordinator Coordinator, ConversationFeed Feed) Create()
    {
        var feed = new ConversationFeed();
        return (new WorkspaceSessionCoordinator(feed, "legacy"), feed);
    }

    private static void SeedLiveView(ConversationFeed feed, string marker)
    {
        feed.AddUser($"prompt {marker}");
        var turn = feed.BeginAssistantTurn();
        feed.AppendDelta(turn, $"streamed {marker}");
        feed.EndAssistantTurn(turn, new PiRunReceipt(true, false, 0, 10, 2));
        feed.AddToolStart("edit", "src/Foo.cs", "src/Foo.cs", isMutating: true);
        feed.AddToolEnd("edit", isError: false);
        feed.AddSystemNotice($"notice {marker}");
    }

    private static string MapSignature(ConversationFeed feed)
    {
        var map = feed.BuildMap();
        return string.Join("|", map.Milestones.Select(m => $"{m.Kind}:{m.Title}#{m.Seq}"))
            + $";errors={map.ErrorCount};files={string.Join(",", map.ChangedFiles)}";
    }

    [Fact]
    public void Initial_SeedsLegacyOwner_WithEmptyMemory()
    {
        var (coordinator, feed) = Create();
        Assert.Equal("legacy", coordinator.ActiveKey);
        Assert.Null(coordinator.ConnectedKey);
        Assert.True(coordinator.HasMemory("legacy"));
        Assert.Empty(feed.Snapshot());
    }

    [Fact]
    public void LegacyToBToLegacy_PreservesLiveOnlyView()
    {
        var (coordinator, feed) = Create();
        SeedLiveView(feed, "A");
        var beforeEntries = feed.Snapshot().ToArray();
        var beforeMap = MapSignature(feed);

        Assert.True(coordinator.RequestTransition("ws-b").Accepted);
        Assert.Equal("ws-b", coordinator.ActiveKey);
        Assert.Empty(feed.Snapshot()); // B establishes an empty view.

        feed.AddUser("prompt B");
        Assert.True(coordinator.RequestTransition("legacy").Accepted);
        Assert.Equal(beforeEntries, feed.Snapshot()); // Exact restore, receipts included.
        Assert.Equal(beforeMap, MapSignature(feed));

        Assert.True(coordinator.RequestTransition("ws-b").Accepted);
        Assert.Single(feed.Snapshot()); // B's own view kept separately.
    }

    [Fact]
    public void TransitionWhileConnected_Refused_FeedUntouched()
    {
        var (coordinator, feed) = Create();
        SeedLiveView(feed, "A");
        coordinator.NoteConnected("legacy");
        var before = feed.Snapshot().ToArray();

        var result = coordinator.RequestTransition("ws-b");
        Assert.False(result.Accepted);
        Assert.NotNull(result.RefusalReason);
        Assert.Contains("legacy", result.RefusalReason, StringComparison.Ordinal);
        Assert.Equal("legacy", coordinator.ActiveKey); // A stays visibly selected.
        Assert.Equal(before, feed.Snapshot()); // A's conversation untouched.
    }

    [Fact]
    public void TransitionAfterDisconnect_Accepted_SnapshotsFirst()
    {
        var (coordinator, feed) = Create();
        SeedLiveView(feed, "A");
        coordinator.NoteConnected("legacy");
        coordinator.NoteDisconnected();
        Assert.Null(coordinator.ConnectedKey);

        Assert.True(coordinator.RequestTransition("ws-b").Accepted);
        Assert.Empty(feed.Snapshot());

        Assert.True(coordinator.RequestTransition("legacy").Accepted);
        Assert.Contains(feed.Snapshot(), e => e.Role == "receipt"); // Live-only receipt survived.
    }

    [Fact]
    public void SameKeyTransition_IsNoop()
    {
        var (coordinator, feed) = Create();
        SeedLiveView(feed, "A");
        var before = feed.Snapshot().ToArray();
        Assert.True(coordinator.RequestTransition("legacy").Accepted);
        Assert.Equal(before, feed.Snapshot());
    }

    [Fact]
    public void RefreshMemory_PicksUpConnectedHistory()
    {
        // Connect loads server history into the feed; RefreshMemory makes it
        // the stored view so a later away-and-back restores it, not the
        // pre-connect snapshot.
        var (coordinator, feed) = Create();
        feed.AddUser("stale pre-connect view");
        coordinator.NoteConnected("legacy");
        feed.LoadHistory([new ConversationMessage("user", "server history")]);
        coordinator.RefreshMemory();
        coordinator.NoteDisconnected();

        Assert.True(coordinator.RequestTransition("ws-b").Accepted);
        Assert.True(coordinator.RequestTransition("legacy").Accepted);
        var restored = Assert.Single(feed.Snapshot());
        Assert.Equal("server history", restored.Text);
    }

    [Fact]
    public void RejectsBlankKeys()
    {
        var (coordinator, _) = Create();
        Assert.Throws<ArgumentException>(() => coordinator.RequestTransition(""));
        Assert.Throws<ArgumentException>(() => coordinator.NoteConnected("  "));
        Assert.Throws<ArgumentNullException>(() => new WorkspaceSessionCoordinator(null!, "legacy"));
    }
}
