using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Search;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for get_members — lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6.
public sealed record GetMembersToolData(
    [property: JsonPropertyName("items")] IReadOnlyList<GetMembersItem> Items);

// Item carries canonical Span + 1-based line/column/end_line/end_column per D-05.
public sealed record GetMembersItem(
    [property: JsonPropertyName("file")]                 string? File,
    [property: JsonPropertyName("span")]                 Span Span,
    [property: JsonPropertyName("line")]                 int Line,
    [property: JsonPropertyName("column")]               int Column,
    [property: JsonPropertyName("end_line")]             int EndLine,
    [property: JsonPropertyName("end_column")]           int EndColumn,
    [property: JsonPropertyName("containing_symbol_id")] string? ContainingSymbolId,
    [property: JsonPropertyName("doc_id")]               string? DocId,
    [property: JsonPropertyName("kind")]                 string Kind,
    [property: JsonPropertyName("accessibility")]        string Accessibility);

/// <summary>
/// get_members -- dry-read enumeration of a type's declared members. Synthesized
/// members are filtered (D-16). Partials are merged by Roslyn.
/// </summary>
[McpServerToolType]
public sealed class GetMembersTool
{
    [McpServerTool(Name = "get_members")]
    [Description("List the declared members of a named type (class, struct, interface, enum, record). " +
        "Accepts an exact DocumentationCommentId (preferred, e.g. T:Ns.Type) or a fuzzy name; multi-match " +
        "returns is_error:true with candidates. Non-type inputs return is_error:true with 'symbol is not a type'. " +
        "Returns flat items[] with (file, span, line, column, containing_symbol_id, doc_id, kind, " +
        "accessibility). Compiler-synthesized members (e.g. backing fields) are excluded. Capped at max " +
        "items (default 500); envelope carries truncated and total_count. Requires workspace_open first.")]
    public async Task<object> GetAsync(
        IWorkspaceHost workspaceHost,
        FuzzySymbolResolver resolver,
        ILogger<GetMembersTool> logger,
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal.
        [CosyRequired]
        [Description("Type symbol to enumerate — DocumentationCommentId (preferred, e.g. T:Ns.Type) " +
            "or a partial name that the fuzzy resolver can route to a named type.")] string? symbol = null,
        [Description("Max items to return (default 500). Lower to bound response size; higher to raise the cap.")] int? max = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // ArgumentGuard rejects an absent/null symbol (CosyRequired) before this handler ever
        // runs -- this binding is a compiler satisfaction only, not a second requiredness check.
        var symbolText = symbol!;

        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<GetMembersToolData>.Err(ToolError.WorkspaceNotLoaded());

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<GetMembersToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        logger.LogDebug("get_members: symbol={Symbol}, max={Max}, timeoutMs={TimeoutMs}", symbolText, max, timeoutMs);

        var sw = Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
        {
            if (ms < 1 || ms > 600_000)
                return Envelope<GetMembersToolData>.Err(
                    ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: ms));
            linkedCts.CancelAfter(ms);
        }
        var effectiveCt = linkedCts.Token;

        try
        {
            // --- SYMBOL RESOLUTION (D-01, D-04, D-05, D-06) ---
            var resolved = await resolver.ResolveAsync(symbolText, solution, effectiveCt);
            ISymbol targetSymbol;
            switch (resolved)
            {
                case ResolveResult.ExactMatch m:
                    targetSymbol = m.Symbol;
                    break;
                case ResolveResult.AmbiguousMatches a:
                    logger.LogWarning("get_members: ambiguous symbol: {Symbol} ({Count} candidates)", symbolText, a.TopN.Count);
                    // D-11: structured ambiguous error with candidate DTOs.
                    return Envelope<GetMembersToolData>.Err(
                        ToolError.AmbiguousSymbol(query: symbolText, candidates: a.TopN.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                case ResolveResult.NoMatch n:
                    logger.LogWarning("get_members: symbol not found: {Symbol}", n.Query);
                    // D-11: structured not-found error with near-miss DTOs (may be empty).
                    return Envelope<GetMembersToolData>.Err(
                        ToolError.SymbolNotFound(query: n.Query, nearMisses: n.NearMisses.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                default:
                    return Envelope<GetMembersToolData>.Err(ToolError.Internal(new Exception("internal: unhandled ResolveResult")));
            }

            // --- TYPE GUARD (T-06-11) ---
            if (targetSymbol is not INamedTypeSymbol named)
                // TODO(Plan 05): ToolError.InvalidArgument (param=symbol, reason=not_a_type).
                return Envelope<GetMembersToolData>.Err(ToolError.Internal(new Exception("symbol is not a type")));

            // --- MEMBER ENUMERATION (D-16, Pitfall 4, Pitfall 5) ---
            var members = named.GetMembers().Where(mem => !mem.IsImplicitlyDeclared).ToArray();

            var cap = max ?? 500;
            var solutionDir = SolutionPaths.GetSolutionDirectory(solution);

            var items = new List<GetMembersItem>();
            var total = 0;

            foreach (var mem in members)
            {
                total++;
                if (items.Count >= cap) continue;

                var firstSrcLoc = mem.Locations.FirstOrDefault(l => l.IsInSource);
                var lineSpan = firstSrcLoc?.GetLineSpan();
                string? filePath = firstSrcLoc?.SourceTree?.FilePath;
                if (filePath is null && mem.DeclaringSyntaxReferences.Length > 0)
                    filePath = mem.DeclaringSyntaxReferences[0].SyntaxTree.FilePath;

                var relFile = filePath is not null
                    ? Path.GetRelativePath(solutionDir, filePath).Replace('\\', '/')
                    : null;

                items.Add(new GetMembersItem(
                    File: relFile,
                    Span: new Span(
                        firstSrcLoc?.SourceSpan.Start ?? 0,
                        firstSrcLoc?.SourceSpan.End ?? 0),
                    Line: lineSpan is { } ls ? ls.StartLinePosition.Line + 1 : 0,
                    Column: lineSpan is { } ls2 ? ls2.StartLinePosition.Character + 1 : 0,
                    EndLine: lineSpan is { } ls3 ? ls3.EndLinePosition.Line + 1 : 0,
                    EndColumn: lineSpan is { } ls4 ? ls4.EndLinePosition.Character + 1 : 0,
                    ContainingSymbolId: mem.ContainingSymbol?.GetDocumentationCommentId(),
                    DocId: mem.GetDocumentationCommentId(),
                    Kind: MapKind(mem),
                    Accessibility: mem.DeclaredAccessibility.ToString().ToLowerInvariant()));
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            var summary = $"get_members: {items.Count} item(s), truncated={truncated} in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            return Envelope<GetMembersToolData>.Ok(
                new GetMembersToolData(items),
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
            logger.LogError(ex, "Roslyn exception during get_members");
            return Envelope<GetMembersToolData>.Err(ToolError.Internal(ex));
        }
    }

    /// <summary>
    /// Map a Roslyn <see cref="ISymbol"/> to the lowercase kind string in D-08.
    /// </summary>
    private static string MapKind(ISymbol sym) => sym switch
    {
        IMethodSymbol ms when ms.MethodKind == MethodKind.Constructor => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        INamedTypeSymbol => "nested_type",
        _ => sym.Kind.ToString().ToLowerInvariant(),
    };
}
