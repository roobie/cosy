using System.ComponentModel;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for discard_snapshot — lives under envelope.data (ADR-0004 §1).
// ADR-0007 §1.6: evicts the snapshot AND its chain descendants from the ring. The
// discarded_ids list always includes the requested id; for cascades it also includes
// every descendant that was evicted in this operation.
public sealed record DiscardSnapshotToolData(
    [property: JsonPropertyName("discarded_ids")] IReadOnlyList<string> DiscardedIds);

/// <summary>
/// discard_snapshot — evict a snapshot from the ring without writing to disk. Descendants
/// in the chain (per ADR-0007 §1.2 lineage) are also evicted. CurrentSolution is unchanged
/// (deferred-promote means there's nothing to revert). ADR-0007 §1.6.
/// </summary>
[McpServerToolType]
public sealed class DiscardSnapshotTool
{
    [McpServerTool(Name = "discard_snapshot")]
    [Description(
        "Evict a snapshot from the workspace's snapshot ring without writing to disk. If the " +
        "snapshot has chain descendants (created with from_snapshot_id pointing at this id, " +
        "transitively), they are also evicted. Returns the list of evicted ids. On unknown id " +
        "(including ring-evicted) returns error.kind=snapshot_not_found.")]
    public async Task<object> DiscardAsync(
        // Wire-name parameter convention: see CommitSnapshotTool comment. Callers pass
        // `snapshotId` (camelCase) on the wire to match the C# parameter name.
        [Description("The 8-char hex snapshot id to evict.")] string snapshotId,
        IWorkspaceHost workspaceHost,
        ILogger<DiscardSnapshotTool> logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(snapshotId))
            return Envelope<DiscardSnapshotToolData>.Err(
                ToolError.InvalidArgument("snapshot_id", "must_not_be_empty", value: snapshotId));

        try
        {
            var result = await workspaceHost.DiscardSnapshotAsync(snapshotId, ct);
            var data = new DiscardSnapshotToolData(result.DiscardedIds);
            logger.LogInformation("discard_snapshot: evicted {Count} id(s) in {ElapsedMs}ms",
                result.DiscardedIds.Count, result.ElapsedMs);
            return Envelope<DiscardSnapshotToolData>.Ok(data, result.ElapsedMs);
        }
        catch (SnapshotNotFoundException ex)
        {
            return Envelope<DiscardSnapshotToolData>.Err(ToolError.SnapshotNotFound(ex.SnapshotId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during discard_snapshot");
            return Envelope<DiscardSnapshotToolData>.Err(ToolError.Internal(ex));
        }
    }
}
