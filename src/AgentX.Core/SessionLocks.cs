namespace AgentX.Core;

/// <summary>
/// Thrown when another Agent X process already owns the cross-process lock
/// for a managed Pi session. Derives from <see cref="InvalidOperationException"/>
/// — the type used for the in-process claim conflict — so existing catch
/// sites handle both ownership refusals identically. The message states
/// explicitly that the competing connect was refused before any Pi process
/// started and that the session was not modified.
/// </summary>
public sealed class SessionLockedException : InvalidOperationException
{
    internal SessionLockedException(string sessionId, string lockPath, Exception inner)
        : base(
            $"Session '{sessionId}' is already owned by another Agent X process " +
            $"(cross-process lock held: {lockPath}). The competing connect was refused before " +
            "any Pi process was started and the session was not modified — close the other " +
            "instance or wait for it to retire this worker, then retry.",
            inner)
    {
        SessionId = sessionId;
        LockPath = lockPath;
    }

    /// <summary>The managed session whose cross-process lock is held.</summary>
    public string SessionId { get; }

    /// <summary>Path of the OS-held lock file (the open handle is the lock; the file proves nothing by existing).</summary>
    public string LockPath { get; }
}

/// <summary>
/// Cross-process single-writer ownership for one managed Pi session, built
/// on an established OS facility: an exclusively-held file handle
/// (<c>FileShare.None</c>) on <c>sessions/&lt;sessionId&gt;.lock</c> beside the
/// session's own files.
///
/// Semantics:
/// - The OPEN HANDLE is the proof of live ownership — no PID is written or
///   trusted; there is no polling, daemon or timer.
/// - Acquire is one non-blocking attempt: a competing Agent X instance fails
///   fast with <see cref="SessionLockedException"/> and never starts Pi.
/// - The kernel drops the lock the moment the holder's handle closes, so a
///   crash or forced termination can never strand ownership.
/// - The file is never unlinked (unlink/recreate is a classic race); an
///   unlocked leftover file is harmless — OpenOrCreate reuses it.
/// - Paths are resolved by NTFS, so equivalent spellings of the same
///   managed root (case, trailing separator, junction) converge on one lock.
/// </summary>
internal static class CrossProcessSessionLock
{
    internal const string LockFileSuffix = ".lock";

    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    private const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

    /// <summary>Lock file path for a managed session (single source of truth).</summary>
    internal static string PathFor(string managedRoot, string sessionId)
    {
        var sessionDirectory = new PiSessionOptions(managedRoot, sessionId).SessionDirectory;
        return Path.Combine(sessionDirectory, sessionId + LockFileSuffix);
    }

    /// <summary>
    /// Take exclusive cross-process ownership or throw immediately. The
    /// returned handle MUST stay open until the Pi process is provably
    /// retired; disposing it releases ownership.
    /// </summary>
    internal static FileStream Acquire(string managedRoot, string sessionId)
    {
        var path = PathFor(managedRoot, sessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException ex) when (ex.HResult is ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION)
        {
            throw new SessionLockedException(sessionId, path, ex);
        }
    }
}
