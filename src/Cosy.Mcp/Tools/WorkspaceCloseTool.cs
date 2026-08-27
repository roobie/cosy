using System.ComponentModel;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for workspace_close — lives under envelope.data (ADR-0004 §1).
// closed=false is the idempotent "nothing was loaded" answer, not an error (ADR-0005 §D-03).
public sealed record WorkspaceCloseToolData(
    [property: JsonPropertyName("closed")]     bool Closed,
    [property: JsonPropertyName("prior_path")] string? PriorPath);

/// <summary>
/// workspace_close — dispose the resident MSBuildWorkspace so a different .sln/.slnx/.csproj
/// can be loaded in the same session. ADR-0005 §D-01 (explicit close), §D-02 (snapshots
/// cleared), §D-03 (idempotent), §D-06 (waits for in-flight read leases to drain).
/// </summary>
[McpServerToolType]
public sealed class WorkspaceCloseTool
{
    [McpServerTool(Name = "workspace_close")]
    [Description(
        "Dispose the resident MSBuildWorkspace, clearing all snapshots. After close, " +
        "workspace_open may load a different .sln/.slnx/.csproj. Idempotent: returns " +
        "closed:false if no workspace is loaded. Waits for in-flight read tools to " +
        "complete before disposing.")]
    // Return type is object to sidestep MCP SDK generic-serialization friction; the runtime
    // instance is always Envelope<WorkspaceCloseToolData> and serializes correctly via STJ.
    public async Task<object> CloseAsync(
        IWorkspaceHost workspaceHost,
        ILogger<WorkspaceCloseTool> logger,
        CancellationToken ct)
    {
        try
        {
            var result = await workspaceHost.CloseAsync(ct);
            var data = new WorkspaceCloseToolData(result.Closed, result.PriorPath);
            if (result.Closed)
                logger.LogInformation("workspace_close: closed {Path} in {ElapsedMs}ms",
                    result.PriorPath, result.ElapsedMs);
            else
                logger.LogDebug("workspace_close: nothing loaded (idempotent no-op)");
            return Envelope<WorkspaceCloseToolData>.Ok(data, result.ElapsedMs);
        }
        catch (OperationCanceledException)
        {
            throw; // let cancellation propagate; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during workspace close");
            return Envelope<WorkspaceCloseToolData>.Err(ToolError.Internal(ex));
        }
    }
}
