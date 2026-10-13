using System.Diagnostics;
using AgentX.Core;
using Xunit.Abstractions;

namespace AgentX.Core.Tests;

/// <summary>
/// C4 acceptance: GENUINE two-process ownership. Each scenario spawns real
/// OS processes running the AgentX.SessionLockProbe helper against the same
/// managed root and asserts: refusal without a second Pi process, graceful
/// hand-off after retirement, release after forced termination, and
/// different sessions running concurrently. Gated loudly on the Pi shim
/// (ACQUIRED scenarios need a real Pi) and on the probe being built.
/// </summary>
public sealed class SessionLockProcessTests : IDisposable
{
    private const string PiShim = @"D:\AgentTools\bin\pi.cmd";
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(120);

    private readonly ITestOutputHelper _output;
    private readonly string _root;

    public SessionLockProcessTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), $"ax-lock2p-{Guid.NewGuid():N}");
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
        if (!File.Exists(PiShim))
        {
            _output.WriteLine($"SKIP: Pi shim not found at {PiShim}; two-process lock test cannot run here.");
            return false;
        }

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

    private static int ParsePid(string text) =>
        int.Parse(text.Split("pid=")[1].Split(' ')[0]);

    private static void AssertExited(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // Already gone from the process table.
            }

            Thread.Sleep(100);
        }

        Assert.Fail($"Process {pid} still alive — expected it to be retired.");
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

    private string MakeProject(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ProcessAConnects_ProcessBRefused_WithoutSecondPi()
    {
        if (!Ready())
        {
            return;
        }

        await using var manager = new PiWorkerManager(_root);
        manager.CreateWorker(MakeProject("projA"), "W");

        var signal = Path.Combine(_root, "a.signal");
        var release = Path.Combine(_root, "a.release");
        var (procA, outA) = StartProbe("hold", _root, signal, release);
        try
        {
            var sig = WaitForSignal(signal);
            _output.WriteLine("A signaled: " + sig.Trim());
            Assert.Contains("RESULT ACQUIRED", sig);
            var pidA = ParsePid(sig);
            Assert.False(Process.GetProcessById(pidA).HasExited, "A's Pi should be running");

            // B: identical managed root → identical stored worker → same session.
            var (procB, outB) = StartProbe("connect", _root);
            var codeB = await WaitForExitAsync(procB);
            var textB = (await outB).Trim();
            _output.WriteLine($"B exit={codeB}: {textB}");

            Assert.Equal(3, codeB); // SessionLockedException
            Assert.Contains("RESULT REFUSED", textB);
            Assert.DoesNotContain("RESULT ACQUIRED", textB);
            Assert.DoesNotContain("pid=", textB); // B never even reached a process id
            Assert.False(Process.GetProcessById(pidA).HasExited, "B's refusal must not disturb A");

            // Graceful retirement: A retires its Pi, then ownership releases.
            File.WriteAllText(release, "go");
            Assert.Equal(0, await WaitForExitAsync(procA));
            AssertExited(pidA);
        }
        finally
        {
            TryKill(procA);
        }
    }

    [Fact]
    public async Task GracefulRetirement_AllowsOtherProcessToAcquire()
    {
        if (!Ready())
        {
            return;
        }

        await using var manager = new PiWorkerManager(_root);
        manager.CreateWorker(MakeProject("projA"), "W");

        var signal = Path.Combine(_root, "a.signal");
        var release = Path.Combine(_root, "a.release");
        var (procA, outA) = StartProbe("hold", _root, signal, release);
        try
        {
            var sig = WaitForSignal(signal);
            Assert.Contains("RESULT ACQUIRED", sig);
            var pidA = ParsePid(sig);

            File.WriteAllText(release, "go");
            Assert.Equal(0, await WaitForExitAsync(procA));
            _output.WriteLine("A retired: " + (await outA).Trim());
            AssertExited(pidA);

            // The released session is acquirable by another process …
            var (procB, outB) = StartProbe("connect", _root);
            var codeB = await WaitForExitAsync(procB);
            var textB = (await outB).Trim();
            _output.WriteLine($"B exit={codeB}: {textB}");
            Assert.Equal(0, codeB);
            Assert.Contains("RESULT ACQUIRED", textB);

            // … and B's own graceful exit retires its Pi again.
            AssertExited(ParsePid(textB));
        }
        finally
        {
            TryKill(procA);
        }
    }

    [Fact]
    public async Task ForcedTermination_ReleasesOwnershipSafely()
    {
        if (!Ready())
        {
            return;
        }

        await using var manager = new PiWorkerManager(_root);
        manager.CreateWorker(MakeProject("projA"), "W");

        var signal = Path.Combine(_root, "crash.signal");
        var (procCrash, outCrash) = StartProbe("hold-crash", _root, signal);
        try
        {
            var sig = WaitForSignal(signal);
            _output.WriteLine("crash-holder signaled: " + sig.Trim());
            Assert.Contains("RESULT ACQUIRED", sig);
            var pidA = ParsePid(sig);

            // Forced termination: no graceful path, no release file.
            procCrash.Kill(entireProcessTree: true);
            await WaitForExitAsync(procCrash);
            AssertExited(pidA);

            // The kernel dropped the OS-held lock with the process — a fresh
            // process can acquire the same session immediately.
            var (procB, outB) = StartProbe("connect", _root);
            var codeB = await WaitForExitAsync(procB);
            var textB = (await outB).Trim();
            _output.WriteLine($"B exit={codeB}: {textB}");
            Assert.Equal(0, codeB);
            Assert.Contains("RESULT ACQUIRED", textB);
            AssertExited(ParsePid(textB));
        }
        finally
        {
            TryKill(procCrash);
        }
    }

    [Fact]
    public async Task DifferentSessions_RunConcurrently_InSeparateProcesses()
    {
        if (!Ready())
        {
            return;
        }

        var root2 = Path.Combine(_root, "root2");
        Directory.CreateDirectory(root2);
        var proj1 = MakeProject("proj1");
        var proj2 = Path.Combine(_root, "proj2");
        Directory.CreateDirectory(proj2);

        await using var manager1 = new PiWorkerManager(_root);
        manager1.CreateWorker(proj1, "W1");
        await using var manager2 = new PiWorkerManager(root2);
        manager2.CreateWorker(proj2, "W2");

        var signal1 = Path.Combine(_root, "s1.signal");
        var release1 = Path.Combine(_root, "s1.release");
        var signal2 = Path.Combine(_root, "s2.signal");
        var release2 = Path.Combine(_root, "s2.release");
        var (proc1, out1) = StartProbe("hold", _root, signal1, release1);
        var (proc2, out2) = StartProbe("hold", root2, signal2, release2);
        try
        {
            var sig1 = WaitForSignal(signal1);
            var sig2 = WaitForSignal(signal2);
            _output.WriteLine("holder1: " + sig1.Trim());
            _output.WriteLine("holder2: " + sig2.Trim());
            Assert.Contains("RESULT ACQUIRED", sig1);
            Assert.Contains("RESULT ACQUIRED", sig2);

            var pid1 = ParsePid(sig1);
            var pid2 = ParsePid(sig2);
            Assert.NotEqual(pid1, pid2);
            // Both live at the same time — different sessions, no interference.
            Assert.False(Process.GetProcessById(pid1).HasExited);
            Assert.False(Process.GetProcessById(pid2).HasExited);

            File.WriteAllText(release1, "go");
            File.WriteAllText(release2, "go");
            Assert.Equal(0, await WaitForExitAsync(proc1));
            Assert.Equal(0, await WaitForExitAsync(proc2));
            AssertExited(pid1);
            AssertExited(pid2);
        }
        finally
        {
            TryKill(proc1);
            TryKill(proc2);
        }
    }
}
