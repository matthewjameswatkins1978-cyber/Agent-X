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

if (mode.StartsWith("reg-", StringComparison.Ordinal))
{
    return await RunRegistryMode(mode, root, args);
}

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

// Registry-race modes: NO Pi connection — these exercise workers.json
// serialization only. Exit 0 ok, 6 typed conflict (Busy/StaleRevision),
// 4 other failure, 5 usage.
static async Task<int> RunRegistryMode(string mode, string root, string[] args)
{
    static string MakeProject(string root, string label) =>
        Directory.CreateDirectory(Path.Combine(root, "proj-" + label)).FullName;

    static void ReportConflict(WorkerRegistryConflictException ex)
    {
        Console.WriteLine($"RESULT CONFLICT {ex.Kind} {ex.Message}");
    }

    switch (mode)
    {
        case "reg-create" when args.Length >= 3:
        {
            await using var manager = new PiWorkerManager(root);
            try
            {
                var w = manager.CreateWorker(MakeProject(root, args[2]), args[2]);
                Console.WriteLine($"RESULT CREATED id={w.Id} label={args[2]}");
                return 0;
            }
            catch (WorkerRegistryConflictException ex)
            {
                ReportConflict(ex);
                return 6;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESULT ERROR {ex.GetType().Name}: {ex.Message}");
                return 4;
            }
        }

        case "reg-remove" when args.Length >= 3:
        {
            await using var manager = new PiWorkerManager(root);
            var target = manager.Workers.FirstOrDefault(w => string.Equals(w.Label, args[2], StringComparison.Ordinal));
            if (target is null)
            {
                Console.WriteLine($"RESULT ERROR no worker labeled '{args[2]}'");
                return 4;
            }

            try
            {
                await manager.RemoveWorkerAsync(target.Id);
                Console.WriteLine($"RESULT REMOVED id={target.Id} label={args[2]}");
                return 0;
            }
            catch (WorkerRegistryConflictException ex)
            {
                ReportConflict(ex);
                return 6;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESULT ERROR {ex.GetType().Name}: {ex.Message}");
                return 4;
            }
        }

        case "reg-stale" when args.Length >= 5:
        {
            // Load FIRST (captures the revision), signal, wait for the
            // release file — by then another process will have changed the
            // registry — then attempt the operation against stale state.
            var op = args[2]; // "create:LABEL" or "remove:LABEL"
            var signal = args[3];
            var release = args[4];
            await using var manager = new PiWorkerManager(root);
            File.WriteAllText(signal, "loaded" + Environment.NewLine);
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (!File.Exists(release) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            var sep = op.IndexOf(':');
            if (sep <= 0 || sep == op.Length - 1)
            {
                Console.WriteLine($"RESULT ERROR bad op '{op}'");
                return 5;
            }

            var kind = op[..sep];
            var label = op[(sep + 1)..];
            try
            {
                if (kind == "create")
                {
                    var w = manager.CreateWorker(MakeProject(root, label), label);
                    Console.WriteLine($"RESULT CREATED id={w.Id} label={label}");
                    return 0;
                }

                if (kind == "remove")
                {
                    var target = manager.Workers.FirstOrDefault(w => string.Equals(w.Label, label, StringComparison.Ordinal));
                    if (target is null)
                    {
                        Console.WriteLine($"RESULT ERROR no worker labeled '{label}'");
                        return 4;
                    }

                    await manager.RemoveWorkerAsync(target.Id);
                    Console.WriteLine($"RESULT REMOVED id={target.Id} label={label}");
                    return 0;
                }

                Console.WriteLine($"RESULT ERROR unknown op kind '{kind}'");
                return 5;
            }
            catch (WorkerRegistryConflictException ex)
            {
                ReportConflict(ex);
                return 6;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RESULT ERROR {ex.GetType().Name}: {ex.Message}");
                return 4;
            }
        }

        case "reg-holdlock" when args.Length >= 3:
        {
            // Mimic a writer inside (or crashed during) its critical
            // section: hold workers.json.lock with an exclusive handle.
            Directory.CreateDirectory(root);
            var lockPath = WorkerStore.StorePath(root) + ".lock";
            using var held = new FileStream(
                lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            File.WriteAllText(args[2], "locked" + Environment.NewLine);
            Thread.Sleep(Timeout.Infinite); // force-terminated by the test
            return 0; // unreachable
        }

        default:
            Console.WriteLine($"RESULT ERROR bad reg arguments for '{mode}'");
            return 5;
    }
}
// Scope exit disposes the manager: graceful retirement releases the claim
// and the cross-process lock. hold-crash never reaches here — its lock is
// released by the kernel when the test kills the process.
