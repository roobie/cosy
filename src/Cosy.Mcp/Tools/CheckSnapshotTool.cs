using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// A net-new diagnostic introduced by a staged snapshot (vs its parent). Same shape as the
// per-tool diagnostic records (ExtractMethodDiagnostic) for wire consistency (ADR-0004).
public sealed record CheckSnapshotDiagnostic(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("message")]    string Message,
    [property: JsonPropertyName("file")]       string File,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

public sealed record CheckSnapshotResult(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("parent_id")]   string? ParentId,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CheckSnapshotDiagnostic> Diagnostics,
    [property: JsonPropertyName("error_count")] int ErrorCount);

/// <summary>
/// check_snapshot — non-destructive diagnostic check of a staged (uncommitted) snapshot
/// (ADR-0010). Returns baseline-subtracted diagnostics (vs the snapshot's parent) over the
/// changed documents — the net-new warnings/errors a commit would introduce. Never promotes,
/// evicts, or writes to disk. Reopens the Phase-9 D-03 deferral (compile_check_against_snapshot)
/// now that Phase 10's snapshot-composition outcome is known.
/// </summary>
[McpServerToolType]
public sealed class CheckSnapshotTool
{
    [McpServerTool(Name = "check_snapshot")]
    [Description(
        "Non-destructive diagnostic check of a staged (uncommitted) snapshot. Returns " +
        "baseline-subtracted diagnostics (vs the snapshot's parent) over the changed documents — " +
        "the net-new warnings/errors a commit would introduce. Does NOT write to disk or alter the " +
        "ring. Unknown/evicted id returns error.kind=snapshot_not_found.")]
    public async Task<object> CheckAsync(
        [Description("The 8-char hex snapshot id to check.")] string snapshotId,
        IWorkspaceHost workspaceHost,
        ILogger<CheckSnapshotTool> logger,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(snapshotId))
            return Envelope<CheckSnapshotResult>.Err(
                ToolError.InvalidArgument("snapshot_id", "must_not_be_empty", value: snapshotId));

        var sw = Stopwatch.StartNew();
        using var lease = workspaceHost.RentSolution(out var current);
        if (current is null)
            return Envelope<CheckSnapshotResult>.Err(ToolError.WorkspaceNotLoaded());

        try
        {
            if (!workspaceHost.TryGetSnapshot(snapshotId, out var entry) || entry is null)
                return Envelope<CheckSnapshotResult>.Err(ToolError.SnapshotNotFound(snapshotId));

            var staged = entry.Snapshot;
            var baseSolution = ReadSnapshotTool.ResolveParentSolution(workspaceHost, entry, current!);

            // Collect changed doc ids + their owning projects.
            var changedDocIds = new List<DocumentId>();
            var changedProjectIds = new HashSet<ProjectId>();
            foreach (var projChanges in staged.GetChanges(baseSolution).GetProjectChanges())
            {
                changedProjectIds.Add(projChanges.NewProject.Id);
                foreach (var docId in projChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
                    changedDocIds.Add(docId);
            }

            // Baseline (parent) diagnostics keyed by (id, message) over the changed projects.
            var baseline = new HashSet<(string, string)>();
            foreach (var projId in changedProjectIds)
            {
                var baseProj = baseSolution.GetProject(projId);
                var baseComp = baseProj is null ? null : await baseProj.GetCompilationAsync(ct);
                if (baseComp is null) continue;
                foreach (var d in baseComp.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    baseline.Add((d.Id, d.GetMessage()));
                }
            }

            // Post diagnostics over the snapshot's changed trees, minus baseline.
            var changedTrees = new HashSet<SyntaxTree>();
            foreach (var docId in changedDocIds)
            {
                var tree = await staged.GetDocument(docId)!.GetSyntaxTreeAsync(ct);
                if (tree is not null) changedTrees.Add(tree);
            }

            var solutionDir = Path.GetDirectoryName(staged.FilePath ?? current!.FilePath) ?? "";
            var diagnostics = new List<CheckSnapshotDiagnostic>();
            foreach (var projId in changedProjectIds)
            {
                var proj = staged.GetProject(projId);
                var comp = proj is null ? null : await proj.GetCompilationAsync(ct);
                if (comp is null) continue;
                foreach (var d in comp.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree is not null && !changedTrees.Contains(d.Location.SourceTree)) continue;
                    if (baseline.Contains((d.Id, d.GetMessage()))) continue;
                    var ls = d.Location.GetLineSpan();
                    var ss = d.Location.SourceSpan;
                    var path = d.Location.SourceTree?.FilePath is { } fp
                        ? Path.GetRelativePath(solutionDir, fp).Replace('\\', '/')
                        : ls.Path;
                    diagnostics.Add(new CheckSnapshotDiagnostic(
                        Severity: d.Severity.ToString(),
                        Id: d.Id,
                        Message: d.GetMessage(),
                        File: path,
                        Span: new Span(ss.Start, ss.End),
                        Line: ls.StartLinePosition.Line + 1,
                        Column: ls.StartLinePosition.Character + 1,
                        EndLine: ls.EndLinePosition.Line + 1,
                        EndColumn: ls.EndLinePosition.Character + 1));
                }
            }

            sw.Stop();
            var errorCount = diagnostics.Count(x => x.Severity == "Error");
            logger.LogInformation(
                "check_snapshot: {DiagCount} net-new diagnostic(s) ({ErrCount} error) for {SnapshotId} in {ElapsedMs}ms",
                diagnostics.Count, errorCount, snapshotId, sw.ElapsedMilliseconds);
            return Envelope<CheckSnapshotResult>.Ok(
                new CheckSnapshotResult(snapshotId, entry.ParentId, diagnostics, errorCount),
                (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during check_snapshot");
            return Envelope<CheckSnapshotResult>.Err(ToolError.Internal(ex));
        }
    }
}
