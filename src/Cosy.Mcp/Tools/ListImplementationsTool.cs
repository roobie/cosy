using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Search;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for list_implementations — lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6.
public sealed record ListImplementationsToolData(
    [property: JsonPropertyName("items")] IReadOnlyList<ListImplementationsItem> Items);

// Item carries canonical Span + 1-based line/column/end_line/end_column per D-05.
// For implementations without in-source locations, line/column fields fall back to 0.
public sealed record ListImplementationsItem(
    [property: JsonPropertyName("file")]                 string? File,
    [property: JsonPropertyName("span")]                 Span Span,
    [property: JsonPropertyName("line")]                 int Line,
    [property: JsonPropertyName("column")]               int Column,
    [property: JsonPropertyName("end_line")]             int EndLine,
    [property: JsonPropertyName("end_column")]           int EndColumn,
    [property: JsonPropertyName("containing_symbol_id")] string? ContainingSymbolId,
    [property: JsonPropertyName("kind")]                 string Kind,
    [property: JsonPropertyName("doc_id")]               string? DocId,
    [property: JsonPropertyName("display_name")]         string DisplayName);

/// <summary>
/// list_implementations -- dry-read discovery of implementing types/members for an interface,
/// abstract class, or polymorphic member. Caps at <c>max ?? 500</c> (D-09) and surfaces
/// truncated/total_count on the envelope.
/// </summary>
[McpServerToolType]
public sealed class ListImplementationsTool
{
    [McpServerTool(Name = "list_implementations")]
    [Description("List implementing types/members for an interface, abstract class, or virtual/abstract " +
        "member across the loaded solution. Accepts an exact DocumentationCommentId or a partial/fuzzy " +
        "name; multi-match returns is_error:true with candidates. Returns flat items[] with " +
        "(file, span, line, column, containing_symbol_id, kind) where kind is 'type' or 'member'. " +
        "Capped at max items (default 500); envelope carries truncated and total_count. " +
        "Requires workspace_open first.")]
    public async Task<object> ListAsync(
        [Description("Symbol to locate — DocumentationCommentId (preferred, e.g. T:Ns.IFoo or " +
            "M:Ns.IFoo.Bar(System.String)) or a partial name the fuzzy resolver can route.")] string symbol,
        IWorkspaceHost workspaceHost,
        FuzzySymbolResolver resolver,
        ILogger<ListImplementationsTool> logger,
        [Description("Max items to return (default 500).")] int? max = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<ListImplementationsToolData>.Err(ToolError.WorkspaceNotLoaded());

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<ListImplementationsToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        logger.LogDebug("list_implementations: symbol={Symbol}, max={Max}, timeoutMs={TimeoutMs}", symbol, max, timeoutMs);

        var sw = Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
        {
            if (ms < 1 || ms > 600_000)
                return Envelope<ListImplementationsToolData>.Err(
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
                    logger.LogWarning("list_implementations: ambiguous symbol: {Symbol} ({Count} candidates)", symbol, a.TopN.Count);
                    // D-11: structured ambiguous error with candidate DTOs.
                    return Envelope<ListImplementationsToolData>.Err(
                        ToolError.AmbiguousSymbol(query: symbol, candidates: a.TopN.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                case ResolveResult.NoMatch n:
                    logger.LogWarning("list_implementations: symbol not found: {Symbol}", n.Query);
                    // D-11: structured not-found error with near-miss DTOs (may be empty).
                    return Envelope<ListImplementationsToolData>.Err(
                        ToolError.SymbolNotFound(query: n.Query, nearMisses: n.NearMisses.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                default:
                    return Envelope<ListImplementationsToolData>.Err(ToolError.Internal(new Exception("internal: unhandled ResolveResult")));
            }

            var cap = max ?? 500;
            var solutionDir = SolutionPaths.GetSolutionDirectory(solution);

            // --- KIND DISPATCH (RESEARCH §Pattern 3) ---
            IEnumerable<ISymbol> impls = targetSymbol switch
            {
                INamedTypeSymbol { TypeKind: TypeKind.Interface } iface
                    => (await SymbolFinder.FindImplementationsAsync(iface, solution, transitive: true,
                            projects: null, effectiveCt)).Cast<ISymbol>(),
                INamedTypeSymbol { IsAbstract: true } abs
                    => (await SymbolFinder.FindDerivedClassesAsync(abs, solution, transitive: true,
                            projects: null, effectiveCt)).Cast<ISymbol>(),
                INamedTypeSymbol cls
                    => (await SymbolFinder.FindDerivedClassesAsync(cls, solution, transitive: true,
                            projects: null, effectiveCt)).Cast<ISymbol>(),
                ISymbol member when member.ContainingType?.TypeKind == TypeKind.Interface || member.IsAbstract
                    => await SymbolFinder.FindImplementationsAsync(member, solution, projects: null, effectiveCt),
                _ => Array.Empty<ISymbol>(),
            };

            var items = new List<ListImplementationsItem>();
            var total = 0;

            foreach (var sym in impls)
            {
                total++;
                if (items.Count >= cap) continue;

                var loc = sym.Locations.FirstOrDefault(l => l.IsInSource);
                string? file = null;
                int line = 0, column = 0, endLine = 0, endColumn = 0, spanStart = 0, spanEnd = 0;
                if (loc is not null)
                {
                    var span = loc.GetLineSpan();
                    if (span.Path is not null)
                        file = Path.GetRelativePath(solutionDir, span.Path).Replace('\\', '/');
                    line = span.StartLinePosition.Line + 1;
                    column = span.StartLinePosition.Character + 1;
                    endLine = span.EndLinePosition.Line + 1;
                    endColumn = span.EndLinePosition.Character + 1;
                    spanStart = loc.SourceSpan.Start;
                    spanEnd = loc.SourceSpan.End;
                }

                items.Add(new ListImplementationsItem(
                    File: file,
                    Span: new Span(spanStart, spanEnd),
                    Line: line,
                    Column: column,
                    EndLine: endLine,
                    EndColumn: endColumn,
                    ContainingSymbolId: sym.ContainingSymbol?.GetDocumentationCommentId(),
                    Kind: sym is INamedTypeSymbol ? "type" : "member",
                    DocId: sym.GetDocumentationCommentId(),
                    DisplayName: sym.ToDisplayString()));
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            var summary = $"list_implementations: {items.Count} item(s), truncated={truncated} in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            return Envelope<ListImplementationsToolData>.Ok(
                new ListImplementationsToolData(items),
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
            logger.LogError(ex, "Roslyn exception during list_implementations");
            return Envelope<ListImplementationsToolData>.Err(ToolError.Internal(ex));
        }
    }
}
