using AgentX.Core;

// C4 cross-process session-lock probe: one action per process so tests
// exercise GENUINE two-process ownership. File-based signalling — the
// RESULT line is also written to the signal file, so tests never race on
// stdout:
//
//   connect    <root>
//   hold       <root> <signalFile> <releaseFile>
//   hold-crash <root> <signalFile>
//
// Exit codes: 0 clean acquire + graceful disposal, 3 refused
// (SessionLockedException — nothing launched), 4 connect failed but
// ownership retained, 5 usage.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: connect <root> | hold <root> <signal> <release> | hold-crash <root> <signal>");
    return 5;
}

var mode = args[0];
var root = args[1];

await using var manager = new PiWorkerManager(root);
var worker = manager.Workers.FirstOrDefault();
if (worker is null)
{
    Console.Error.WriteLine("RESULT ERROR no-worker (workers.json has no stored worker)");
    return 5;
}

var sw = System.Diagnostics.Stopwatch.StartNew();
Exception? connectFailure = null;
try
{
    await worker.ConnectAsync();
}
catch (Exception ex)
{
    connectFailure = ex;
}

var connectMs = sw.ElapsedMilliseconds;
if (connectFailure is SessionLockedException locked)
{
    // Refused before any Pi launch — report and exit without touching anything.
    Console.WriteLine($"RESULT REFUSED session={worker.SessionId} lock={locked.LockPath}");
    return 3;
}

var pid = worker.Snapshot().ProcessId;
var status = connectFailure is null ? "ACQUIRED" : "HELD-ON-ERROR";
var result =
    $"RESULT {status} session={worker.SessionId} pid={(pid?.ToString() ?? "none")} connectMs={connectMs}" +
    (connectFailure is null ? string.Empty : $" error={connectFailure.GetType().Name}");
Console.WriteLine(result);

int exit;
switch (mode)
{
    case "connect":
        exit = connectFailure is null ? 0 : 4;
        break;

    case "hold" when args.Length >= 4:
        // Signal: the lock is held from this point (connect success or a
        // retained-on-failure ownership — both keep other instances out).
        File.WriteAllText(args[2], result + Environment.NewLine);
        var release = args[3];
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(release) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        exit = connectFailure is null ? 0 : 4;
        break;

    case "hold-crash" when args.Length >= 3:
        File.WriteAllText(args[2], result + Environment.NewLine);
        Thread.Sleep(Timeout.Infinite); // the test force-terminates this process
        exit = 0; // unreachable
        break;

    default:
        Console.Error.WriteLine($"usage: bad or missing arguments for mode '{mode}'");
        exit = 5;
        break;
}

return exit;
// Scope exit disposes the manager: graceful retirement releases the claim
// and the cross-process lock. hold-crash never reaches here — its lock is
// released by the kernel when the test kills the process.
