using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Search;
using Cosy.Mcp.Source;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for read_source -- lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6 (item universe); truncated_text lives on the
// envelope per the Phase 12 D-12 amendment (character universe). read_snapshot_id echoes which
// base answered (D-08): the supplied fromSnapshotId when a staged snapshot was read, explicit
// JSON null when the current solution was.
public sealed record ReadSourceToolData(
    [property: JsonPropertyName("items")] IReadOnlyList<ReadSourceItem> Items,
    // Explicit null (not omitted) when fromSnapshotId was not supplied -- D-08 requires the
    // caller to be able to distinguish "current solution answered" from "field absent", which
    // JsonIgnoreCondition.Never forces the same way FindTextItem.ContainingSymbolId does
    // (FindTextTool.cs:40).
    [property: JsonPropertyName("read_snapshot_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? ReadSnapshotId);

// Item carries the canonical Span + 1-based line/column/end_line/end_column (ADR-0004 §3) and the
// declaration's text (D-01 -- FullSpan, so XML doc comments/attributes/modifiers come with it),
// possibly cut back to a syntax boundary by the maxChars budget (D-11). When it was cut,
// text_truncated is true and snap_suggest names the construct that begins the remainder (D-13).
//
// snap_suggest is left on the SDK-wide WhenWritingNull default: when null the key is OMITTED from
// the wire, not serialised as an explicit null. Only members carrying JsonIgnore(Never) -- here
// just read_snapshot_id, per D-08 -- appear as explicit nulls. Whether snap_suggest ought to be
// explicit-null for symmetry is an open contract question left to 12-06's ADR-0013; assertions
// must read "absent, or present and null".
public sealed record ReadSourceItem(
    [property: JsonPropertyName("file")]           string? File,
    [property: JsonPropertyName("span")]           Span Span,
    [property: JsonPropertyName("line")]           int Line,
    [property: JsonPropertyName("column")]         int Column,
    [property: JsonPropertyName("end_line")]       int EndLine,
    [property: JsonPropertyName("end_column")]     int EndColumn,
    [property: JsonPropertyName("text")]           string Text,
    [property: JsonPropertyName("text_truncated")] bool TextTruncated,
    [property: JsonPropertyName("snap_suggest")]   SnapSuggest? SnapSuggest);

/// <summary>
/// read_source -- returns a resolved symbol's declaration text (Roslyn FullSpan) in the same
/// character-offset coordinate system apply_edits_verified consumes (SC-2). Symbol-keyed, never
/// a file path (D-07, the inner-platform guard). This plan handles the single-declaration-site
/// case only -- the partial-method union (D-02) is 12-02's job.
/// </summary>
[McpServerToolType]
public sealed class ReadSourceTool
{
    // Public (not private): 12-02's both-directions fact calls this directly on the two linked
    // partial-member symbols, because the public tool surface cannot address either half
    // separately (both share one DocumentationCommentId -- FuzzySymbolResolver collapses any
    // query to the same starting symbol). A future tidy-up must not re-privatise this.
    // Public static does NOT widen the MCP catalog: .WithToolsFromAssembly() only discovers
    // [McpServerTool]-attributed methods, and this one carries no such attribute.
    //
    // Roslyn does not expose PartialDefinitionPart/PartialImplementationPart on ISymbol itself --
    // IMethodSymbol, IPropertySymbol and IEventSymbol each declare the pair independently with no
    // shared interface (12-RESEARCH.md Pattern 1, live-verified against Roslyn 5.3.0). Whichever
    // half FuzzySymbolResolver returns exposes only its OWN DeclaringSyntaxReferences, so the
    // union below is required to honour D-02's all-sites contract for a partial method/property/
    // event. INamedTypeSymbol (partial class) has no partner symbol at all -- it falls through the
    // switch to the no-partner case and its own DeclaringSyntaxReferences (one entry per file) is
    // already complete.
    public static IEnumerable<SyntaxReference> AllDeclaringSyntaxReferences(ISymbol sym)
    {
        var (defPart, implPart) = sym switch
        {
            IMethodSymbol m   => ((ISymbol?)m.PartialDefinitionPart, (ISymbol?)m.PartialImplementationPart),
            IPropertySymbol p => ((ISymbol?)p.PartialDefinitionPart, (ISymbol?)p.PartialImplementationPart),
            IEventSymbol e    => ((ISymbol?)e.PartialDefinitionPart, (ISymbol?)e.PartialImplementationPart),
            _                 => (null, null),
        };

        var all = sym.DeclaringSyntaxReferences.AsEnumerable();
        if (defPart is not null)  all = all.Concat(defPart.DeclaringSyntaxReferences);
        if (implPart is not null) all = all.Concat(implPart.DeclaringSyntaxReferences);

        // Dedupe by (file, span) -- mandatory, not defensive tidiness: a defining and
        // implementing pair CAN be declared in the same file, and without this a site collected
        // from both the resolved symbol's own references and a partner's would double-emit.
        return all
            .GroupBy(r => (r.SyntaxTree.FilePath, r.Span.Start, r.Span.End))
            .Select(g => g.First());
    }

    [McpServerTool(Name = "read_source")]
    [Description("Return the resolved symbol's declaration source text -- the Roslyn FullSpan " +
        "(includes XML doc comments, attributes, and the modifiers line), keyed on a fuzzy-resolved " +
        "symbol, never a file path. Accepts an exact DocumentationCommentId (preferred, e.g. " +
        "M:Ns.Type.Method) or a fuzzy name; multi-match returns is_error:true with candidates. " +
        "Returns flat items[] -- one per declaration site -- with (file, span, line, column, " +
        "end_line, end_column, text, text_truncated, snap_suggest). span offsets are the same " +
        "character-offset coordinate system apply_edits_verified consumes. Each item's text is " +
        "capped at maxChars characters (default 8000), snapped back to a syntax boundary; a cut " +
        "item carries text_truncated:true and snap_suggest {start, end, kind, display_name} " +
        "naming the construct that begins the remainder, and the envelope's truncated_text is " +
        "true when any item was cut. Capped at max items " +
        "(default 500); envelope carries truncated and total_count for the item universe. When " +
        "fromSnapshotId is supplied, the read resolves and reads against that staged snapshot " +
        "instead of the current workspace state; data.read_snapshot_id echoes which base answered " +
        "(the supplied id, or explicit null for the current state). Requires workspace_open first.")]
    public async Task<object> GetAsync(
        [Description("Symbol to read -- DocumentationCommentId (preferred, e.g. M:Ns.Type.Method) " +
            "or a partial name that the fuzzy resolver can route to a symbol.")] string symbol,
        IWorkspaceHost workspaceHost,
        FuzzySymbolResolver resolver,
        ILogger<ReadSourceTool> logger,
        [Description("Max declaration sites to return (default 500). Lower to bound response size; higher to raise the cap.")] int? max = null,
        [Description("Max characters of text per declaration site (default 8000, range 500..200000). " +
            "A real ceiling: the returned text is never longer, and the cut is snapped back to a " +
            "statement/member boundary, then a line break, then a hard cut. Each site gets its own " +
            "full budget.")] int? maxChars = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        [Description("Optional snapshot id to read on top of (ADR-0007 §1.2), instead of the current workspace state. Defaults to the current solution when absent.")] string? fromSnapshotId = null,
        CancellationToken ct = default)
    {
        // --- ARGUMENT VALIDATION (cheap first, before any workspace access) ---
        if (max is not null && (max < 1 || max > 10000))
            return Envelope<ReadSourceToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        // D-14: per-item character budget, range [500, 200000]. Checked here, before the solution
        // is rented, alongside `max` -- together they bound the worst-case response at
        // max x maxChars. The WIRE parameter is camelCase `maxChars` (ADR-0004 §2's snake_case
        // mandate binds response keys only); the `param` STRING below is response-side and is
        // therefore snake_case `max_chars`. Two different surfaces -- conflating them is a
        // documented pitfall.
        if (maxChars is not null && (maxChars < 500 || maxChars > 200_000))
            return Envelope<ReadSourceToolData>.Err(
                ToolError.InvalidArgument("max_chars", "out_of_range_500_to_200000", value: maxChars));

        // Validated HERE, with the other cheap checks, and not down at the CancelAfter call that
        // consumes it: an early return from below the RentSolution line leaks the read lease,
        // because the `finally` that disposes it does not begin until the `try`. A leaked lease
        // wedges workspace_close for the life of the process (ADR-0005 §D-06), so ordinary bad
        // input must never reach that window. ReadSourceSpanTool.cs:105-111 has always ordered it
        // this way; this file diverged.
        if (timeoutMs is not null && (timeoutMs < 1 || timeoutMs > 600_000))
            return Envelope<ReadSourceToolData>.Err(
                ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: timeoutMs));

        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Not a `using` because the snapshot-not-found
        // branch below must dispose it before an early return (FindTextTool.cs:142-166 mirror);
        // the `finally` at the bottom covers every other exit path.
        var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
        {
            lease.Dispose();
            return Envelope<ReadSourceToolData>.Err(ToolError.WorkspaceNotLoaded());
        }

        // ADR-0007 §1.2 / D-08: if fromSnapshotId is supplied, the read base is that snapshot's
        // Solution rather than CurrentSolution -- symbol resolution and text both come from it.
        var baseSolution = solution;
        if (fromSnapshotId is not null)
        {
            if (!workspaceHost.TryGetSnapshot(fromSnapshotId, out var snapshotEntry) || snapshotEntry is null)
            {
                lease.Dispose();
                return Envelope<ReadSourceToolData>.Err(ToolError.SnapshotNotFound(fromSnapshotId));
            }
            baseSolution = snapshotEntry.Snapshot;
        }

        logger.LogDebug("read_source: symbol={Symbol}, max={Max}, timeoutMs={TimeoutMs}, fromSnapshotId={FromSnapshotId}",
            symbol, max, timeoutMs, fromSnapshotId);

        var sw = Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms) linkedCts.CancelAfter(ms); // range already validated above
        var effectiveCt = linkedCts.Token;

        try
        {
            // --- SYMBOL RESOLUTION against the BASE solution (D-08, SC-3) ---
            var resolved = await resolver.ResolveAsync(symbol, baseSolution, effectiveCt);
            ISymbol targetSymbol;
            switch (resolved)
            {
                case ResolveResult.ExactMatch m:
                    targetSymbol = m.Symbol;
                    break;
                case ResolveResult.AmbiguousMatches a:
                    logger.LogWarning("read_source: ambiguous symbol: {Symbol} ({Count} candidates)", symbol, a.TopN.Count);
                    return Envelope<ReadSourceToolData>.Err(
                        ToolError.AmbiguousSymbol(query: symbol, candidates: a.TopN.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                case ResolveResult.NoMatch n:
                    logger.LogWarning("read_source: symbol not found: {Symbol}", n.Query);
                    return Envelope<ReadSourceToolData>.Err(
                        ToolError.SymbolNotFound(query: n.Query, nearMisses: n.NearMisses.ToDtos()),
                        (int)sw.ElapsedMilliseconds);
                default:
                    return Envelope<ReadSourceToolData>.Err(ToolError.Internal(new Exception("internal: unhandled ResolveResult")));
            }

            // --- NO-SOURCE GUARDS (D-03, D-04) -- AFTER resolution, BEFORE site collection ---
            // Both are post-resolution checks on the symbol the resolver already returned; neither
            // touches FuzzySymbolResolver (D-04's guard is explicitly NOT a resolver-side filter --
            // ResolveAsync stays byte-identical for the three other tools that share the singleton).
            var symbolId = targetSymbol.GetDocumentationCommentId() ?? symbol;

            // D-04: a synthesized symbol (e.g. a compiler-generated backing member) has no real
            // declaration to read, even though it resolved. Guarding on IsImplicitlyDeclared here
            // (not in the resolver) keeps this specific to read_source's contract.
            if (targetSymbol.IsImplicitlyDeclared)
            {
                logger.LogWarning("read_source: no source available (implicitly declared): {Symbol}", symbol);
                return Envelope<ReadSourceToolData>.Err(
                    ToolError.NoSourceAvailable(symbolId, targetSymbol.ContainingAssembly?.Name),
                    (int)sw.ElapsedMilliseconds);
            }

            // --- DECLARATION SITES (D-01, D-02) ---
            // Materialize (node, FullSpan, file path) per reference up front so both the sort key
            // (D-20: file path, then span start) and the later text/line-mapping work read from
            // the same already-fetched node -- GetSyntax is synchronous and cheap once the tree
            // is loaded, which it is here since the symbol just resolved against this solution.
            // AllDeclaringSyntaxReferences unions in a partial method/property/event's other half
            // (D-02) -- a no-op for every symbol kind that doesn't have one, including a partial
            // class, whose own DeclaringSyntaxReferences is already the complete site list.
            var allRefs = AllDeclaringSyntaxReferences(targetSymbol).ToList();

            // D-03: the resolve succeeded but the union yielded zero syntax references (e.g. a
            // type from a referenced assembly, metadata-only). Deliberately distinct from
            // symbol_not_found -- an agent branching on error.kind needs to tell "your query was
            // wrong" apart from "the source is not here".
            if (allRefs.Count == 0)
            {
                logger.LogWarning("read_source: no source available (zero syntax references): {Symbol}", symbol);
                return Envelope<ReadSourceToolData>.Err(
                    ToolError.NoSourceAvailable(symbolId, targetSymbol.ContainingAssembly?.Name),
                    (int)sw.ElapsedMilliseconds);
            }

            var sites = allRefs
                .Select(r => r.GetSyntax(effectiveCt))
                .Select(node => (Node: node, FullSpan: node.FullSpan, FilePath: node.SyntaxTree.FilePath))
                .OrderBy(x => x.FilePath, StringComparer.Ordinal)
                .ThenBy(x => x.FullSpan.Start)
                .ToList();

            var total = sites.Count;
            var cap = max ?? 500;
            var charBudget = maxChars ?? 8000;   // D-14's default
            var solutionDir = SolutionPaths.GetSolutionDirectory(baseSolution);

            var items = new List<ReadSourceItem>();
            var anyTextTruncated = false;
            foreach (var site in sites.Take(cap))
            {
                // FullSpan (not the narrow Span) so leading trivia -- XML doc comments,
                // attributes, the modifiers line -- comes with the declaration text (D-01).
                var sourceText = site.Node.SyntaxTree.GetText(effectiveCt);

                // D-11 + assumption A1: the budget is measured from FullSpan.Start, so emitted
                // leading trivia counts against it. Measuring from the narrow Span.Start would let
                // an XML-doc-heavy declaration silently exceed the ceiling.
                //
                // D-14: each site gets its OWN full budget -- no remaining-budget accumulator
                // across sites, which is what makes truncation position-independent.
                var snap = BoundarySnap.Snap(
                    scope: site.Node,
                    text: sourceText,
                    start: site.FullSpan.Start,
                    maxChars: charBudget,
                    extentEnd: site.FullSpan.End,
                    ct: effectiveCt);

                // The item's span reports the SNAPPED end, so span and text keep agreeing even
                // when the text was cut -- that agreement is the SC-2 invariant 12-01 established
                // and it must survive truncation. line/column are recomputed from it too.
                var emitted = TextSpan.FromBounds(site.FullSpan.Start, snap.End);
                var text = sourceText.ToString(emitted);
                var startPos = sourceText.Lines.GetLinePosition(emitted.Start);
                var endPos = sourceText.Lines.GetLinePosition(emitted.End);

                var relFile = site.FilePath is not null
                    ? Path.GetRelativePath(solutionDir, site.FilePath).Replace('\\', '/')
                    : null;

                anyTextTruncated |= snap.Truncated;

                items.Add(new ReadSourceItem(
                    File: relFile,
                    Span: new Span(emitted.Start, emitted.End),
                    Line: startPos.Line + 1,
                    Column: startPos.Character + 1,
                    EndLine: endPos.Line + 1,
                    EndColumn: endPos.Character + 1,
                    Text: text,
                    TextTruncated: snap.Truncated,
                    SnapSuggest: snap.Suggest));
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            logger.LogInformation("read_source: {Count} item(s), truncated={Truncated}, truncatedText={TruncatedText} in {ElapsedMs}ms",
                items.Count, truncated, anyTextTruncated, elapsedMs);

            return Envelope<ReadSourceToolData>.Ok(
                new ReadSourceToolData(items, fromSnapshotId),
                elapsedMs,
                resolvedSymbol: new ResolvedSymbol(
                    targetSymbol.GetDocumentationCommentId(),
                    targetSymbol.ToDisplayString()),
                truncated: truncated,
                totalCount: total,
                // D-12: "present iff the tool returns text" -- read_source always returns text, so
                // this is always a bool, never `null` (null would suppress the key via
                // WhenWritingNull and violate the always-present rule). True iff ANY item was cut,
                // and deliberately independent of `truncated` above: the item universe and the
                // character universe answer different questions and can differ in all four
                // combinations.
                truncatedText: anyTextTruncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during read_source");
            return Envelope<ReadSourceToolData>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Idempotent -- the not-loaded and snapshot-not-found branches above already
            // disposed the lease before returning; this catches every other exit path.
            lease.Dispose();
        }
    }
}
