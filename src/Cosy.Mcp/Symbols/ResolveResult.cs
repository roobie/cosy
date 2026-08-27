using Microsoft.CodeAnalysis;

namespace Cosy.Mcp.Symbols;

/// <summary>
/// Discriminated-union result of <see cref="FuzzySymbolResolver.ResolveAsync"/>.
/// Consumers pattern-match on subtypes (see Phase 6 CONTEXT.md D-04..D-06).
/// </summary>
public abstract record ResolveResult
{
    /// <summary>Exactly one match above threshold (or an exact DocID/FQN hit).</summary>
    public sealed record ExactMatch(ISymbol Symbol, double Score, string MatchReason) : ResolveResult;

    /// <summary>Two or more distinct candidates scored ≥ threshold. Caller must pick one and retry with exact DocID (D-07: at most 10).</summary>
    public sealed record AmbiguousMatches(IReadOnlyList<SymbolCandidate> TopN) : ResolveResult;

    /// <summary>
    /// No candidate above threshold. <paramref name="NearMisses"/> is a best-effort top-N list of
    /// sub-threshold candidates — may be empty but never null (shape stability per ADR-0004 §8, D-11).
    /// The legacy <see cref="BestMiss"/> accessor remains as a convenience for existing callers;
    /// it is just <c>NearMisses[0]</c> when any near-miss exists.
    /// </summary>
    public sealed record NoMatch(string Query, IReadOnlyList<SymbolCandidate> NearMisses) : ResolveResult
    {
        public SymbolCandidate? BestMiss => NearMisses.Count > 0 ? NearMisses[0] : null;
    }
}
