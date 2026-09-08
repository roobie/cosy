using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Refactor;
using Cosy.Mcp.Search;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Per-document staged change, projected as a unified diff (ADR-0010).
// new_text_length round-trip-verifies the staged content without echoing whole files
// on the write path (D-07 spirit preserved; source crosses the wire only on this explicit read).
public sealed record ReadSnapshotDocument(
    [property: JsonPropertyName("file")]            string File,
    [property: JsonPropertyName("diff")]            string Diff,
    [property: JsonPropertyName("new_text_length")] int NewTextLength);

public sealed record ReadSnapshotResult(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("parent_id")]   string? ParentId,
    [property: JsonPropertyName("documents")]   IReadOnlyList<ReadSnapshotDocument> Documents);

/// <summary>
/// read_snapshot — non-destructive inspection of a staged (uncommitted) snapshot (ADR-0010).
/// Reads the staged Solution from the ring (TryGetSnapshot), diffs it against its parent
/// (or CurrentSolution), and returns a unified diff per changed document. Never promotes,
/// evicts, or writes to disk. Closes the staged-invisibility gap (B4/FM-2) surfaced by the
/// 2026-06-04 boundary analysis.
/// </summary>
[McpServerToolType]
public sealed class ReadSnapshotTool
{
    [McpServerTool(Name = "read_snapshot")]
    [Description(
        "Non-destructive inspection of a staged (uncommitted) snapshot. Returns a unified diff " +
        "per changed document, computed against the snapshot's parent (or the current solution). " +
        "Does NOT write to disk or alter the snapshot ring. Unknown/evicted id returns " +
        "error.kind=snapshot_not_found. Optional file suffix narrows to one changed document.")]
    public async Task<object> ReadAsync(
        [Description("The 8-char hex snapshot id to inspect (from extract_method / apply_edits_verified).")] string snapshotId,
        IWorkspaceHost workspaceHost,
        ILogger<ReadSnapshotTool> logger,
        [Description("Optional file-path suffix to narrow the diff to a single changed document.")] string? file = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(snapshotId))
            return Envelope<ReadSnapshotResult>.Err(
                ToolError.InvalidArgument("snapshot_id", "must_not_be_empty", value: snapshotId));

        var sw = Stopwatch.StartNew();
        using var lease = workspaceHost.RentSolution(out var current);
        if (current is null)
            return Envelope<ReadSnapshotResult>.Err(ToolError.WorkspaceNotLoaded());

        try
        {
            if (!workspaceHost.TryGetSnapshot(snapshotId, out var entry) || entry is null)
                return Envelope<ReadSnapshotResult>.Err(ToolError.SnapshotNotFound(snapshotId));

            var staged = entry.Snapshot;
            var baseSolution = ResolveParentSolution(workspaceHost, entry, current!);
            var solutionDir = SolutionPaths.GetSolutionDirectory(staged);

            var documents = new List<ReadSnapshotDocument>();
            foreach (var projChanges in staged.GetChanges(baseSolution).GetProjectChanges())
            {
                foreach (var docId in projChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
                {
                    var newDoc = staged.GetDocument(docId)!;
                    var oldDoc = baseSolution.GetDocument(docId);
                    var newText = (await newDoc.GetTextAsync(ct)).ToString();
                    var oldText = oldDoc is null ? "" : (await oldDoc.GetTextAsync(ct)).ToString();

                    var relPath = newDoc.FilePath is null
                        ? newDoc.Name
                        : Path.GetRelativePath(solutionDir, newDoc.FilePath).Replace('\\', '/');

                    if (file is not null
                        && !relPath.EndsWith(file, StringComparison.OrdinalIgnoreCase)
                        && !(newDoc.FilePath?.EndsWith(file, StringComparison.OrdinalIgnoreCase) ?? false))
                        continue;

                    documents.Add(new ReadSnapshotDocument(
                        File: relPath,
                        Diff: UnifiedDiff.Render(oldText, newText),
                        NewTextLength: newText.Length));
                }
            }

            if (file is not null && documents.Count == 0)
                return Envelope<ReadSnapshotResult>.Err(
                    ToolError.InvalidArgument("file", "not_found_in_snapshot", value: file));

            sw.Stop();
            logger.LogInformation("read_snapshot: {DocCount} document(s) for {SnapshotId} in {ElapsedMs}ms",
                documents.Count, snapshotId, sw.ElapsedMilliseconds);
            return Envelope<ReadSnapshotResult>.Ok(
                new ReadSnapshotResult(snapshotId, entry.ParentId, documents), (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during read_snapshot");
            return Envelope<ReadSnapshotResult>.Err(ToolError.Internal(ex));
        }
    }

    /// <summary>
    /// The snapshot's diff base: its parent snapshot if still in the ring, else CurrentSolution.
    /// A null-parent snapshot read after an intervening commit diffs against a drifted base
    /// (ADR-0010 caveat) — rare under the single-agent model. Shared with check_snapshot.
    /// </summary>
    internal static Solution ResolveParentSolution(IWorkspaceHost host, SnapshotEntry entry, Solution current)
    {
        if (entry.ParentId is not null && host.TryGetSnapshot(entry.ParentId, out var parent) && parent is not null)
            return parent.Snapshot;
        return current;
    }
}
