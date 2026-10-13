using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// C5 acceptance with GENUINE separate OS processes (the probe never
/// launches Pi — registry serialization only): a stale save in another
/// process conflicts without losing either registration, stale removal
/// neither resurrects nor deletes unrelated entries, a crashed lock holder
/// does not strand registry ownership, and distinct managed roots stay
/// independent. Loud skip when the probe is not built.
/// </summary>
public sealed class RegistryRaceProcessTests : IDisposable
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(120);

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public RegistryRaceProcessTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"ax-reg2p-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    // Resolve tests/AgentX.Core.Tests/bin/<cfg>/net10.0 → tests/ (the probe
    // is a solution project; a full solution build produces it).
    private static readonly string ProbeDll = FindProbe() ?? string.Empty;

    private static string? FindProbe()
    {
        var testsDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        foreach (var cfg in new[] { "Release", "Debug" })
        {
            var dll = Path.Combine(testsDir, "AgentX.SessionLockProbe", "bin", cfg, "net10.0", "AgentX.SessionLockProbe.dll");
            if (File.Exists(dll))
            {
                return dll;
            }
        }

        return null;
    }

    private bool Ready()
    {
        if (!File.Exists(ProbeDll))
        {
            _output.WriteLine("SKIP: probe not built (looked under tests/AgentX.SessionLockProbe/bin); build the solution first.");
            return false;
        }

        return true;
    }

    private (Process Process, Task<string> Output) StartProbe(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(ProbeDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        var process = Process.Start(psi) ?? throw new InvalidOperationException("probe process failed to start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var output = Task.WhenAll(stdout, stderr)
            .ContinueWith(t => string.Join("\n", t.Result), TaskContinuationOptions.ExecuteSynchronously);
        return (process, output);
    }

    private string WaitForSignal(string path)
    {
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text.Length > 0)
                    {
                        return text;
                    }
                }
                catch
                {
                    // Mid-write; retry.
                }
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"signal file never appeared: {path}");
    }

    private static async Task<int> WaitForExitAsync(Process process)
    {
        using var cts = new CancellationTokenSource(ExitTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException("probe process did not exit within the budget.");
        }

        return process.ExitCode;
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup of a test-owned helper.
        }
    }

    private static string[] DiskLabels(string root) =>
        WorkerStore.Load(root).List().Select(r => r.Label).OrderBy(l => l, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task StaleSave_InOtherProcess_Conflicts_AndEarlierRegistrationSurvives()
    {
        if (!Ready())
        {
            return;
        }

        var signal = Path.Combine(_root, "p1.signal");
        var release = Path.Combine(_root, "p1.release");

        // P1 loads the registry (revision baseline), then pauses.
        var (p1, out1) = StartProbe("reg-stale", _root, "create:W1", signal, release);
        try
        {
            WaitForSignal(signal);

            // A (P2) adds a worker while P1 is paused → disk revision moves.
            var (p2, out2) = StartProbe("reg-create", _root, "WA");
            Assert.Equal(0, await WaitForExitAsync(p2));
            _output.WriteLine("A: " + (await out2).Trim());
            Assert.Equal(new[] { "WA" }, DiskLabels(_root));

            // P1's stale create must conflict, not clobber.
            File.WriteAllText(release, "go");
            var codeP1 = await WaitForExitAsync(p1);
            var textP1 = (await out1).Trim();
            _output.WriteLine($"P1 exit={codeP1}: {textP1}");
            Assert.Equal(6, codeP1);
            Assert.Contains("RESULT CONFLICT StaleRevision", textP1);
            Assert.Equal(new[] { "WA" }, DiskLabels(_root)); // A's registration survives; P1 wrote nothing

            // Recovery by reopen: a fresh process persists its worker and
            // BOTH registrations coexist — nothing silently lost.
            var (p3, out3) = StartProbe("reg-create", _root, "WB");
            Assert.Equal(0, await WaitForExitAsync(p3));
            _output.WriteLine("retry: " + (await out3).Trim());
            Assert.Equal(new[] { "WA", "WB" }, DiskLabels(_root));
        }
        finally
        {
            TryKill(p1);
        }
    }

    [Fact]
    public async Task StaleRemoval_InOtherProcess_NeitherResurrectsNorDeletes()
    {
        if (!Ready())
        {
            return;
        }

        // Seed two registrations, then let P1 load {W1, W2} and pause.
        var seed = new PiWorkerManager(_root);
        var proj = Path.Combine(_root, "proj");
        Directory.CreateDirectory(proj);
        seed.CreateWorker(proj, "W1");
        seed.CreateWorker(proj, "W2");

        var signal = Path.Combine(_root, "p1.signal");
        var release = Path.Combine(_root, "p1.release");
        var (p1, out1) = StartProbe("reg-stale", _root, "remove:W2", signal, release);
        try
        {
            WaitForSignal(signal);

            // A removes W1 while P1 is paused → revision moves.
            var (p2, out2) = StartProbe("reg-remove", _root, "W1");
            Assert.Equal(0, await WaitForExitAsync(p2));
            _output.WriteLine("A: " + (await out2).Trim());
            Assert.Equal(new[] { "W2" }, DiskLabels(_root));

            // P1's stale removal of W2 must conflict: W1 stays removed
            // (no resurrection), W2 stays present (no stale deletion).
            File.WriteAllText(release, "go");
            var codeP1 = await WaitForExitAsync(p1);
            var textP1 = (await out1).Trim();
            _output.WriteLine($"P1 exit={codeP1}: {textP1}");
            Assert.Equal(6, codeP1);
            Assert.Contains("RESULT CONFLICT StaleRevision", textP1);
            Assert.Equal(new[] { "W2" }, DiskLabels(_root));

            // Recovery by reopen: the intended removal then completes.
            var (p3, out3) = StartProbe("reg-remove", _root, "W2");
            Assert.Equal(0, await WaitForExitAsync(p3));
            _output.WriteLine("retry: " + (await out3).Trim());
            Assert.Empty(DiskLabels(_root));
        }
        finally
        {
            TryKill(p1);
        }
    }

    [Fact]
    public async Task CrashedRegistryWriter_DoesNotStrandOwnership()
    {
        if (!Ready())
        {
            return;
        }

        var signal = Path.Combine(_root, "hold.signal");
        var (holder, _) = StartProbe("reg-holdlock", _root, signal);
        try
        {
            WaitForSignal(signal);

            // While the (simulated mid-write) lock is held: clear failure.
            var (busy, busyOut) = StartProbe("reg-create", _root, "WhileHeld");
            var busyCode = await WaitForExitAsync(busy);
            var busyText = (await busyOut).Trim();
            _output.WriteLine($"busy exit={busyCode}: {busyText}");
            Assert.Equal(6, busyCode);
            Assert.Contains("RESULT CONFLICT Busy", busyText);
            Assert.Empty(DiskLabels(_root)); // nothing written

            // Crash the writer: the kernel drops the lock with its handle…
            holder.Kill(entireProcessTree: true);
            await WaitForExitAsync(holder);

            // …and a fresh process saves immediately. No stranding.
            var (after, afterOut) = StartProbe("reg-create", _root, "AfterCrash");
            Assert.Equal(0, await WaitForExitAsync(after));
            _output.WriteLine("after crash: " + (await afterOut).Trim());
            Assert.Equal(new[] { "AfterCrash" }, DiskLabels(_root));
        }
        finally
        {
            TryKill(holder);
        }
    }

    [Fact]
    public async Task DifferentManagedRoots_SaveConcurrentlyAndIndependently()
    {
        if (!Ready())
        {
            return;
        }

        var root2 = Path.Combine(_root, "root2");
        Directory.CreateDirectory(root2);

        var (p1, out1) = StartProbe("reg-create", _root, "R1");
        var (p2, out2) = StartProbe("reg-create", root2, "R2");
        Assert.Equal(0, await WaitForExitAsync(p1));
        Assert.Equal(0, await WaitForExitAsync(p2));
        _output.WriteLine("root1: " + (await out1).Trim());
        _output.WriteLine("root2: " + (await out2).Trim());
        Assert.Equal(new[] { "R1" }, DiskLabels(_root));
        Assert.Equal(new[] { "R2" }, DiskLabels(root2));
    }
}
