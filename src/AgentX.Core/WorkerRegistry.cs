using System.Security.Cryptography;

namespace AgentX.Core;

/// <summary>Why a workers.json save was refused.</summary>
public enum WorkerRegistryConflictKind
{
    /// <summary>Another instance is inside its registry-write critical section right now — transient; retry the operation.</summary>
    Busy,

    /// <summary>The registry bytes on disk differ from what this store loaded — reopen the registry before retrying.</summary>
    StaleRevision,
}

/// <summary>
/// Raised instead of overwriting a <c>workers.json</c> that changed since
/// this manager loaded it, or while another instance holds the registry
/// write lock. Derives from <see cref="InvalidOperationException"/> (the
/// ownership-refusal family). Nothing is written, and the existing
/// save-failure rollback paths keep the in-memory store coherent, so disk
/// and memory both stay exactly as they were; the message carries the
/// recovery instruction (retry for <see cref="WorkerRegistryConflictKind.Busy"/>,
/// reopen for <see cref="WorkerRegistryConflictKind.StaleRevision"/>).
/// </summary>
public sealed class WorkerRegistryConflictException : InvalidOperationException
{
    internal WorkerRegistryConflictException(
        WorkerRegistryConflictKind kind,
        string storePath,
        string message,
        Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StorePath = storePath;
    }

    /// <summary>Busy = safe to retry as-is; StaleRevision = reopen the registry first.</summary>
    public WorkerRegistryConflictKind Kind { get; }

    /// <summary>The registry file this save refused to clobber.</summary>
    public string StorePath { get; }
}

/// <summary>
/// Cross-process serialization for <c>workers.json</c>: an OS-held
/// exclusive file handle on <c>workers.json.lock</c> — the same
/// <c>FileShare.None</c> facility as the C4 session locks (no daemon, no
/// polling, no background thread; the kernel releases it if a writer
/// crashes; the lock file is never deleted merely because it is unlocked).
/// Revision helpers hash the exact registry bytes so a save can compare
/// disk state WITHOUT parsing it — corrupt bytes are therefore detected
/// and preserved, never rewritten.
/// </summary>
internal static class WorkerRegistryLock
{
    internal const string LockFileSuffix = ".lock";

    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    private const int ERROR_LOCK_VIOLATION = unchecked((int)0x80070021);

    /// <summary>Path of the registry write lock (beside workers.json).</summary>
    internal static string PathFor(string managedRoot) => WorkerStore.StorePath(managedRoot) + LockFileSuffix;

    /// <summary>
    /// Take the registry write lock, or fail clearly and immediately with
    /// <see cref="WorkerRegistryConflictException"/> (Kind = Busy).
    /// </summary>
    internal static FileStream Acquire(string storePath)
    {
        var path = storePath + LockFileSuffix;
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
            throw new WorkerRegistryConflictException(
                WorkerRegistryConflictKind.Busy,
                storePath,
                $"workers.json save refused: another Agent X instance is writing the registry right now (lock: {path}). " +
                "Nothing was written; this is transient — retry the operation.",
                ex);
        }
    }

    /// <summary>SHA-256 of the exact bytes currently on disk, or null when the registry does not exist.</summary>
    internal static string? ReadRevision(string storePath)
    {
        try
        {
            return ComputeRevision(File.ReadAllBytes(storePath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Revision token for exact bytes — any byte change invalidates it (content meaning is irrelevant).</summary>
    internal static string ComputeRevision(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
