using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Refactor;
using Cosy.Mcp.Search;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// extract_method — dry-run extract refactor via Roslyn (Phase 10 D-05, D-07).
// Response shape per D-07: {snapshot_id, edits[file, span, new_text_length],
// original_span, post_extract_diagnostics, action_title}. No new_text in echo
// (D-17 spirit — extracted body may carry secrets/noise; length round-trip-verifies).

public sealed record ExtractMethodResult(
    [property: JsonPropertyName("snapshot_id")]              string SnapshotId,
    [property: JsonPropertyName("edits")]                    IReadOnlyList<ExtractMethodEdit> Edits,
    [property: JsonPropertyName("original_span")]            Span OriginalSpan,
    [property: JsonPropertyName("post_extract_diagnostics")] IReadOnlyList<ExtractMethodDiagnostic> PostExtractDiagnostics,
    [property: JsonPropertyName("action_title")]             string ActionTitle);

public sealed record ExtractMethodEdit(
    [property: JsonPropertyName("file")]            string File,
    [property: JsonPropertyName("span")]            Span Span,
    [property: JsonPropertyName("new_text_length")] int NewTextLength);

public sealed record ExtractMethodDiagnostic(
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
/// extract_method — dry-run-by-default extract-method refactor. Invokes Roslyn's
/// ExtractMethodCodeRefactoringProvider via the isolated ExtractMethodInvoker harness,
/// renames the synthetic method (default "NewMethod") to the agent-supplied newMethodName
/// via Renamer.RenameSymbolAsync (locates the RenameAnnotation-tagged identifier in the
/// changed solution), and composes the result into a single snapshot via
/// WorkspaceHost (deferred-promote per ADR-0007 §1.1 — CurrentSolution is NOT mutated).
///
/// Phase 10 D-13: trace auto-decoration via the Phase 9 filter — no tool-specific
/// trace code lives here. D-08: no new ToolError kind introduced (deferred to
/// post-implementation discovery per ADR-0008).
/// </summary>
[McpServerToolType]
public sealed class ExtractMethodTool
{
    [McpServerTool(Name = "extract_method")]
    [Description("Dry-run-by-default extract-method refactor. Resolves the file by suffix-match, " +
        "validates the span, invokes Roslyn's ExtractMethodCodeRefactoringProvider, renames the " +
        "synthetic method to newMethodName via Renamer.RenameSymbolAsync, composes the change set " +
        "into a single snapshot (deferred-promote), and returns the edit set plus baseline-subtracted " +
        "post-extract diagnostics. Requires workspace_open first. dryRun must be true.")]
    public async Task<object> ExtractAsync(
        [Description("Relative or absolute file path; suffix-matched against the workspace documents (one-and-only-one match required).")] string file,
        [Description("Extract span as {start, end} zero-based, end-exclusive UTF-16 code unit " +
            "offsets into the document's Roslyn SourceText (ADR-0004 §3) -- never byte offsets " +
            "from wc -c or ls -l, and wc -m is also wrong since it disagrees with SourceText on " +
            "surrogate pairs; see ADR-0004 §3 for where a correct offset comes from.")] Span span,
        [Description("Name of the new method. Must be a valid C# identifier.")] string newMethodName,
        [Description("Must be true; false is not supported in spike (RenameTool precedent).")] bool dryRun,
        IWorkspaceHost workspaceHost,
        ILogger<ExtractMethodTool> logger,
        [Description("Optional snapshot id to chain extract off of (ADR-0007 §1.2). Defaults to CurrentSolution.")] string? fromSnapshotId = null,
        [Description("Optional timeout in milliseconds; linked to caller CancellationToken.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // --- ARG VALIDATION (no workspace access required) ---

        // RenameTool precedent: dryRun=false rejected as invalid_argument. The wire arg
        // name is "dryRun" (camelCase per Phase 9 D-04); error details param key follows
        // snake_case wire-key convention.
        if (!dryRun)
        {
            return Envelope<ExtractMethodResult>.Err(
                ToolError.InvalidArgument(param: "dry_run", reason: "must_be_true", value: dryRun));
        }

        // D-16 identifier gate — reject invalid C# identifiers before any Roslyn work.
        if (string.IsNullOrEmpty(newMethodName) || !SyntaxFacts.IsValidIdentifier(newMethodName))
        {
            return Envelope<ExtractMethodResult>.Err(
                ToolError.InvalidArgument(
                    param: "new_method_name",
                    reason: "not_a_valid_identifier",
                    value: newMethodName));
        }

        // Linked CTS: caller's ct + optional timeoutMs. opCt is what Roslyn sees.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms) linkedCts.CancelAfter(ms);
        var opCt = linkedCts.Token;

        logger.LogDebug("extract_method: file={File}, span={Start}..{End}, new_method_name={Name}",
            file, span.Start, span.End, newMethodName);

        var sw = Stopwatch.StartNew();

        // --- READ LEASE + BASE SOLUTION RESOLUTION ---

        var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
        {
            lease.Dispose();
            return Envelope<ExtractMethodResult>.Err(ToolError.WorkspaceNotLoaded());
        }

        // ADR-0007 §1.2 chain head selection — see ApplyEditsVerifiedTool.cs:128-138.
        var baseSolution = solution!;
        if (fromSnapshotId is not null)
        {
            if (!workspaceHost.TryGetSnapshot(fromSnapshotId, out var entry) || entry is null)
            {
                lease.Dispose();
                return Envelope<ExtractMethodResult>.Err(ToolError.SnapshotNotFound(fromSnapshotId));
            }
            baseSolution = entry.Snapshot;
        }

        try
        {
            // --- FILE RESOLUTION (suffix-match; AEV precedent lines 152-170) ---
            var matches = baseSolution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null &&
                            d.FilePath.EndsWith(file, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0)
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.InvalidArgument("file", "not_found_in_workspace", value: file),
                    (int)sw.ElapsedMilliseconds);
            }
            if (matches.Count > 1)
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.InvalidArgument("file", "ambiguous_path", value: file,
                        message: $"ambiguous file path: {file} matches {matches.Count} documents"),
                    (int)sw.ElapsedMilliseconds);
            }
            var document = matches[0];

            // --- SPAN VALIDATION (AEV precedent lines 190-204) ---
            var sourceText = await document.GetTextAsync(opCt);
            if (span.Start < 0 || span.End < span.Start)
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.InvalidArgument("span", "inverted_or_negative", value: span),
                    (int)sw.ElapsedMilliseconds);
            }
            if (span.End > sourceText.Length)
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.OutOfBounds(file, span, sourceText.Length),
                    (int)sw.ElapsedMilliseconds);
            }

            // --- GENERATED-CODE REJECTION (threat T-10-02-06; D-08 unsupported_option path) ---
            if (ExtractMethodInvoker.IsGeneratedCode(document, sourceText))
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.UnsupportedOption(
                        param: "file",
                        requested: file,
                        supported: new[] { "non_generated_files_only" }),
                    (int)sw.ElapsedMilliseconds);
            }

            // --- BASELINE DIAGNOSTICS (RenameTool.cs:174-200 — single touched project) ---
            var touchedProjectId = document.Project.Id;
            var baseCompilation = await document.Project.GetCompilationAsync(opCt);
            var baseTree = await document.GetSyntaxTreeAsync(opCt);
            var baseline = new HashSet<(string Id, string Message)>();
            if (baseCompilation is not null && baseTree is not null)
            {
                foreach (var d in baseCompilation.GetDiagnostics(opCt))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && d.Location.SourceTree != baseTree) continue;
                    baseline.Add((d.Id, d.GetMessage()));
                }
            }

            // --- ROSLYN INVOCATION ---
            var textSpan = new TextSpan(span.Start, span.End - span.Start);
            Solution editedSolution;
            string actionTitle;
            var outcome = await ExtractMethodInvoker.InvokeAsync(document, textSpan, opCt);
            switch (outcome)
            {
                case ExtractInvocationOutcome.RoslynRefused:
                    // D-08 — Roslyn refused → unsupported_option (no new kind introduced).
                    return Envelope<ExtractMethodResult>.Err(
                        ToolError.UnsupportedOption(
                            param: "span",
                            requested: $"extract_at_{span.Start}_{span.End}",
                            supported: new[] { "roslyn_compatible_extract_span" }),
                        (int)sw.ElapsedMilliseconds);
                case ExtractInvocationOutcome.Success success:
                    editedSolution = success.ChangedSolution;
                    actionTitle = success.ActionTitle;
                    break;
                default:
                    throw new InvalidOperationException("Unknown ExtractInvocationOutcome subtype");
            }

            // --- SYNTHETIC METHOD NAME → AGENT-SUPPLIED NAME ---
            // Roslyn extracts the new method under a synthetic name ("NewMethod", or a
            // uniquified "NewMethodN" when that identifier is already in scope). Locate the
            // synthetic method's SYMBOL and rename it via Renamer.RenameSymbolAsync so only
            // that declaration + its references change, preserving the auto-inferred
            // signature (params, ref/out) intact (Plan 10-03 fixture #3).
            //
            // Locators tried in order (see docs/ideas/extract-method-protocol-gaps.md):
            //   1. RenameAnnotation (kind "CodeAction_Rename") — present only on Roslyn
            //      versions that emit it; CSharp.Features 5.3.0 does NOT.
            //   2. Structural tree-diff — the MethodDeclarationSyntax present in the changed
            //      tree but absent from the base tree. This is the reliable path. A
            //      text-replace of "\bNewMethod\b" is unsound: it silently corrupts any
            //      pre-existing identifier named NewMethod and misses uniquified synthetic
            //      names (NewMethod1) — empirically confirmed (FM-1).
            var solutionChangesPreRename = editedSolution.GetChanges(baseSolution);
            var syntheticMethodSymbol = await LocateSyntheticMethodAsync(
                baseSolution, editedSolution, solutionChangesPreRename, opCt);

            if (syntheticMethodSymbol is null)
            {
                // Loud failure — NEVER text-replace (would silently corrupt unrelated
                // symbols, threat T-10-02-08). Surface the inability to locate the new
                // method rather than risk wrong edits.
                logger.LogError(
                    "ExtractMethod: could not locate the synthetic method declaration to rename to {NewName}",
                    newMethodName);
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.Internal(new InvalidOperationException(
                        $"extract_method: synthetic method declaration not found for rename to '{newMethodName}'")),
                    (int)sw.ElapsedMilliseconds);
            }

            var renameOptions = new SymbolRenameOptions(
                RenameOverloads: false,
                RenameInStrings: false,
                RenameInComments: false,
                RenameFile: false);
            editedSolution = await Renamer.RenameSymbolAsync(
                editedSolution, syntheticMethodSymbol, renameOptions, newMethodName, opCt);

            // --- DEFER-TRIGGER DEFENSE (D-03; tool-layer beyond Wave-0 probe) ---
            // Recompute SolutionChanges AFTER rename — Renamer may have touched additional
            // documents (callers in other files). The single-project invariant from 10-01
            // remains the relevant runtime check.
            var solutionChanges = editedSolution.GetChanges(baseSolution);
            var projectChanges = solutionChanges.GetProjectChanges().ToList();
            if (projectChanges.Count > 1)
            {
                return Envelope<ExtractMethodResult>.Err(
                    ToolError.UnsupportedOption(
                        param: "(workspace_topology)",
                        requested: $"multi_project_extract_{projectChanges.Count}_projects",
                        supported: new[] { "single_project_extract" }),
                    (int)sw.ElapsedMilliseconds);
            }

            // --- EDIT EXTRACTION (RenameTool pattern; emit NewTextLength only per D-07) ---
            var solutionDir = SolutionPaths.GetSolutionDirectory(baseSolution);
            var edits = new List<ExtractMethodEdit>();
            var changedDocIds = new List<DocumentId>();
            foreach (var projChanges in projectChanges)
            {
                foreach (var docId in projChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
                {
                    changedDocIds.Add(docId);
                    var oldDoc = baseSolution.GetDocument(docId)!;
                    var newDoc = editedSolution.GetDocument(docId)!;
                    var oldText = await oldDoc.GetTextAsync(opCt);
                    var newText = await newDoc.GetTextAsync(opCt);
                    var relPath = Path.GetRelativePath(solutionDir, oldDoc.FilePath!).Replace('\\', '/');
                    foreach (var change in newText.GetTextChanges(oldText))
                    {
                        edits.Add(new ExtractMethodEdit(
                            File: relPath,
                            Span: new Span(change.Span.Start, change.Span.End),
                            NewTextLength: (change.NewText ?? "").Length));
                    }
                }
            }

            // --- POST-EXTRACT DIAGNOSTICS (baseline-subtracted; RenameTool.cs:202-243) ---
            var postExtractDiagnostics = new List<ExtractMethodDiagnostic>();
            var editedProject = editedSolution.GetProject(touchedProjectId)!;
            var editedCompilation = await editedProject.GetCompilationAsync(opCt);
            if (editedCompilation is not null)
            {
                var changedTrees = new HashSet<SyntaxTree>();
                foreach (var docId in changedDocIds)
                {
                    var doc = editedSolution.GetDocument(docId);
                    if (doc?.Project.Id != touchedProjectId) continue;
                    var tree = await doc.GetSyntaxTreeAsync(opCt);
                    if (tree != null) changedTrees.Add(tree);
                }
                foreach (var d in editedCompilation.GetDiagnostics(opCt))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && !changedTrees.Contains(d.Location.SourceTree)) continue;
                    if (baseline.Contains((d.Id, d.GetMessage()))) continue;
                    var lineSpan = d.Location.GetLineSpan();
                    var src = d.Location.SourceSpan;
                    postExtractDiagnostics.Add(new ExtractMethodDiagnostic(
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

            // --- MTIME CAPTURE (AEV pattern lines 300-316) ---
            var fileMtimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var docId in changedDocIds)
            {
                var doc = baseSolution.GetDocument(docId);
                if (doc?.FilePath is null || fileMtimes.ContainsKey(doc.FilePath)) continue;
                fileMtimes[doc.FilePath] = File.Exists(doc.FilePath)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(doc.FilePath), TimeSpan.Zero)
                    : DateTimeOffset.MinValue;
            }

            // --- SNAPSHOT COMPOSITION (AEV Pitfall 3 — release lease BEFORE the snapshot await) ---
            lease.Dispose();
            string snapshotId;
            try
            {
                snapshotId = await workspaceHost.CreateSnapshotAsync(
                    editedSolution,
                    parentSnapshotId: fromSnapshotId,
                    fileMtimes: fileMtimes,
                    opCt);
            }
            catch (InvalidOperationException)
            {
                // Race: workspace closed between read and snapshot mint.
                return Envelope<ExtractMethodResult>.Err(ToolError.WorkspaceNotLoaded());
            }

            // --- RESPONSE ---
            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var errorCount = postExtractDiagnostics.Count(d => d.Severity == "Error");
            logger.LogInformation(
                "extract_method: {EditCount} edit(s) across {FileCount} file(s), {ErrorCount} new error(s) in {ElapsedMs}ms (snapshot {SnapshotId})",
                edits.Count, changedDocIds.Distinct().Count(), errorCount, elapsedMs, snapshotId);

            var data = new ExtractMethodResult(
                SnapshotId: snapshotId,
                Edits: edits,
                OriginalSpan: span,
                PostExtractDiagnostics: postExtractDiagnostics,
                ActionTitle: actionTitle);
            return Envelope<ExtractMethodResult>.Ok(data, elapsedMs);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during extract_method");
            return Envelope<ExtractMethodResult>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Idempotent — happy path already disposes before the snapshot await; this
            // catches early-error returns that exited before that point.
            lease.Dispose();
        }
    }

    // Locate the synthetic method produced by the extract refactoring, for rename.
    // Tries RenameAnnotation first, then a structural base/edited tree-diff. Returns null
    // if the new method cannot be unambiguously identified — the caller then fails loud
    // rather than risk a text-replace that corrupts unrelated symbols (FM-1).
    private static async Task<ISymbol?> LocateSyntheticMethodAsync(
        Solution baseSolution,
        Solution editedSolution,
        SolutionChanges changes,
        CancellationToken ct)
    {
        foreach (var projChanges in changes.GetProjectChanges())
        {
            foreach (var docId in projChanges.GetChangedDocuments(onlyGetDocumentsWithTextChanges: true))
            {
                var editedDoc = editedSolution.GetDocument(docId);
                if (editedDoc is null) continue;
                var editedRoot = await editedDoc.GetSyntaxRootAsync(ct);
                var semanticModel = await editedDoc.GetSemanticModelAsync(ct);
                if (editedRoot is null || semanticModel is null) continue;

                // Locator 1: RenameAnnotation (kind "CodeAction_Rename"), when emitted.
                var annotatedNode = editedRoot.GetAnnotatedNodes(RenameAnnotation.Kind).FirstOrDefault();
                var annotatedMethod = annotatedNode?.AncestorsAndSelf()
                    .OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (annotatedMethod is not null
                    && semanticModel.GetDeclaredSymbol(annotatedMethod, ct) is { } annotatedSym)
                {
                    return annotatedSym;
                }

                // Locator 2: structural tree-diff — the net-new method declaration. The
                // extracted method is the MethodDeclarationSyntax present in the edited tree
                // whose (type.name/arity) key is absent from the base tree. The edited
                // original method keeps its signature, so it is NOT counted as new.
                var baseDoc = baseSolution.GetDocument(docId);
                if (baseDoc is null) continue; // extract never adds files; base doc exists.
                var baseRoot = await baseDoc.GetSyntaxRootAsync(ct);
                if (baseRoot is null) continue;

                var baseSigs = baseRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Select(MethodSignatureKey)
                    .ToHashSet(StringComparer.Ordinal);
                var newMethods = editedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>()
                    .Where(m => !baseSigs.Contains(MethodSignatureKey(m)))
                    .ToList();
                if (newMethods.Count == 1
                    && semanticModel.GetDeclaredSymbol(newMethods[0], ct) is { } newSym)
                {
                    return newSym;
                }
                // 0 or >1 net-new methods → ambiguous; fall through, caller fails loud.
            }
        }
        return null;
    }

    // Structural identity for a method declaration within its file: containing type +
    // method name + parameter count. Distinguishes the uniquified synthetic extract method
    // (e.g. NewMethod1) from pre-existing members across a base/edited tree diff.
    private static string MethodSignatureKey(MethodDeclarationSyntax m)
    {
        var typeName = (m.Parent as TypeDeclarationSyntax)?.Identifier.Text ?? "<global>";
        return $"{typeName}.{m.Identifier.Text}/{m.ParameterList.Parameters.Count}";
    }
}
