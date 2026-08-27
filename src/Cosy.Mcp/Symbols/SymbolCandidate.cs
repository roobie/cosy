using Cosy.Mcp.Contracts;
using Microsoft.CodeAnalysis;

namespace Cosy.Mcp.Symbols;

/// <summary>
/// Ranked candidate from fuzzy resolution. Projected to JSON by tool classes
/// (doc_id, score, display_name, containing_symbol) — see 06-PATTERNS.md.
/// </summary>
public sealed record SymbolCandidate(ISymbol Symbol, double Score, string MatchReason);

/// <summary>
/// Shared projection from internal <see cref="SymbolCandidate"/> (carries ISymbol) to wire
/// <see cref="SymbolCandidateDto"/>. Centralized per Plan 08-06/D-11 so every symbol-taking
/// tool emits identical candidate shapes — previously each tool hand-rolled anonymous objects.
/// </summary>
public static class SymbolCandidateExtensions
{
    public static SymbolCandidateDto ToDto(this SymbolCandidate c) => new(
        DocId: c.Symbol.GetDocumentationCommentId(),
        DisplayName: c.Symbol.ToDisplayString(),
        Score: c.Score,
        MatchReason: c.MatchReason,
        ContainingSymbolId: c.Symbol.ContainingSymbol?.GetDocumentationCommentId());

    public static IReadOnlyList<SymbolCandidateDto> ToDtos(this IReadOnlyList<SymbolCandidate> list)
        => list.Select(ToDto).ToArray();
}
