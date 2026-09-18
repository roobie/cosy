using System.ComponentModel;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for workspace_open — lives under envelope.data (ADR-0004 §1).
// Envelope carries is_error/elapsed_ms/etc; this record holds tool-specific fields.
public sealed record WorkspaceOpenToolData(
    [property: JsonPropertyName("project_count")]  int ProjectCount,
    [property: JsonPropertyName("document_count")] int DocumentCount,
    // 1FH-01/1FH-02: one row per loaded Roslyn project instance -- the discovery route for a
    // legal `project` value on compile_check/find_files/run_tests. This array is ALWAYS
    // complete (design_decisions §2 of quick task 260918-1fh): workspace_open stays
    // expectList:false and takes no `max`, and a caller detects completeness for free via
    // projects.length == project_count rather than via envelope truncated/total_count -- a
    // truncated discovery list would be worse than no discovery list, since the caller could
    // not tell "my project is not loaded" from "my project was cut".
    [property: JsonPropertyName("projects")]       IReadOnlyList<WorkspaceOpenProject> Projects,
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

/// <summary>One loaded Roslyn project instance (1FH-01/1FH-02). file_path and
/// target_framework carry JsonIgnore(Never) -- without it the MCP SDK's serializer-wide
/// WhenWritingNull default would drop the key when the value is null, and an omitted key is
/// indistinguishable from an older server that lacks the field entirely. Precedent:
/// ReadSourceToolData.ReadSnapshotId (ReadSourceTool.cs) carries the same override for the
/// same reason; FindTextToolData.SearchedSnapshotId (FindTextTool.cs) has NO override and is
/// dropped from the wire when null -- do not copy that shape here.</summary>
public sealed record WorkspaceOpenProject(
    [property: JsonPropertyName("name")]             string Name,
    [property: JsonPropertyName("assembly_name")]    string AssemblyName,
    [property: JsonPropertyName("file_path"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? FilePath,
    [property: JsonPropertyName("target_framework"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? TargetFramework);

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
        "and resolved TFM for .csproj loads). Path must be absolute; relative paths are rejected " +
        "(see server_cwd in the response, or the error, for why). One workspace at a time: a " +
        "different-path re-open is rejected with a hint to call workspace_close first. " +
        "The response also lists every loaded project under data.projects with its name, assembly_name, " +
        "file_path and target_framework -- one row per project instance, so a multi-targeted project " +
        "appears once per framework with the same assembly_name and file_path. Pass assembly_name as " +
        "find_files' project, assembly_name as compile_check's project, and file_path as run_tests' projectPath.")]
    // Return type is object to sidestep MCP SDK generic-serialization friction; the runtime
    // instance is always Envelope<WorkspaceOpenToolData> and serializes correctly via STJ.
    public async Task<object> OpenAsync(
        IWorkspaceHost workspaceHost,
        ILogger<WorkspaceOpenTool> logger,
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal.
        [CosyRequired]
        [Description("Absolute path to a .sln, .slnx, or .csproj file. Relative paths are rejected " +
                     "-- this server's cwd does not reliably match your session's directory (e.g. " +
                     "across git worktrees).")] string? path = null,
        // Phase 12.3 D-12/second-order compile trap: this tool is the ONLY one of the 13 where
        // moving the tool parameter after the DI block puts it before a trailing CancellationToken
        // that previously had no default -- without `= default` here, CS1737 recurs on this file
        // specifically (RESEARCH.md confirmed by reading all 18 signatures). WorkspaceCloseTool's
        // own bare `ct` is left untouched: nothing moves past it there, so it carries no such risk.
        CancellationToken ct = default)
    {
        // ArgumentGuard rejects an absent/null path (CosyRequired) before this handler ever runs
        // -- this binding is a compiler satisfaction only, not a second requiredness check.
        var pathText = path!;

        // ADR-0022: relative paths are rejected outright, before any filesystem access. This
        // server's cwd is fixed at launch and is not necessarily the caller's directory (e.g.
        // inside a git worktree), so resolving against it can silently succeed against the wrong
        // tree -- rejection removes the hazard instead of just reporting it after the fact.
        if (!Path.IsPathRooted(pathText))
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "relative_path_rejected", value: pathText,
                    message: $"relative paths are rejected: {pathText} (pass an absolute path; this " +
                             $"server's cwd is {Directory.GetCurrentDirectory()} and may not match " +
                             $"your session's directory, e.g. inside a git worktree)"));

        var absolutePath = pathText;

        // Path-shape validation is user-input error → invalid_argument (D-06).
        if (Directory.Exists(absolutePath))
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "is_directory", value: pathText,
                    message: $"expected .sln or .csproj file, got directory: {pathText}"));
        if (!File.Exists(absolutePath))
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "not_found", value: pathText,
                    message: $"path not found: {pathText}"));

        var ext = Path.GetExtension(absolutePath).ToLowerInvariant();
        if (ext != ".sln" && ext != ".slnx" && ext != ".csproj")
            return Envelope<WorkspaceOpenToolData>.Err(
                ToolError.InvalidArgument("path", "unsupported_extension", value: pathText,
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
                Projects: result.Projects
                    .Select(p => new WorkspaceOpenProject(p.Name, p.AssemblyName, p.FilePath, p.TargetFramework))
                    .ToArray(),
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
