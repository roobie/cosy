using System.Text;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cosy.Mcp.Source;

/// <summary>
/// D-13's semantic hint: the span of the un-returned remainder PLUS the syntax kind and display
/// name of the construct that begins it, so an agent can decide whether it wants the rest. It is
/// deliberately NOT a bare byte cursor -- that distinction is what keeps read_source from
/// degenerating into paginated `cat`.
/// </summary>
public sealed record SnapSuggest(
    [property: JsonPropertyName("start")]        int Start,
    [property: JsonPropertyName("end")]          int End,
    [property: JsonPropertyName("kind")]         string Kind,
    [property: JsonPropertyName("display_name")] string DisplayName);

/// <summary>
/// Outcome of a boundary snap: the offset the returned text should stop at, whether a cut
/// actually occurred, and (only when it did) the hint describing what comes next.
/// </summary>
/// <param name="End">Exclusive end offset for the text to return. Never greater than
/// <c>start + maxChars</c> and never greater than the extent end.</param>
/// <param name="Truncated">True iff the extent did not fit inside the budget.</param>
/// <param name="Suggest">Null iff <paramref name="Truncated"/> is false.</param>
public readonly record struct SnapResult(int End, bool Truncated, SnapSuggest? Suggest);

/// <summary>
/// D-11's three-tier backward boundary snap, shared by read_source (scope = the declaration node,
/// extent = its FullSpan) and, from 12-04, read_source_span (scope = the document root, extent =
/// the requested span -- assumption A2).
///
/// The entry point is public static on purpose: read_source_span consumes it in 12-04, and this
/// plan's continuation fact drives it directly because no span-keyed read verb exists yet. It is
/// a shared helper in its own namespace, not tool-private. A future tidy-up must not re-privatise it.
///
/// CONTEXT.md's ORIGINAL D-11 algorithm was corrected before this shipped, and the correction is
/// the whole point of the file: the draft walked the ancestors of the token sitting at the cap.
/// Every such ancestor ends at or AFTER that token, so an ancestor chain yields monotonically
/// LARGER spans and finds the nearest boundary AFTER the cap -- the opposite of a ceiling. The
/// search here is over candidate ENDS instead. Do not "simplify" it back toward an ancestor walk.
/// </summary>
public static class BoundarySnap
{
    /// <summary>Cap for the human-facing <see cref="SnapSuggest.DisplayName"/> label (PD-02).</summary>
    private const int DisplayNameMaxLength = 80;

    /// <summary>PD-04's fallback kind, when no statement or member ancestor covers the snap point.</summary>
    private const string GenericTextKind = "text";

    /// <summary>
    /// Phase 14 D-09: cut <paramref name="value"/> to at most <paramref name="maxUnits"/> UTF-16
    /// code units, never splitting a surrogate pair. Extracted from <see cref="FirstLineLabel"/>'s
    /// pre-existing surrogate-safe cut rather than re-derived — this is the phase's only other
    /// call site (the bounded excerpt on <c>expected_text_mismatch</c>), and the two truncations
    /// must agree on the same edge case. <paramref name="value"/> shorter than or equal to the
    /// bound is returned unchanged.
    /// </summary>
    public static string TruncateForDisplay(string value, int maxUnits = DisplayNameMaxLength)
    {
        if (value.Length <= maxUnits)
            return value;

        var cut = maxUnits;
        if (char.IsHighSurrogate(value[cut - 1]) && char.IsLowSurrogate(value[cut]))
            cut--;
        return value[..cut];
    }

    /// <summary>
    /// Snap the end of a read so the returned text is at most <paramref name="maxChars"/>
    /// characters and stops at a syntax boundary where one exists.
    /// </summary>
    /// <param name="scope">Node to search within -- the declaration node for read_source, the
    /// document root for read_source_span (A2).</param>
    /// <param name="text">The document's text, the same one the caller slices for the response.</param>
    /// <param name="start">Offset the returned text begins at. For read_source this is the
    /// declaration's <c>FullSpan.Start</c> (assumption A1), NOT its narrow <c>Span.Start</c> --
    /// otherwise emitted leading trivia would not count against the budget and an XML-doc-heavy
    /// declaration would silently exceed the ceiling.</param>
    /// <param name="maxChars">The per-item character budget (D-14). The tool validates it to
    /// [500, 200000] before calling; any value of at least 1 is safe here.</param>
    /// <param name="extentEnd">End of the extent being read -- the declaration's
    /// <c>FullSpan.End</c> for read_source, the requested span's end for read_source_span.</param>
    /// <param name="ct">The caller's effective token, so a timeout is observable inside the two
    /// unbounded Roslyn operations here (ADR-0004 §7's inward token flow). It matters most for
    /// read_source_span, whose scope is the whole document root rather than one declaration.</param>
    public static SnapResult Snap(SyntaxNode scope, SourceText text, int start, int maxChars, int extentEnd, CancellationToken ct = default)
    {
        // long arithmetic so a caller-supplied budget can never overflow into a negative limit.
        var limit = (int)Math.Min((long)start + maxChars, extentEnd);

        // The whole extent fits: nothing is cut, so there is no suggestion to make. Returning
        // early here is also what lets tier 1 skip a "is this the scope node itself?" guard --
        // any surviving candidate necessarily ends strictly inside the extent.
        if (limit >= extentEnd)
            return new SnapResult(extentEnd, Truncated: false, Suggest: null);

        // --- TIER 1: the greatest statement/member end inside the budget ---
        //
        // The post-query filter below is LOAD-BEARING, not cosmetic tightening.
        // DescendantNodes(TextSpan) filters by INTERSECTION, not containment: a node whose
        // FullSpan begins inside the query span but extends far past it IS returned. Verified
        // live against the pinned Roslyn 5.3.0 -- a node ending at 122 came back from a query
        // capped at 104 (12-RESEARCH.md Pattern 2 / Pitfall 1). An implementation that trusts the
        // query to have already excluded it silently returns MORE text than the caller asked for,
        // breaking the one invariant D-11 states about itself.
        //
        // The comparison at the limit is INCLUSIVE. An exclusive comparison rejects a node ending
        // exactly at the budget -- a one-character truncation bug that only surfaces on the rare
        // declaration whose node lands precisely there.
        //
        // The filter is on ENDS ONLY. An earlier draft also required the candidate's FullSpan to
        // begin after `start`, to "exclude the scope node itself". That is unnecessary --
        // DescendantNodes never yields the scope node, that is DescendantNodesAndSelf -- and
        // actively harmful: on a continuation read `start` is a previous snap's end, so the very
        // next statement begins AT it and would be discarded, dropping the read to a line break or
        // a hard cut with a real syntax boundary sitting unused inside the budget.
        //
        // The token check rides in the first predicate because DescendantNodes has no cancellable
        // overload -- the lazy pipeline is the only place this traversal can observe cancellation.
        var querySpan = TextSpan.FromBounds(start, limit);
        var candidate = scope
            .DescendantNodes(querySpan)
            .Where(n => { ct.ThrowIfCancellationRequested(); return n is StatementSyntax or MemberDeclarationSyntax; })
            .Where(n => n.FullSpan.End > start && n.FullSpan.End <= limit)
            .OrderByDescending(n => n.FullSpan.End)
            .FirstOrDefault();

        int? snapped = candidate?.FullSpan.End;

        // --- TIER 2: the last line break at or before the limit ---
        // Expressed as a line START (the offset just after a line break), so the returned text
        // ends immediately after a newline and never splits a line in half. The greatest line
        // start at or before `limit` is by definition the start of the line containing `limit`.
        if (snapped is null)
        {
            var lineStart = text.Lines.GetLineFromPosition(limit).Start;
            if (lineStart > start)
                snapped = lineStart;
        }

        // --- TIER 3: hard cut at the limit ---
        var end = snapped ?? limit;

        return new SnapResult(end, Truncated: true, Suggest: BuildSuggest(scope, text, end, extentEnd, ct));
    }

    /// <summary>
    /// PD-03/PD-04: the suggestion spans from the snap point to the end of the extent, and is
    /// labelled with the construct that BEGINS at the snap point -- found by taking the token at
    /// that offset and walking up to the nearest statement or member-declaration ancestor.
    ///
    /// Leading trivia belongs to the FOLLOWING token (verified live, 12-RESEARCH.md Pattern 2), so
    /// a snap landing exactly at a node's FullSpan.Start puts a preceding comment block into the
    /// NEXT chunk -- a comment is never split -- and the token lookup below naturally resolves to
    /// the construct that comment documents.
    /// </summary>
    private static SnapSuggest BuildSuggest(SyntaxNode scope, SourceText text, int snapEnd, int extentEnd, CancellationToken ct)
    {
        // The root, not `scope`: FindToken throws when the position is outside the node's span,
        // and for read_source_span the snap point can sit outside any single declaration.
        // GetRoot(ct), not the parameterless overload -- the latter is synchronous AND
        // non-cancellable, which is the second half of the inward-token-flow gap.
        var root = scope.SyntaxTree.GetRoot(ct);
        var token = root.FindToken(snapEnd);

        SyntaxNode? owner = token.Parent;
        while (owner is not null && owner is not StatementSyntax && owner is not MemberDeclarationSyntax)
            owner = owner.Parent;

        var (kind, displayName) = owner switch
        {
            MemberDeclarationSyntax m => (MemberKind(m), MemberIdentifier(m) ?? FirstLineLabel(text, m.Span)),
            StatementSyntax s => (ToLowerSnakeCase(s.Kind().ToString()), FirstLineLabel(text, s.Span)),
            _ => (GenericTextKind, FirstLineLabel(text, TextSpan.FromBounds(snapEnd, extentEnd))),
        };

        return new SnapSuggest(snapEnd, extentEnd, kind, displayName);
    }

    /// <summary>
    /// PD-01: a member declaration reuses GetMembersTool's lowercase symbol-kind vocabulary, so an
    /// agent learns one vocabulary across two verbs. Anything that vocabulary has no term for
    /// falls back to the syntax-kind name in lower snake case, the same form statements use.
    /// </summary>
    private static string MemberKind(MemberDeclarationSyntax m) => m switch
    {
        ConstructorDeclarationSyntax                       => "constructor",
        MethodDeclarationSyntax                            => "method",
        PropertyDeclarationSyntax or IndexerDeclarationSyntax => "property",
        FieldDeclarationSyntax                             => "field",
        EventDeclarationSyntax or EventFieldDeclarationSyntax => "event",
        BaseTypeDeclarationSyntax or DelegateDeclarationSyntax => "nested_type",
        _                                                  => ToLowerSnakeCase(m.Kind().ToString()),
    };

    /// <summary>PD-02: a member declaration is labelled by its declared identifier.</summary>
    private static string? MemberIdentifier(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x       => x.Identifier.Text,
        PropertyDeclarationSyntax x     => x.Identifier.Text,
        EventDeclarationSyntax x        => x.Identifier.Text,
        ConstructorDeclarationSyntax x  => x.Identifier.Text,
        DestructorDeclarationSyntax x   => x.Identifier.Text,
        BaseTypeDeclarationSyntax x     => x.Identifier.Text,
        DelegateDeclarationSyntax x     => x.Identifier.Text,
        // FieldDeclarationSyntax and EventFieldDeclarationSyntax both derive from this; a
        // declaration can declare several variables, so the first one names the site.
        BaseFieldDeclarationSyntax x    => x.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        OperatorDeclarationSyntax x     => x.OperatorToken.Text,
        IndexerDeclarationSyntax        => "this[]",
        _                               => null,
    };

    /// <summary>
    /// PD-02: a statement (and any unlabelled construct) is described by the first non-empty line
    /// of its NARROW span, trimmed and capped. Human/agent-facing only -- never parsed.
    /// </summary>
    private static string FirstLineLabel(SourceText text, TextSpan span)
    {
        if (span.IsEmpty)
            return string.Empty;

        foreach (var line in text.ToString(span).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            // The cap counts UTF-16 code units, so a non-BMP character (emoji, CJK Ext-B) whose
            // pair straddles the cut would leave a lone high surrogate on the wire -- malformed
            // UTF-16 that System.Text.Json escapes rather than rejects, so it fails at the client
            // instead of here. TruncateForDisplay steps back one unit when the cut lands inside a
            // pair (Phase 14 D-09) and is a no-op when trimmed already fits.
            return TruncateForDisplay(trimmed);
        }
        return string.Empty;
    }

    /// <summary>PD-01: "LocalDeclarationStatement" becomes "local_declaration_statement".</summary>
    private static string ToLowerSnakeCase(string pascalCase)
    {
        var sb = new StringBuilder(pascalCase.Length + 8);
        for (var i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
