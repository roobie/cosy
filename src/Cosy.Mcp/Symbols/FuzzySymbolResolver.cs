using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Cosy.Mcp.Symbols;

/// <summary>
/// Resolves an agent-supplied symbol query (exact DocID, FQN, or a partial name)
/// to an <see cref="ISymbol"/> in the current <see cref="Solution"/>.
///
/// Exact DocID fast-path first (zero false positives by construction); on miss,
/// falls through to a fuzzy pass driven by <see cref="SymbolFinder.FindDeclarationsAsync"/>
/// and scored per ADR-0002 (Phase 6 CONTEXT.md §D-02): 1.0 exact, 0.99 case-insensitive,
/// 0.9 params-omitted, threshold 0.7. Scores are discrete — we never interpolate
/// between tiers (keeps reasoning trivial; matches SharpToolsMCP prior art).
///
/// No interface wrapper (RESEARCH.md §Project Constraints): DI registers the
/// concrete class as a singleton.
/// </summary>
public sealed class FuzzySymbolResolver
{
    // DocID prefix sniff: "T:/M:/P:/F:/E:/N:" per Roslyn DocumentationCommentId contract.
    private static readonly char[] DocIdKinds = ['T', 'M', 'P', 'F', 'E', 'N'];

    // Scoring FQN format (Rule 1 fix of RESEARCH Pattern 4):
    // The plan says use FullyQualifiedFormat, but for *members* FullyQualifiedFormat returns
    // just the member name (e.g. "Process") — useless for FQN matching. Extend it with
    // MemberOptions=IncludeContainingType|IncludeParameters + ParameterOptions=IncludeType
    // so that a method symbol renders as "global::Sample.Contracts.IFooService.Process(System.String)"
    // — which is what an agent typing a "fully qualified" method reference would supply.
    private static readonly SymbolDisplayFormat FqnFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMemberOptions(
            SymbolDisplayMemberOptions.IncludeContainingType |
            SymbolDisplayMemberOptions.IncludeParameters)
        .WithParameterOptions(SymbolDisplayParameterOptions.IncludeType);

    public async Task<ResolveResult> ResolveAsync(string query, Solution solution, CancellationToken ct)
    {
        // --- 1. Exact DocID fast path ---
        // If the query looks like a DocID, try each project's compilation. Note we fall
        // THROUGH to fuzzy on miss rather than short-circuiting to NoMatch — a caller may
        // hand us "M:Type.Method" with a typo in the namespace, and a fuzzy near-miss
        // is still useful disambiguation (RESEARCH.md §Resolver skeleton).
        if (LooksLikeDocId(query))
        {
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;
                var sym = DocumentationCommentId.GetFirstSymbolForDeclarationId(query, compilation);
                if (sym is not null)
                    return new ResolveResult.ExactMatch(sym, 1.0, "exact_docid");
            }
            // fall through — no exception, no early NoMatch
        }

        // --- 2. Fuzzy pass ---
        // Extract the simple name for the SymbolFinder index. Strip any "(...)" signature tail
        // FIRST so that the last-'.' split doesn't land inside a parameter-list namespace (e.g.
        // "Foo.Bar(System.String)" — the last '.' is inside the parens). Then take the tail
        // after the last '.'. FindDeclarationsAsync expects a bare identifier (Pitfall 8).
        var simpleName = StripParens(query);
        if (simpleName.Contains('.'))
            simpleName = simpleName[(simpleName.LastIndexOf('.') + 1)..];

        var candidates = new List<SymbolCandidate>();
        foreach (var project in solution.Projects)
        {
            var decls = await SymbolFinder.FindDeclarationsAsync(
                project, simpleName, ignoreCase: true, SymbolFilter.TypeAndMember, ct);
            foreach (var d in decls)
            {
                var score = Score(d, query);
                if (score >= 0.7)
                    candidates.Add(new SymbolCandidate(d, score, ReasonFor(score)));
            }
        }

        // Dedupe by DocID — a symbol can surface from multiple projects that reference
        // the same assembly (esp. relevant for multi-targeted builds, irrelevant at spike
        // scale but cheap to keep). Keep the highest-scoring occurrence.
        var distinct = candidates
            .GroupBy(c => c.Symbol.GetDocumentationCommentId() ?? "<none>")
            .Select(g => g.OrderByDescending(x => x.Score).First())
            .OrderByDescending(c => c.Score)
            .ToList();

        return distinct.Count switch
        {
            0 => new ResolveResult.NoMatch(query, NearMissesOrEmpty(candidates)),
            1 => new ResolveResult.ExactMatch(distinct[0].Symbol, distinct[0].Score, distinct[0].MatchReason),
            _ => new ResolveResult.AmbiguousMatches(distinct.Take(10).ToList()), // D-07
        };
    }

    /// <summary>
    /// Scores a candidate against a query per the ADR-0002 rubric. Discrete tiers only.
    /// </summary>
    private static double Score(ISymbol sym, string query)
    {
        var docId = sym.GetDocumentationCommentId();
        if (docId == query) return 1.0;                                             // exact DocID
        var fqn = sym.ToDisplayString(FqnFormat);
        if (fqn == query) return 1.0;                                               // exact FQN
        if (fqn.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0.99;     // case-insensitive FQN
        var fqnNoParams = StripParens(fqn);
        if (fqnNoParams == query) return 0.9;                                       // params-omitted
        if (fqnNoParams.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0.9;
        return 0.0;
    }

    private static string ReasonFor(double score) => score switch
    {
        1.0 => "exact",
        0.99 => "case_insensitive",
        0.9 => "params_omitted",
        _ => "other",
    };

    // ADR-0004 §8, D-11: near_misses is best-effort and always-present (may be empty).
    // Current scorer only emits candidates with score ≥ 0.7; when ResolveAsync reaches the
    // NoMatch branch, `candidates` is empty by construction (everything below 0.7 was filtered
    // upstream). Return empty so the shape is stable. A richer sub-threshold top-N can land
    // later without breaking callers — the producer contract is already a list.
    private static IReadOnlyList<SymbolCandidate> NearMissesOrEmpty(List<SymbolCandidate> all) =>
        all.Count == 0 ? Array.Empty<SymbolCandidate>() : all.OrderByDescending(c => c.Score).Take(10).ToList();

    private static bool LooksLikeDocId(string q) =>
        q.Length > 2 && q[1] == ':' && Array.IndexOf(DocIdKinds, q[0]) >= 0;

    /// <summary>Strips from the first '(' to end. Returns input unchanged if no '('.</summary>
    private static string StripParens(string s)
    {
        var i = s.IndexOf('(');
        return i < 0 ? s : s[..i];
    }
}
