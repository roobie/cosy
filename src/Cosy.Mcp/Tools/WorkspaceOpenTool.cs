using System.ComponentModel;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for workspace_open — lives under envelope.data (ADR-0004 §1).
// Envelope carries is_error/elapsed_ms/etc; this record holds tool-specific fields.
public sealed record WorkspaceOpenToolData(
    [property: JsonPropertyName("project_count")]  int ProjectCount,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    [property: JsonPropertyName("diagnostics")]    IReadOnlyList<WorkspaceOpenDiagnostic> Diagnostics,
    [property: JsonPropertyName("resolved_tfm")]   string? ResolvedTfm,
    [property: JsonPropertyName("msbuild")]        WorkspaceOpenMsBuild? MsBuild,
    // ADR-0020. The directory this server resolves relative paths against. The caller cannot see
    // it any other way and it is NOT necessarily the session's own directory: a Claude Code MCP
    // server keeps its launch cwd for the process lifetime and is not respawned when the session
    // moves into a git worktree.
    [property: JsonPropertyName("server_cwd")]     string ServerCwd);

public sealed record WorkspaceOpenDiagnostic(
    [property: JsonPropertyName("kind")]    string Kind,
    [property: JsonPropertyName("message")] string Message);

/// <summary>Which MSBuild the host resolved. Cosy evaluates projects with the SDK
/// installed on the machine, not one it ships — echoing this turns "why won't my
/// solution load" into a one-line answer (ADR-0011).</summary>
public sealed record WorkspaceOpenMsBuild(
    [property: JsonPropertyName("kind")]    string Kind,
    [property: JsonPropertyName("path")]    string Path,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// workspace_open — load a .sln or .csproj into the resident MSBuildWorkspace.
/// Path validation happens here; actual Roslyn work is delegated to IWorkspaceHost.
/// Diagnostics are expected output (is_error:false even with Kind=Failure present).
/// </summary>
[McpServerToolType]
public sealed class WorkspaceOpenTool
{
    [McpServerTool(Name = "workspace_open")]
    [Description(
        "Load a .sln, .slnx, or .csproj file into the resident MSBuildWorkspace and return structured " +
        "load metadata (project count, document count, elapsed ms, WorkspaceFailed diagnostics, " +
        "and resolved TFM for .csproj loads). Relative paths are resolved against the server's CWD. " +
        "One workspace at a time: a different-path re-open is rejected with a hint to call " +
        "workspace_close first.")]
    // Return type is object to sidestep MCP SDK generic-serialization friction; the runtime
    // instance is always Envelope<WorkspaceOpenToolData> and serializes correctly via STJ.
    public async Task<object> OpenAsync(
        [Description("Absolute or CWD-relative path to a .sln, .slnx, or .csproj file")] string path,
        IWorkspaceHost workspaceHost,
        ILogger<WorkspaceOpenTool> logger,
        CancellationToken ct)
    {
        // 1. Resolve + validate path BEFORE delegating. This keeps the host free of
        //    filesystem concerns and produces deterministic error messages.
        var absolutePath = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);

        // Path-shape validation is user-input error → invalid_argument (D-06).
        if (Directory.Exists(absolutePath))
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "is_directory", value: path,
                    message: $"expected .sln or .csproj file, got directory: {path}"));
        if (!File.Exists(absolutePath))
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "not_found", value: path,
                    message: Path.IsPathRooted(path)
                        ? $"path not found: {path}"
                        // A RELATIVE path that misses is the worktree trap: it resolved against
                        // this server's cwd, which the caller may not share. Name both, so the
                        // failure diagnoses itself instead of reading as "the file is gone".
                        : $"path not found: {path} (resolved to {absolutePath} against server " +
                          $"cwd {Directory.GetCurrentDirectory()}; pass an absolute path)"));

        var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (ext != ".sln" && ext != ".slnx" && ext != ".csproj")
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "unsupported_extension", value: path,
                    message: $"expected .sln, .slnx, or .csproj, got: {ext}"));

        // 2. Delegate to the singleton host.
        try
        {
            var result = await workspaceHost.LoadAsync(absolutePath, ct);
            // LoadAsync failures are Roslyn/MSBuild-side anomalies, not user input → internal_error.
            if (result.IsError)
                return Envelope<WorkspaceOpenToolData>.Err(
                    ToolError.Internal(new InvalidOperationException(result.ErrorMessage!)));

            var failures = result.Diagnostics.Count(d => d.Kind == "Failure");
            var warnings = result.Diagnostics.Count(d => d.Kind == "Warning");
            var summary =
                $"Loaded {result.ProjectCount} project(s) in {result.ElapsedMs}ms, " +
                $"{failures} failures, {warnings} warnings";
            logger.LogInformation("{Summary}", summary);

            var data = new WorkspaceOpenToolData(
                ProjectCount: result.ProjectCount,
                DocumentCount: result.DocumentCount,
                Diagnostics: result.Diagnostics
                    .Select(d => new WorkspaceOpenDiagnostic(d.Kind, d.Message))
                    .ToArray(),
                ResolvedTfm: result.ResolvedTfm,
                MsBuild: result.MsBuild is { } mb
                    ? new WorkspaceOpenMsBuild(mb.Kind, mb.Path, mb.Version)
                    : null,
                ServerCwd: Directory.GetCurrentDirectory());

            return Envelope<WorkspaceOpenToolData>.Ok(data, result.ElapsedMs);
        }
        catch (OperationCanceledException)
        {
            throw; // let cancellation propagate; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during workspace load");
            return Envelope<WorkspaceOpenToolData>.Err(ToolError.Internal(ex));
        }
    }
}
