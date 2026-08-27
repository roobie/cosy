using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace Cosy.Mcp.Workspace;

/// <summary>
/// One entry in the WorkspaceHost snapshot ring (ADR-0007 §1.4).
/// Carries the immutable Roslyn Solution, the chain parent id (D-02; null for chain head),
/// and per-file disk mtimes captured at snapshot-create time (D-07).
/// Public (not internal) because Plan 09-05's CommitSnapshotTool/DiscardSnapshotTool live in
/// the Cosy.Mcp.Tools namespace and need to consume this surface; deviating intentionally
/// from RESEARCH.md §4's internal recommendation.
/// </summary>
public sealed record SnapshotEntry(
    string Id,
    Solution Snapshot,
    string? ParentId,
    IReadOnlyDictionary<string, DateTimeOffset> FileMtimes);

/// <summary>Result of CommitSnapshotAsync (ADR-0007 §1.5).</summary>
public sealed record CommitResult(
    string SnapshotId,
    IReadOnlyList<string> FilesWritten,
    DateTimeOffset CommitTs,
    int ElapsedMs);

/// <summary>
/// Result of DiscardSnapshotAsync (ADR-0007 §1.6).
/// DiscardedIds includes the requested id plus any descendants that were cascade-evicted.
/// </summary>
public sealed record DiscardResult(
    IReadOnlyList<string> DiscardedIds,
    int ElapsedMs);

/// <summary>
/// Thrown by CommitSnapshotAsync / DiscardSnapshotAsync when the snapshot_id is not in the
/// ring (either never existed or was evicted by D-04 ring rotation). Plan 09-05 tools
/// translate this to <c>ToolError.SnapshotNotFound(...)</c> at the wire boundary.
/// </summary>
public sealed class SnapshotNotFoundException : Exception
{
    public string SnapshotId { get; }
    public SnapshotNotFoundException(string snapshotId)
        : base($"snapshot not found: {snapshotId}") { SnapshotId = snapshotId; }
}

/// <summary>
/// Thrown by CommitSnapshotAsync when one of the captured mtimes no longer matches the disk
/// file's current mtime (ADR-0007 §1.7). force_overwrite is explicitly out of scope per D-07.
/// </summary>
public sealed class DiskConflictException : Exception
{
    public string Path { get; }
    public DateTimeOffset ExpectedMtime { get; }
    public DateTimeOffset ActualMtime { get; }
    public DiskConflictException(string path, DateTimeOffset expected, DateTimeOffset actual)
        : base($"file modified on disk since snapshot was created: {path}")
    { Path = path; ExpectedMtime = expected; ActualMtime = actual; }
}

/// <summary>
/// Singleton owner of the resident MSBuildWorkspace.
/// Invariants:
///   - MSBuildWorkspace is created lazily inside LoadAsync (cheap ctor, slow first load).
///   - WorkspaceFailed is subscribed BEFORE OpenSolutionAsync/OpenProjectAsync (PITFALLS §2).
///   - One path at a time: different-path re-opens require an explicit workspace_close first.
///   - CurrentSolution is an immutable Roslyn snapshot; pointer reads are lock-free (ARCHITECTURE §Pattern 1).
///   - Read tools acquire an IReadLease via RentSolution so CloseAsync can wait for in-flight
///     reads to drain before disposing the workspace (ADR-0005 §D-06).
///   - LoadAsync, CreateSnapshotAsync, and CloseAsync are write-serialised via SemaphoreSlim
///     (ARCHITECTURE §Pattern 2).
/// </summary>
public sealed class WorkspaceHost : IWorkspaceHost, IDisposable
{
    private readonly ILogger<WorkspaceHost> _logger;
    // Guards LoadAsync / CreateSnapshotAsync / CloseAsync. Read tools do NOT take this lock —
    // they use RentSolution and the _noReadersGate handshake instead.
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Reader gate (ADR-0005 §D-06). _activeReaders is Interlocked-modified; _noReadersGate is
    // set when the counter hits zero. CloseAsync waits on the gate after nulling CurrentSolution
    // so no new reader can pick up a soon-to-be-disposed workspace, and outstanding readers
    // finish their Roslyn work before Dispose() runs.
    private int _activeReaders;
    private readonly ManualResetEventSlim _noReadersGate = new(initialState: true);

    // Lazy — created on first LoadAsync, not in ctor.
    private MSBuildWorkspace? _workspace;
    // Normalised absolute path of the currently-loaded solution/project; null before first load
    // and after CloseAsync.
    private string? _loadedPath;
    // ADR-0007 §1.4: bounded ring (N=16, evict-oldest) replaces the unbounded Dictionary store.
    // LinkedList gives O(1) head removal + O(1) tail insert; the parallel index gives O(1) id
    // lookup. Cleared on CloseAsync — every stored Solution becomes an orphan of the just-disposed
    // workspace and would crash on lookup.
    private const int SnapshotRingCapacity = 16;
    private readonly LinkedList<SnapshotEntry> _snapshotRing = new();
    private readonly Dictionary<string, LinkedListNode<SnapshotEntry>> _snapshotIndex = new();

    public Solution? CurrentSolution { get; private set; }

    public WorkspaceHost(ILogger<WorkspaceHost> logger) => _logger = logger;

    /// <summary>
    /// Load a .sln, .slnx, or .csproj. Same-path re-calls reuse the workspace (idempotent recount).
    /// Different-path re-calls return IsError=true with a message that points the caller at
    /// workspace_close. Path validation (existence, extension) is the tool layer's responsibility;
    /// the host receives a validated absolute path. A 60s timeout guards BuildHost subprocess
    /// hangs on headless Linux (PITFALLS §9).
    /// </summary>
    public async Task<LoadResult> LoadAsync(string path, CancellationToken ct)
    {
        // Normalise before acquiring the lock so elapsed_ms for same-path re-calls is minimal.
        var sw = Stopwatch.StartNew();
        var absolutePath = Path.GetFullPath(path);

        await _writeLock.WaitAsync(ct);
        try
        {
            // Different-path rejection. Caller must close before switching (ADR-0005 §D-01).
            if (_loadedPath != null && !string.Equals(_loadedPath, absolutePath, StringComparison.Ordinal))
                return LoadResult.Error(
                    $"workspace already loaded from {_loadedPath}; call workspace_close first to switch");

            // Same-path idempotent recount — no OpenSolutionAsync call.
            if (_loadedPath == absolutePath && CurrentSolution is not null)
            {
                sw.Stop();
                var pc = CurrentSolution.Projects.Count();
                var dc = CurrentSolution.Projects.Sum(p => p.Documents.Count());
                return new LoadResult
                {
                    ProjectCount = pc,
                    DocumentCount = dc,
                    ElapsedMs = (int)sw.ElapsedMilliseconds,
                    Diagnostics = Array.Empty<LoadDiagnostic>(),
                    ResolvedTfm = null
                };
            }

            // First load — create workspace lazily.
            _workspace = MSBuildWorkspace.Create();

            // PITFALLS §2: subscribe WorkspaceFailed BEFORE any Open* call or events are silently lost.
            // Using RegisterWorkspaceFailedHandler (preferred API in Roslyn 5.x — WorkspaceFailed event
            // is obsolete because it fires on the UI thread; RegisterWorkspaceFailedHandler does not).
            var buffer = new List<LoadDiagnostic>();
            _workspace.RegisterWorkspaceFailedHandler(e =>
            {
                var kind = e.Diagnostic.Kind.ToString(); // "Failure" or "Warning"
                buffer.Add(new LoadDiagnostic(kind, e.Diagnostic.Message));
                if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                    _logger.LogError("WorkspaceFailed [Failure]: {Message}", e.Diagnostic.Message);
                else
                    _logger.LogWarning("WorkspaceFailed [Warning]: {Message}", e.Diagnostic.Message);
            });

            // PITFALLS §9: 60s timeout guards against BuildHost subprocess hanging on headless Linux.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            _logger.LogInformation("workspace_open: loading {Path}", absolutePath);

            Solution solution;
            string? resolvedTfm = null;
            // .sln and .slnx both route through OpenSolutionAsync — Roslyn 4.13+ handles
            // the XML-format .slnx transparently inside that call.
            if (absolutePath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                absolutePath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                solution = await _workspace.OpenSolutionAsync(absolutePath, cancellationToken: timeoutCts.Token);
                // resolvedTfm stays null for solutions (mixed TFMs).
            }
            else // .csproj — tool layer already validated extension.
            {
                var project = await _workspace.OpenProjectAsync(absolutePath, cancellationToken: timeoutCts.Token);
                solution = project.Solution;
                resolvedTfm = ResolveTfm(absolutePath);
            }

            CurrentSolution = solution;
            _loadedPath = absolutePath;
            sw.Stop();

            var projectCount = solution.Projects.Count();
            var documentCount = solution.Projects.Sum(p => p.Documents.Count());
            _logger.LogInformation(
                "workspace_open: loaded {ProjectCount} project(s) in {ElapsedMs}ms from {Path}",
                projectCount, sw.ElapsedMilliseconds, absolutePath);

            return new LoadResult
            {
                ProjectCount = projectCount,
                DocumentCount = documentCount,
                ElapsedMs = (int)sw.ElapsedMilliseconds,
                Diagnostics = buffer.ToArray(),
                ResolvedTfm = resolvedTfm
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Dispose the resident MSBuildWorkspace, clear snapshots, null out CurrentSolution.
    /// Order matters: <see cref="CurrentSolution"/> is nulled BEFORE waiting for the reader
    /// gate so no new lease can capture a soon-to-be-disposed workspace. Then we wait for
    /// in-flight readers to drain, then Dispose synchronously (no timeout — a hang surfaces
    /// to the caller, per ADR-0005 §D-06). Idempotent: returns Closed=false if no workspace
    /// is loaded.
    /// </summary>
    public async Task<CloseResult> CloseAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_workspace is null)
            {
                sw.Stop();
                return new CloseResult
                {
                    Closed = false,
                    PriorPath = null,
                    ElapsedMs = (int)sw.ElapsedMilliseconds
                };
            }

            var priorPath = _loadedPath;

            // Step 1: stop handing out non-null solutions. Readers that increment the counter
            // AFTER this point capture null and short-circuit to workspace_not_loaded.
            CurrentSolution = null;

            // Step 2: wait for all in-flight readers (those that captured a real solution
            // before step 1) to release their leases. Off-thread because ManualResetEventSlim
            // exposes only a blocking Wait — async caller stays responsive.
            await Task.Run(() => _noReadersGate.Wait(ct), ct);

            // Step 3: dispose. Synchronous and uncancellable from here on by design — if
            // MSBuild hangs, the close call hangs; we do NOT clear state behind a half-finished
            // disposal because that would let a re-open race a still-running shutdown.
            _workspace.Dispose();
            _workspace = null;
            _loadedPath = null;
            _snapshotRing.Clear();
            _snapshotIndex.Clear();

            sw.Stop();
            _logger.LogInformation(
                "workspace_close: closed {Path} in {ElapsedMs}ms",
                priorPath, sw.ElapsedMilliseconds);

            return new CloseResult
            {
                Closed = true,
                PriorPath = priorPath,
                ElapsedMs = (int)sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadLease RentSolution(out Solution? solution)
    {
        // Increment FIRST so CloseAsync (if it has already nulled CurrentSolution) can't
        // dispose the workspace between our null-check and our use of the snapshot.
        if (Interlocked.Increment(ref _activeReaders) == 1)
            _noReadersGate.Reset();
        solution = CurrentSolution;
        return new ReaderLease(this);
    }

    private void ReleaseReader()
    {
        if (Interlocked.Decrement(ref _activeReaders) == 0)
            _noReadersGate.Set();
    }

    /// <summary>Per-call lease. Dispose is idempotent (Interlocked.Exchange guards against
    /// double-decrement); the using statement in callers makes correct usage automatic.</summary>
    private sealed class ReaderLease : IReadLease
    {
        private WorkspaceHost? _host;
        public ReaderLease(WorkspaceHost host) => _host = host;
        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _host, null);
            h?.ReleaseReader();
        }
    }

    /// <summary>
    /// ADR-0007 §1.1, §1.4, §1.7. Store snapshot in the bounded ring (D-04, N=16) WITHOUT
    /// promoting to CurrentSolution (D-01); capture parent lineage (D-02) and per-file disk
    /// mtimes (D-07) for disk-conflict detection at commit time. Evicts oldest entry if the
    /// ring is full. Write-serialised via _writeLock; reads of CurrentSolution remain lock-free
    /// (D-08/D-10). Throws <see cref="InvalidOperationException"/> if the host was closed
    /// between the caller's read phase and this call — the caller (apply_edits_verified) MUST
    /// release its read lease BEFORE awaiting this method to avoid a lease+_writeLock deadlock
    /// with a concurrent close, and MUST handle this exception by surfacing workspace_not_loaded
    /// to the user.
    /// </summary>
    public async Task<string> CreateSnapshotAsync(
        Solution solution,
        string? parentSnapshotId,
        IReadOnlyDictionary<string, DateTimeOffset> fileMtimes,
        CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_workspace is null)
                throw new InvalidOperationException(
                    "workspace was closed mid-operation; cannot create snapshot");

            // D-04: evict oldest if the ring is full. ADR-0007 §1.4 documents that
            // cascade-eviction of orphaned descendants is NOT performed here — children
            // remain in the ring but their ParentId no longer resolves to a live entry.
            if (_snapshotRing.Count >= SnapshotRingCapacity)
            {
                var oldest = _snapshotRing.First!.Value;
                _snapshotRing.RemoveFirst();
                _snapshotIndex.Remove(oldest.Id);
                _logger.LogDebug("snapshot ring full; evicting oldest id={Id}", oldest.Id);
            }

            var entry = new SnapshotEntry(id, solution, parentSnapshotId, fileMtimes);
            var node = _snapshotRing.AddLast(entry);
            _snapshotIndex[id] = node;
            // D-01: do NOT set CurrentSolution = solution. Promotion is deferred to commit_snapshot.
            return id;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// ADR-0007 §1. Synchronous lookup; takes <see cref="_writeLock"/> briefly to ensure
    /// ring/index consistency with concurrent inserts/evicts. Returns false if the id was
    /// never present or was evicted by ring rotation (D-04).
    /// </summary>
    public bool TryGetSnapshot(string snapshotId, out SnapshotEntry? entry)
    {
        _writeLock.Wait();
        try
        {
            if (_snapshotIndex.TryGetValue(snapshotId, out var node))
            {
                entry = node.Value;
                return true;
            }
            entry = null;
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// ADR-0007 §1.5. Write the snapshot's text changes to disk (per-document
    /// File.WriteAllTextAsync against the Roslyn Solution diff), promote to CurrentSolution,
    /// evict from the ring. D-07 mtime check: compare captured FileMtimes against current disk
    /// mtimes BEFORE writing; throws <see cref="DiskConflictException"/> on mismatch (no
    /// force-overwrite). Throws <see cref="SnapshotNotFoundException"/> if id is unknown or
    /// was evicted. Write-serialised via _writeLock.
    /// </summary>
    public async Task<CommitResult> CommitSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _writeLock.WaitAsync(ct);
        try
        {
            if (!_snapshotIndex.TryGetValue(snapshotId, out var node))
                throw new SnapshotNotFoundException(snapshotId);
            var entry = node.Value;

            // D-07: mtime conflict detection. Compare captured mtimes against current disk
            // state BEFORE writing anything. Second-level tolerance — filesystem mtime
            // precision varies on Linux (ext4 nanosecond, tmpfs second-granularity).
            foreach (var (path, expectedMtime) in entry.FileMtimes)
            {
                if (!File.Exists(path))
                    throw new DiskConflictException(path, expectedMtime, DateTimeOffset.MinValue);
                var actualMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                if (Math.Abs((actualMtime - expectedMtime).TotalSeconds) >= 1.0)
                    throw new DiskConflictException(path, expectedMtime, actualMtime);
            }

            // For Phase 9, commit is semantically "make CurrentSolution equal entry.Snapshot";
            // diff against CurrentSolution to know which document texts to write to disk.
            var baseSolution = CurrentSolution
                ?? throw new InvalidOperationException("workspace closed mid-commit");
            var changes = entry.Snapshot.GetChanges(baseSolution);

            var filesWritten = new List<string>();
            foreach (var projectChanges in changes.GetProjectChanges())
            {
                foreach (var docId in projectChanges.GetChangedDocuments())
                {
                    var doc = entry.Snapshot.GetDocument(docId)!;
                    if (doc.FilePath is null) continue;
                    var text = await doc.GetTextAsync(ct);
                    await File.WriteAllTextAsync(doc.FilePath, text.ToString(), ct);
                    filesWritten.Add(doc.FilePath);
                }
            }

            // D-01: this is the one place CurrentSolution advances after a snapshot is committed.
            CurrentSolution = entry.Snapshot;
            _snapshotRing.Remove(node);
            _snapshotIndex.Remove(snapshotId);

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            _logger.LogInformation(
                "commit_snapshot: wrote {Count} file(s) in {ElapsedMs}ms",
                filesWritten.Count, elapsedMs);
            return new CommitResult(snapshotId, filesWritten, DateTimeOffset.UtcNow, elapsedMs);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// ADR-0007 §1.6. Evict the snapshot AND its chain descendants from the ring without
    /// writing to disk. CurrentSolution is unchanged (deferred-promote means there's nothing
    /// to revert). Throws <see cref="SnapshotNotFoundException"/> if the requested id is
    /// unknown. Descendant walk is O(N²) at N=16 — negligible.
    /// </summary>
    public async Task<DiscardResult> DiscardSnapshotAsync(string snapshotId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await _writeLock.WaitAsync(ct);
        try
        {
            if (!_snapshotIndex.ContainsKey(snapshotId))
                throw new SnapshotNotFoundException(snapshotId);

            // Forward-parent walk: include any entry whose ParentId resolves (directly or
            // transitively) to the requested id. Fixed-point iteration handles arbitrary chains.
            var toEvict = new HashSet<string> { snapshotId };
            bool grew;
            do
            {
                grew = false;
                foreach (var node in _snapshotRing)
                {
                    if (node.ParentId is not null
                        && toEvict.Contains(node.ParentId)
                        && toEvict.Add(node.Id))
                    {
                        grew = true;
                    }
                }
            } while (grew);

            var evictedIds = new List<string>(toEvict.Count);
            foreach (var id in toEvict)
            {
                if (_snapshotIndex.TryGetValue(id, out var node))
                {
                    _snapshotRing.Remove(node);
                    _snapshotIndex.Remove(id);
                    evictedIds.Add(id);
                }
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            _logger.LogInformation(
                "discard_snapshot: evicted {Count} id(s) in {ElapsedMs}ms",
                evictedIds.Count, elapsedMs);
            return new DiscardResult(evictedIds, elapsedMs);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads TargetFramework/TargetFrameworks from csproj XML.
    /// Roslyn's Project API does not expose the TFM string directly.</summary>
    private static string? ResolveTfm(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            return doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value
                ?? doc.Descendants(ns + "TargetFrameworks").FirstOrDefault()?.Value?.Split(';')[0].Trim();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _writeLock.Dispose();
        _noReadersGate.Dispose();
    }
}
