using System.ComponentModel;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for commit_snapshot — lives under envelope.data (ADR-0004 §1).
// ADR-0007 §1.5: writes the snapshot's text changes to disk, promotes to CurrentSolution,
// evicts from the ring. Returns the absolute paths of files written + the commit timestamp.
public sealed record CommitSnapshotToolData(
    [property: JsonPropertyName("files_written")] IReadOnlyList<string> FilesWritten,
    [property: JsonPropertyName("commit_ts")]     DateTimeOffset CommitTs);

/// <summary>
/// commit_snapshot — persist the named snapshot's text changes to disk, promote to
/// CurrentSolution, and evict the snapshot from the ring. ADR-0007 §1.5. Closes ADR-0005 §D-02.
/// </summary>
[McpServerToolType]
public sealed class CommitSnapshotTool
{
    [McpServerTool(Name = "commit_snapshot")]
    [Description(
        "Write a previously-staged snapshot's text changes to disk, promote it to the current solution, " +
        "and evict it from the snapshot ring. On disk-mtime mismatch returns error.kind=disk_conflict; on " +
        "unknown id (including ring-evicted) returns error.kind=snapshot_not_found.")]
    public async Task<object> CommitAsync(
        IWorkspaceHost workspaceHost,
        ILogger<CommitSnapshotTool> logger,
        // Wire-name parameter convention: the MCP SDK does NOT snake_case-fold C# parameter
        // names (see ApplyEditsVerifiedTests note on fromSnapshotId — same SDK behaviour).
        // Hence callers pass `snapshotId` (camelCase) on the wire, matching the C# parameter
        // name verbatim. The RESPONSE-side wire key `snapshot_id` is controlled by
        // JsonPropertyName on the details record (SnapshotNotFoundDetails); the two
        // conventions are independent.
        //
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal. The existing
        // string.IsNullOrEmpty(snapshotId) check below is [NotNullWhen(false)]-annotated, so it
        // narrows snapshotId for the rest of this method once ArgumentGuard has already rejected
        // an absent/null snapshotId before dispatch.
        [CosyRequired]
        [Description("The 8-char hex snapshot id returned by apply_edits_verified.")] string? snapshotId = null,
        CancellationToken ct = default)
    {
        // Argument validation: empty/null snapshot_id is rejected with invalid_argument BEFORE
        // dispatching to the host. T-09-14 mitigation. The host would otherwise raise
        // SnapshotNotFound on the empty key — invalid_argument is friendlier for obvious typos.
        if (string.IsNullOrEmpty(snapshotId))
            return Envelope<CommitSnapshotToolData>.Err(
                ToolError.InvalidArgument("snapshot_id", "must_not_be_empty", value: snapshotId));

        try
        {
            var result = await workspaceHost.CommitSnapshotAsync(snapshotId, ct);
            var data = new CommitSnapshotToolData(result.FilesWritten, result.CommitTs);
            logger.LogInformation("commit_snapshot: {Count} file(s) written in {ElapsedMs}ms",
                result.FilesWritten.Count, result.ElapsedMs);
            return Envelope<CommitSnapshotToolData>.Ok(data, result.ElapsedMs);
        }
        catch (SnapshotNotFoundException ex)
        {
            return Envelope<CommitSnapshotToolData>.Err(ToolError.SnapshotNotFound(ex.SnapshotId));
        }
        catch (DiskConflictException ex)
        {
            return Envelope<CommitSnapshotToolData>.Err(
                ToolError.DiskConflict(ex.Path, ex.ExpectedMtime, ex.ActualMtime));
        }
        catch (OperationCanceledException)
        {
            throw; // let cancellation propagate; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during commit_snapshot");
            return Envelope<CommitSnapshotToolData>.Err(ToolError.Internal(ex));
        }
    }
}
