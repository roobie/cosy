using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Search;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for find_references — lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6; not duplicated here.
public sealed record FindReferencesToolData(
    [property: JsonPropertyName("items")] IReadOnlyList<FindReferencesItem> Items);

// Item carries canonical Span + 1-based line/column/end_line/end_column per D-05.
public sealed record FindReferencesItem(
    [property: JsonPropertyName("file")]                 string File,
    [property: JsonPropertyName("span")]                 Span Span,
    [property: JsonPropertyName("line")]                 int Line,
    [property: JsonPropertyName("column")]               int Column,
    [property: JsonPropertyName("end_line")]             int EndLine,
    [property: JsonPropertyName("end_column")]           int EndColumn,
    // The MCP SDK's McpJsonUtilities.CreateDefaultOptions sets a serializer-wide
    // DefaultIgnoreCondition = WhenWritingNull, which would otherwise omit this key entirely
    // for a reference with no enclosing symbol. JsonIgnore(Condition = Never) overrides that
    // default per-property so the key is always emitted, carrying explicit JSON null when
    // unresolved -- kept byte-identical to FindTextItem's identical override (D-05; plan
    // 10.1-01 deviation: this file was widened to keep the two tools' wire behavior aligned).
    [property: JsonPropertyName("containing_symbol_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? ContainingSymbolId,
    [property: JsonPropertyName("kind")]                 string Kind);

/// <summary>
/// find_references -- dry-read semantic reference lookup. Resolves a DocID or fuzzy
/// name via <see cref="FuzzySymbolResolver"/>, then flattens Roslyn reference results
/// (SymbolFinder) to the D-08 item shape. Caps at <c>max ?? 500</c> items (D-09) and
/// surfaces <c>truncated</c>/<c>total_count</c> (envelope) for caller visibility.
/// </summary>
[McpServerToolType]
public sealed class FindReferencesTool
{
    [McpServerTool(Name = "find_references")]
    [Description("Find every reference to a symbol across the loaded solution. " +
        "Accepts an exact DocumentationCommentId or a partial/fuzzy name; multi-match returns " +
        "is_error:true with candidates. Returns flat items[] with (file, span, line, column, " +
        "containing_symbol_id, kind). kind is one of {reference, candidate, declaration, " +
        "definition, cref} (D-18): 'definition' = primary declaration site, 'declaration' = " +
        "additional partial declarations, 'cref' = XML-doc <see cref=...> reference, " +
        "'candidate' = compiler candidate-bind, 'reference' = ordinary use. " +
        "Capped at max items (default 500); envelope carries truncated and total_count. " +
        "Requires workspace_open first.")]
    public async Task<object> FindAsync(
        [Description("Symbol to locate — DocumentationCommentId (preferred, e.g. M:Ns.Type.Method(System.String)) " +
            "or a partial name that the fuzzy resolver can route.")] string symbol,
        IWorkspaceHost workspaceHost,
        FuzzySymbolResolver resolver,
        ILogger<FindReferencesTool> logger,
        [Description("Max items to return (default 500). Lower to bound response size; higher to raise the cap.")] int? max = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<FindReferencesToolData>.Err(ToolError.WorkspaceNotLoaded());

        // TC-07 (D-13): validate max range uniformly across all list-returning tools.
        if (max is not null && (max < 1 || max > 10000))
            return Envelope<FindReferencesToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        logger.LogDebug("find_references: symbol={Symbol}, max={Max}, timeoutMs={TimeoutMs}", symbol, max, timeoutMs);

        var sw = Stopwatch.StartNew();

        // D-15: compose caller CT with optional timeout via linked CTS.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
        {
            if (ms < 1 || ms > 600_000)
                return Envelope<FindReferencesToolData>.Err(
                    ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: ms));
            linkedCts.CancelAfter(ms);
        }
        var effectiveCt = linkedCts.Token;

        try
        {
            // --- SYMBOL RESOLUTION (D-01, D-04, D-05, D-06) ---
            var resolved = await resolver.ResolveAsync(symbol, solution, effectiveCt);
            ISymbol targetSymbol;
            switch (resolved)
            {
                case ResolveResult.ExactMatch m:
                    targetSymbol = m.Symbol;
                    break;
                case ResolveResult.AmbiguousMatches a:
                    logger.LogWarning("find_references: ambiguous symbol: {Symbol} ({Count} candidates)", symbol, a.TopN.Count);
                    // D-11: structured ambiguous error with candidate DTOs.
                    return Envelope<FindReferencesToolData>.Err(
                        ToolError.AmbiguousSymbol(query: symbol, candidates: a.TopN.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                case ResolveResult.NoMatch n:
                    logger.LogWarning("find_references: symbol not found: {Symbol}", n.Query);
                    // D-11: structured not-found error with near-miss DTOs (may be empty).
                    return Envelope<FindReferencesToolData>.Err(
                        ToolError.SymbolNotFound(query: n.Query, nearMisses: n.NearMisses.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                default:
                    return Envelope<FindReferencesToolData>.Err(ToolError.Internal(new Exception("internal: unhandled ResolveResult")));
            }

            // --- REFERENCE LOOKUP (D-08) ---
            var cap = max ?? 500; // D-09 default.
            var solutionDir = SolutionPaths.GetSolutionDirectory(solution);

            var refs = await SymbolFinder.FindReferencesAsync(targetSymbol, solution, effectiveCt);

            var items = new List<FindReferencesItem>();
            var total = 0;

            // D-18 kind taxonomy precedence: cref > candidate > definition > declaration > reference.
            // cref/candidate derive from the location itself (orthogonal to decl/ref). "definition"
            // marks the primary (first) declaration site of the symbol; additional partial
            // declarations are "declaration". A cref-hosted candidate emits "cref" (more precise).
            foreach (var rs in refs)
            {
                // Emit the symbol's own declaration locations as their own rows. SymbolFinder
                // does not include rs.Definition.Locations in rs.Locations — those are the
                // *definition sites*, not reference sites, and D-18 requires them as a distinct
                // kind so agents can navigate declaration vs. use uniformly.
                var defLocs = rs.Definition.Locations.Where(l => l.IsInSource).ToList();
                for (int i = 0; i < defLocs.Count; i++)
                {
                    var defLoc = defLocs[i];
                    total++;
                    if (items.Count >= cap) continue;

                    var defDoc = solution.GetDocument(defLoc.SourceTree);
                    var defDocPath = defDoc?.FilePath;
                    if (defDocPath is null) continue;

                    var defSpan = defLoc.GetLineSpan();
                    var defSm = defDoc is null ? null : await defDoc.GetSemanticModelAsync(effectiveCt);
                    var defContaining = defSm?.GetEnclosingSymbol(defLoc.SourceSpan.Start, effectiveCt);

                    // First decl is "definition" (primary), subsequent are "declaration" (partials).
                    // Single-declaration case → "definition".
                    var defKind = i == 0 ? "definition" : "declaration";

                    items.Add(new FindReferencesItem(
                        File: Path.GetRelativePath(solutionDir, defDocPath).Replace('\\', '/'),
                        Span: new Span(defLoc.SourceSpan.Start, defLoc.SourceSpan.End),
                        Line: defSpan.StartLinePosition.Line + 1,
                        Column: defSpan.StartLinePosition.Character + 1,
                        EndLine: defSpan.EndLinePosition.Line + 1,
                        EndColumn: defSpan.EndLinePosition.Character + 1,
                        ContainingSymbolId: defContaining?.GetDocumentationCommentId(),
                        Kind: defKind));
                }

                foreach (var loc in rs.Locations)
                {
                    total++;
                    if (items.Count >= cap) continue;

                    var docPath = loc.Document.FilePath;
                    if (docPath is null) continue;

                    var span = loc.Location.GetLineSpan();
                    var semanticModel = await loc.Document.GetSemanticModelAsync(effectiveCt);
                    var containing = semanticModel?.GetEnclosingSymbol(loc.Location.SourceSpan.Start, effectiveCt);

                    // cref detection: if the reference token sits inside an XML-doc <cref>
                    // subtree, Roslyn records it as a normal reference but semantically it's
                    // a documentation link — agents need to distinguish it from real code use.
                    // findInsideTrivia is required because cref lives in doc-comment trivia.
                    bool inCref = false;
                    var tree = loc.Location.SourceTree;
                    if (tree is not null)
                    {
                        var root = await tree.GetRootAsync(effectiveCt);
                        var node = root.FindNode(loc.Location.SourceSpan,
                            findInsideTrivia: true, getInnermostNodeForTie: true);
                        inCref = node.AncestorsAndSelf().Any(n => n is CrefSyntax);
                    }

                    var kind = inCref
                        ? "cref"
                        : (loc.IsCandidateLocation ? "candidate" : "reference");

                    items.Add(new FindReferencesItem(
                        File: Path.GetRelativePath(solutionDir, docPath).Replace('\\', '/'),
                        Span: new Span(loc.Location.SourceSpan.Start, loc.Location.SourceSpan.End),
                        Line: span.StartLinePosition.Line + 1,
                        Column: span.StartLinePosition.Character + 1,
                        EndLine: span.EndLinePosition.Line + 1,
                        EndColumn: span.EndLinePosition.Character + 1,
                        ContainingSymbolId: containing?.GetDocumentationCommentId(),
                        Kind: kind));
                }
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            var summary = $"find_references: {items.Count} item(s), truncated={truncated} in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            var data = new FindReferencesToolData(items);
            return Envelope<FindReferencesToolData>.Ok(
                data,
                elapsedMs,
                resolvedSymbol: new ResolvedSymbol(
                    targetSymbol.GetDocumentationCommentId(),
                    targetSymbol.ToDisplayString()),
                truncated: truncated,
                totalCount: total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during find_references");
            return Envelope<FindReferencesToolData>.Err(ToolError.Internal(ex));
        }
    }
}
