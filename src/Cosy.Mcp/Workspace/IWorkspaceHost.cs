using Microsoft.CodeAnalysis;

namespace Cosy.Mcp.Workspace;

/// <summary>
/// Singleton owner of the resident MSBuildWorkspace.
///
/// Lifecycle: one workspace path at a time. <see cref="LoadAsync"/> on a different path while
/// a workspace is open is rejected with IsError=true; callers must invoke
/// <see cref="CloseAsync"/> first to switch. <see cref="CloseAsync"/> waits for outstanding
/// read leases to drain before disposing the workspace.
///
/// Reads: <see cref="CurrentSolution"/> is a Roslyn-immutable snapshot — pointer reads are
/// lock-free (ARCHITECTURE.md Pattern 1). Read tools MUST acquire a lease via
/// <see cref="RentSolution"/> so that <see cref="CloseAsync"/> can block until in-flight
/// readers finish; an unleased read can race with workspace disposal.
///
/// Writes: <see cref="LoadAsync"/>, <see cref="CreateSnapshotAsync"/>, and
/// <see cref="CloseAsync"/> are write-serialised via a SemaphoreSlim inside the implementation.
/// </summary>
public interface IWorkspaceHost
{
    /// <summary>Load a .sln, .slnx, or .csproj. Same-path re-calls reuse the workspace;
    /// different-path re-calls return a LoadResult with IsError=true and a message that
    /// points the caller at workspace_close.</summary>
    Task<LoadResult> LoadAsync(string path, CancellationToken ct);

    /// <summary>Dispose the resident workspace, clear snapshots, null out CurrentSolution.
    /// Waits for in-flight read leases to drain before calling Dispose so a concurrent
    /// read tool cannot observe a half-disposed workspace. Idempotent — returns
    /// Closed=false when nothing was loaded.</summary>
    Task<CloseResult> CloseAsync(CancellationToken ct);

    /// <summary>Null before the first successful load and after CloseAsync.
    /// Direct reads are lock-free but bypass the close gate — prefer <see cref="RentSolution"/>
    /// in any tool that walks the snapshot beyond a single field access.</summary>
    Solution? CurrentSolution { get; }

    /// <summary>Capture the current solution under a read lease. While the returned
    /// <see cref="IReadLease"/> is alive, <see cref="CloseAsync"/> will block at the
    /// reader-drain step. Disposing the lease releases the gate.
    ///
    /// The out parameter mirrors <see cref="CurrentSolution"/> at the moment the lease
    /// was taken — it may be null (before first load / after close); callers must check
    /// and surface the standard <c>workspace_not_loaded</c> error in that case. The lease
    /// itself is still valid and must be disposed regardless.</summary>
    IReadLease RentSolution(out Solution? solution);

    /// <summary>
    /// ADR-0007 §1.1, §1.4, §1.7. Store snapshot in the bounded ring (D-04, N=16) WITHOUT
    /// promoting to CurrentSolution (D-01); captures parent lineage (D-02) and per-file disk
    /// mtimes (D-07) for later disk-conflict detection at commit time. Evicts oldest entry if
    /// the ring is full. Write-serialised internally via SemaphoreSlim. Throws
    /// InvalidOperationException if the workspace was closed.
    /// </summary>
    Task<string> CreateSnapshotAsync(
        Solution solution,
        string? parentSnapshotId,
        IReadOnlyDictionary<string, DateTimeOffset> fileMtimes,
        CancellationToken ct);

    /// <summary>
    /// ADR-0007 §1.5. Write snapshot text changes to disk, promote to CurrentSolution, evict
    /// from the ring. Throws SnapshotNotFoundException if the id is unknown or evicted, and
    /// DiskConflictException if any captured mtime no longer matches disk.
    /// </summary>
    Task<CommitResult> CommitSnapshotAsync(string snapshotId, CancellationToken ct);

    /// <summary>
    /// ADR-0007 §1.6. Evict the snapshot AND its chain descendants from the ring without
    /// writing to disk. CurrentSolution is unchanged. Throws SnapshotNotFoundException if
    /// the id is unknown.
    /// </summary>
    Task<DiscardResult> DiscardSnapshotAsync(string snapshotId, CancellationToken ct);

    /// <summary>Synchronous lookup; returns false if the id was never present or was
    /// evicted by ring rotation (D-04).</summary>
    bool TryGetSnapshot(string snapshotId, out SnapshotEntry? entry);
}

/// <summary>Read lease returned by <see cref="IWorkspaceHost.RentSolution"/>. Dispose to
/// release the close gate. Always dispose, even when the captured solution was null —
/// the lease counter still incremented.</summary>
public interface IReadLease : IDisposable { }
