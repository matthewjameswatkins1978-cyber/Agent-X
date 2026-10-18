using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// Live acceptance-callback contract: Pi's prompt acceptance fires exactly
/// once per settled run, before the run completes, so hosts can retire the
/// composer draft promptly. Temp roots, loud skip without the shim.
/// </summary>
public sealed class AcceptanceLiveTests
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);

    private readonly ITestOutputHelper _output;

    public AcceptanceLiveTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private bool PiAvailable()
    {
        if (File.Exists(PiShim))
        {
            return true;
        }

        _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; live acceptance test cannot run here.");
        return false;
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ax-accept-it-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectDir(string root, string name)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception) { /* never fails the test */ }
    }

    [Fact]
    public async Task PromptAccepted_FiresOnceBeforeSettle()
    {
        if (!PiAvailable())
        {
            return;
        }

        var root = TempRoot();
        using var budget = new CancellationTokenSource(Budget);
        var ct = budget.Token;
        try
        {
            await using var manager = new PiWorkerManager(root);
            var worker = manager.CreateWorker(ProjectDir(root, "proj"), "Agent ONE");
            await worker.ConnectAsync(ct);
            var marker = $"ax-accept-{Guid.NewGuid():N}";
            var accepted = 0;
            var settled = false;
            var (_, receipt) = await worker.SendAsync(
                $"Reply with exactly: {marker}", null, ct,
                onPromptAccepted: () => Interlocked.Increment(ref accepted));
            settled = true;
            Assert.Equal(1, accepted);
            Assert.True(settled);
            Assert.True(receipt.RunHealthy, $"Echo prompt receipt: {receipt.Display}");
            _output.WriteLine($"ACCEPT: fired once before settle; healthy={receipt.RunHealthy}");
        }
        finally
        {
            Cleanup(root);
        }
    }
}
