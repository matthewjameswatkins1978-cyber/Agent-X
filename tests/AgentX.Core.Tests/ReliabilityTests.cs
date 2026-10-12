using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using AgentX.Core;

namespace AgentX.Core.Tests;

/// <summary>
/// RC1 C2 reliability verification: registry writer integrity, hostile
/// registry payloads, oversized transport records, and map scaling. Live
/// process-death, reconnect, cancel, redaction, and session-sharing evidence
/// comes from disposable real-Pi probes (see PR body); these tests pin the
/// deterministic half. New file on purpose: WorkspaceTests belongs to the
/// navigation line.
/// </summary>
public sealed class ReliabilityTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public ReliabilityTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    private static string TempRoot(string tag)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-rel-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Registry_WriterNeverEmitsNull_SaveReloadIsByteStable()
    {
        // Item 4 ([null] writer hunt): the only writer path (GetOrRegister of
        // concrete records + atomic Save) must round-trip byte-identical with
        // no null entry ever present on disk.
        var root = TempRoot("writer");
        try
        {
            var registry = new WorkspaceRegistry(root);
            registry.GetOrRegister(ProjectDir(root, "Alpha"));
            registry.GetOrRegister(ProjectDir(root, "Beta"));
            registry.Save();
            var path = WorkspaceRegistry.RegistryPath(root);
            var first = File.ReadAllText(path);
            Assert.DoesNotContain("null", first, StringComparison.OrdinalIgnoreCase);

            var reloaded = WorkspaceRegistry.Load(root);
            Assert.Equal(2, reloaded.List().Count);
            reloaded.Save();
            var second = File.ReadAllText(path);
            Assert.Equal(first, second);

            // And a hostile null-element file still fails closed, never loads.
            File.WriteAllText(path, "[null]");
            Assert.Throws<InvalidDataException>(() => WorkspaceRegistry.Load(root));
            Assert.Equal("[null]", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Registry_NullFields_FailClosed()
    {
        var root = TempRoot("nullfields");
        try
        {
            var path = WorkspaceRegistry.RegistryPath(root);
            File.WriteAllText(path, "[{}]");
            Assert.Throws<InvalidDataException>(() => WorkspaceRegistry.Load(root));
            File.WriteAllText(path, """[{"slug":null,"projectRoot":null,"sessionId":null}]""");
            Assert.Throws<InvalidDataException>(() => WorkspaceRegistry.Load(root));
            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Transport_OversizedRecord_RoundTripsIntact()
    {
        await using var loop = new OversizedLoopback();
        var big = new string('Q', 5 * 1024 * 1024);
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Connection.EventReceived += (_, args) =>
        {
            if (PiRpcFraming.GetEventType(args.Record) == "message_update")
            {
                seen.TrySetResult(args.Record.RootElement.GetProperty("assistantMessageEvent").GetProperty("delta").GetString() ?? "");
            }
        };
        await loop.WriteStdoutLineAsync("""{"type":"message_update","usage":{},"assistantMessageEvent":{"type":"text_delta","contentIndex":0,"delta":"__B__"}}""".Replace("__B__", big));
        var got = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(5 * 1024 * 1024, got.Length);
    }

    [Fact]
    public void SessionMap_ScalesLinearly_CorrectAtVolume()
    {
        var feed = new ConversationFeed();
        for (var i = 0; i < 5000; i++)
        {
            if (i % 3 == 0) feed.AddUser($"prompt number {i} with realistic row text");
            else if (i % 3 == 1) feed.AddToolStart("powershell", $"Get-Item probe{i}.txt", null, false);
            else feed.AddToolEnd("powershell", false);
        }

        var watch = Stopwatch.StartNew();
        var map = feed.BuildMap();
        watch.Stop();
        _output.WriteLine($"MAP-BUILD entries={feed.Count} milestones={map.Milestones.Count} elapsedMs={watch.ElapsedMilliseconds}");
        Assert.Equal(5000, map.Milestones.Count);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"elapsed={watch.ElapsedMilliseconds}ms");
    }

    private sealed class OversizedLoopback : IAsyncDisposable
    {
        private readonly Pipe _input = new();

        public PiRpcConnection Connection { get; }

        public OversizedLoopback()
        {
            Connection = new PiRpcConnection(_input.Reader.AsStream(), Stream.Null, leaveOpen: true);
            Connection.Start();
        }

        public async Task WriteStdoutLineAsync(string line)
        {
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
        }
    }
}
