using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Search;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for rename — lives under envelope.data (ADR-0004 §1).
// Spans use canonical Cosy.Mcp.Contracts.Span {start, end} per D-04.
// Edits + diagnostics carry span AND line/column/end_line/end_column adjacent per D-05.
public sealed record RenameToolData(
    [property: JsonPropertyName("edits")]       IReadOnlyList<RenameEdit> Edits,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<RenameDiagnostic> Diagnostics,
    [property: JsonPropertyName("findings")]    IReadOnlyList<string> Findings);

public sealed record RenameEdit(
    [property: JsonPropertyName("file")]       string File,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn,
    [property: JsonPropertyName("old_text")]   string OldText,
    [property: JsonPropertyName("new_text")]   string NewText);

public sealed record RenameDiagnostic(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("message")]    string Message,
    [property: JsonPropertyName("file")]       string File,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

/// <summary>
/// rename -- dry-run semantic rename via Roslyn Renamer. Resolves symbol by
/// DocumentationCommentId, returns full edit set with baseline-subtracted diagnostics
/// and a findings array for occurrences Renamer may have missed (known bugs #58463/#56940).
/// No disk writes. No Console.Write (PITFALLS §6).
/// </summary>
[McpServerToolType]
public sealed class RenameTool
{
    [McpServerTool(Name = "rename")]
    [Description("Dry-run semantic rename of a C# symbol. Resolves symbol by DocumentationCommentId across all " +
        "projects, returns the full edit set (file, span, old_text, new_text) plus baseline-subtracted " +
        "post-rename diagnostics and a findings array for known Renamer miss bugs. " +
        "Requires workspace_open first. dry_run must be true.")]
    public async Task<object> RenameAsync(
        [Description("DocumentationCommentId of the symbol to rename (e.g. M:Ns.Type.Method(System.String)). " +
            "Accepts a DocumentationCommentId (preferred) or a fuzzy name; multi-match returns is_error:true with candidates.")] string symbol,
        [Description("New name for the symbol")] string newName,
        [Description("Must be true; false is not supported in spike")] bool dryRun,
        IWorkspaceHost workspaceHost,
        FuzzySymbolResolver resolver,
        ILogger<RenameTool> logger,
        [Description("Max diagnostic rows to echo in data.diagnostics (default 500). Edits are always returned in full.")] int? max = null,
        CancellationToken ct = default)
    {
        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<RenameToolData>.Err(ToolError.WorkspaceNotLoaded());

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<RenameToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));
        var cap = max ?? 500;

        if (!dryRun)
            // TODO(Plan 05/06): ToolError.InvalidArgument kind for dry_run.
            return Envelope<RenameToolData>.Err(ToolError.Internal(new Exception("dry_run=false not supported in spike")));

        // D-16: reject invalid C# identifiers before any Roslyn work (cheapest check first).
        // SyntaxFacts.IsValidIdentifier enforces Unicode identifier rules and rejects plain keywords
        // (class, int, ...). Contextual keywords (var, dynamic) ARE legal identifiers — the compiler
        // disambiguates by context, so we don't reject them here.
        if (string.IsNullOrEmpty(newName) || !SyntaxFacts.IsValidIdentifier(newName))
        {
            return Envelope<RenameToolData>.Err(
                ToolError.InvalidArgument(
                    param: "new_name",
                    reason: "not_a_valid_identifier",
                    value: newName));
        }

        logger.LogDebug("rename: symbol={Symbol}, new_name={NewName}", symbol, newName);

        var sw = Stopwatch.StartNew();

        try
        {
            // --- SYMBOL RESOLUTION (D-01, D-02, D-03) ---
            var resolved = await resolver.ResolveAsync(symbol, solution, ct);
            ISymbol targetSymbol;
            switch (resolved)
            {
                case ResolveResult.ExactMatch m:
                    targetSymbol = m.Symbol;
                    break;
                case ResolveResult.AmbiguousMatches a:
                    logger.LogWarning("rename: ambiguous symbol: {Symbol} ({Count} candidates)", symbol, a.TopN.Count);
                    // D-11: structured ambiguous error with candidate DTOs.
                    return Envelope<RenameToolData>.Err(
                        ToolError.AmbiguousSymbol(query: symbol, candidates: a.TopN.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                case ResolveResult.NoMatch n:
                    logger.LogWarning("rename: symbol not found: {Symbol}", n.Query);
                    // D-11: structured not-found error with near-miss DTOs (may be empty).
                    return Envelope<RenameToolData>.Err(
                        ToolError.SymbolNotFound(query: n.Query, nearMisses: n.NearMisses.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                default:
                    return Envelope<RenameToolData>.Err(ToolError.Internal(new Exception("internal: unhandled ResolveResult")));
            }

            // --- RENAME INVOCATION (D-04, D-05, D-06) ---
            var options = new SymbolRenameOptions(
                RenameOverloads: false, RenameInStrings: false,
                RenameInComments: false, RenameFile: false);
            var renamedSolution = await Renamer.RenameSymbolAsync(
                solution, targetSymbol, options, newName, ct);

            var solutionDir = SolutionPaths.GetSolutionDirectory(solution);

            // --- EDIT-SET EXTRACTION (D-10, D-11) ---
            // GetChanges returns only documents with actual differences.
            var solutionChanges = renamedSolution.GetChanges(solution);
            var edits = new List<RenameEdit>();
            var changedDocIds = new List<DocumentId>();

            foreach (var projectChanges in solutionChanges.GetProjectChanges())
            {
                foreach (var docId in projectChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
                {
                    changedDocIds.Add(docId);

                    var oldDoc = solution.GetDocument(docId)!;
                    var newDoc = renamedSolution.GetDocument(docId)!;
                    var oldText = await oldDoc.GetTextAsync(ct);
                    var newText = await newDoc.GetTextAsync(ct);

                    var relPath = Path.GetRelativePath(solutionDir, oldDoc.FilePath!).Replace('\\', '/');

                    foreach (var change in newText.GetTextChanges(oldText))
                    {
                        // D-04: {start, end} end-exclusive. TextSpan.End == Start + Length.
                        // D-05: derive line/column from old text line map for human readability.
                        var lineSpan = oldText.Lines.GetLinePositionSpan(change.Span);
                        edits.Add(new RenameEdit(
                            File: relPath,
                            Span: new Span(change.Span.Start, change.Span.End),
                            Line: lineSpan.Start.Line + 1,
                            Column: lineSpan.Start.Character + 1,
                            EndLine: lineSpan.End.Line + 1,
                            EndColumn: lineSpan.End.Character + 1,
                            OldText: oldText.GetSubText(change.Span).ToString(),
                            NewText: change.NewText ?? ""));
                    }
                }
            }

            // --- BASELINE SUBTRACTION (D-08, D-09) ---
            var touchedProjectIds = changedDocIds
                .Select(id => id.ProjectId)
                .Distinct()
                .ToHashSet();

            var baseline = new HashSet<(string Id, string Message)>();
            foreach (var projectId in touchedProjectIds)
            {
                var project = solution.GetProject(projectId)!;
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;

                var changedTreeSet = new HashSet<SyntaxTree>();
                foreach (var docId in changedDocIds.Where(id => id.ProjectId == projectId))
                {
                    var doc = solution.GetDocument(docId)!;
                    var tree = await doc.GetSyntaxTreeAsync(ct);
                    if (tree != null) changedTreeSet.Add(tree);
                }

                foreach (var d in compilation.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && !changedTreeSet.Contains(d.Location.SourceTree)) continue;
                    baseline.Add((d.Id, d.GetMessage()));
                }
            }

            // Post-rename diagnostics, baseline-subtracted.
            // D-17 (08-06): Compilation.GetDiagnostics merges parse + declaration + method-body +
            // compile diagnostics, so syntax-level errors from the renamed source text ARE surfaced
            // via this path. No separate tree.GetDiagnostics() iteration is required. The D-16
            // identifier gate above rejects the most common shapes that would yield parse errors
            // before any Roslyn work happens.
            var netNewDiagnostics = new List<RenameDiagnostic>();
            foreach (var projectId in touchedProjectIds)
            {
                var renamedProject = renamedSolution.GetProject(projectId)!;
                var compilation = await renamedProject.GetCompilationAsync(ct);
                if (compilation is null) continue;

                var changedTreeSet = new HashSet<SyntaxTree>();
                foreach (var docId in changedDocIds.Where(id => id.ProjectId == projectId))
                {
                    var doc = renamedSolution.GetDocument(docId)!;
                    var tree = await doc.GetSyntaxTreeAsync(ct);
                    if (tree != null) changedTreeSet.Add(tree);
                }

                foreach (var d in compilation.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && !changedTreeSet.Contains(d.Location.SourceTree)) continue;
                    if (baseline.Contains((d.Id, d.GetMessage()))) continue;

                    // D-05: emit span {start, end} alongside 1-based line/column.
                    var lineSpan = d.Location.GetLineSpan();
                    var src = d.Location.SourceSpan;
                    netNewDiagnostics.Add(new RenameDiagnostic(
                        Severity: d.Severity.ToString(),
                        Id: d.Id,
                        Message: d.GetMessage(),
                        File: lineSpan.Path,
                        Span: new Span(src.Start, src.End),
                        Line: lineSpan.StartLinePosition.Line + 1,
                        Column: lineSpan.StartLinePosition.Character + 1,
                        EndLine: lineSpan.EndLinePosition.Line + 1,
                        EndColumn: lineSpan.EndLinePosition.Character + 1));
                }
            }

            // --- FINDINGS: text-search for remaining occurrences of old name (D-14) ---
            var findings = new List<string>();
            foreach (var docId in changedDocIds)
            {
                var doc = renamedSolution.GetDocument(docId)!;
                var text = (await doc.GetTextAsync(ct)).ToString();
                var relPath = Path.GetRelativePath(solutionDir, doc.FilePath!).Replace('\\', '/');
                var idx = 0;
                while ((idx = text.IndexOf(targetSymbol.Name, idx, StringComparison.Ordinal)) >= 0)
                {
                    var lineNum = text[..idx].Count(c => c == '\n') + 1;
                    findings.Add($"Renamer missed occurrence of old name '{targetSymbol.Name}' in {relPath}:{lineNum} (possible known bug #58463/#56940)");
                    idx += targetSymbol.Name.Length;
                }
            }

            // --- RESPONSE (D-12) ---
            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            var fileCount = changedDocIds
                .Select(id => renamedSolution.GetDocument(id)?.FilePath)
                .Where(p => p != null)
                .Distinct()
                .Count();
            var errorCount = netNewDiagnostics.Count(d => d.Severity == "Error");
            var summary = $"rename: {edits.Count} edit(s) across {fileCount} file(s), {errorCount} new error(s) in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            // D-13: truncate diagnostics at cap; envelope surfaces total.
            var totalDiagnostics = netNewDiagnostics.Count;
            var truncatedDiagnostics = netNewDiagnostics.Count > cap
                ? netNewDiagnostics.Take(cap).ToList()
                : (IReadOnlyList<RenameDiagnostic>)netNewDiagnostics;
            var isTruncated = totalDiagnostics > truncatedDiagnostics.Count;

            var data = new RenameToolData(edits, truncatedDiagnostics, findings);
            // D-10: rename echoes resolved_symbol uniformly with the other symbol-taking tools.
            return Envelope<RenameToolData>.Ok(
                data,
                elapsedMs,
                resolvedSymbol: new ResolvedSymbol(
                    targetSymbol.GetDocumentationCommentId(),
                    targetSymbol.ToDisplayString()),
                truncated: isTruncated,
                totalCount: totalDiagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during rename");
            return Envelope<RenameToolData>.Err(ToolError.Internal(ex));
        }
    }
}
