using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Search;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for find_text -- lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6; not duplicated here.
// searched_snapshot_id echoes which base answered the search (D-06a): the supplied
// fromSnapshotId when a staged snapshot was searched, null when CurrentSolution was.
public sealed record FindTextToolData(
    [property: JsonPropertyName("items")]                 IReadOnlyList<FindTextItem> Items,
    [property: JsonPropertyName("searched_snapshot_id")]   string? SearchedSnapshotId);

// Item's first seven members are byte-identical to FindReferencesItem's first seven
// (file, span, line, column, end_line, end_column, containing_symbol_id) per D-05, then
// diverges: context (syntactic classification) + line_text (the matched line's own text).
public sealed record FindTextItem(
    [property: JsonPropertyName("file")]                 string File,
    [property: JsonPropertyName("span")]                 Span Span,
    [property: JsonPropertyName("line")]                 int Line,
    [property: JsonPropertyName("column")]               int Column,
    [property: JsonPropertyName("end_line")]             int EndLine,
    [property: JsonPropertyName("end_column")]           int EndColumn,
    // The MCP SDK's McpJsonUtilities.CreateDefaultOptions sets a serializer-wide
    // DefaultIgnoreCondition = WhenWritingNull, which would otherwise omit this key entirely
    // for an unresolved hit. JsonIgnore(Condition = Never) overrides that default per-property
    // so the key is always emitted, carrying explicit JSON null when unresolved -- required by
    // 10.1-04 (must_have) and kept byte-identical to FindReferencesItem's identical override.
    [property: JsonPropertyName("containing_symbol_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? ContainingSymbolId,
    [property: JsonPropertyName("context")]              string Context,
    [property: JsonPropertyName("line_text")]            string LineText);

/// <summary>
/// find_text -- dry-read literal (v1) text search over the loaded Solution's documents.
/// The corpus is Roslyn <see cref="Document"/>s, not the filesystem: a file no project
/// references, and a non-source file under a project directory, are invisible to it and
/// remain Bash's job (D-02). Two-phase: (1) cheap scan over raw SourceText, ordered by
/// (path, offset) and capped BEFORE enrichment (D-04, avoids the sort-after-truncate
/// defect); (2) semantic/syntactic enrichment of survivors only -- containing_symbol_id,
/// syntactic context classification, and the matched line's own text (D-09).
/// </summary>
[McpServerToolType]
public sealed class FindTextTool
{
    [McpServerTool(Name = "find_text")]
    [Description("Search literal text across every document in the loaded Solution -- the " +
        "corpus is Roslyn documents, not the filesystem, so a file no project references or a " +
        "non-source file under a project directory is invisible here and remains Bash's job. " +
        "Returns flat items[] with (file, span, line, column, end_line, end_column, " +
        "containing_symbol_id, context, line_text); context classifies the hit as one of " +
        "comment/string/identifier/xmldoc/directive/code from the syntax tree. Items are " +
        "ordered by (file, span.start) and capped at max (default 500); envelope carries " +
        "truncated and total_count. When fromSnapshotId is supplied, the search reads that " +
        "staged snapshot's text instead of the current workspace state; data.searched_snapshot_id " +
        "echoes which base answered (the supplied id, or null for the current state). pathGlob is " +
        "solution-relative, supports ** and is case-insensitive (D-07 -- independent of " +
        "caseSensitive, which governs only the content pattern). regex is opt-in and defaults " +
        "to literal matching; an uncompilable pattern and a pattern that exceeds the per-match " +
        "budget both return invalid_argument with distinct reason values (invalid_regex, " +
        "regex_match_timeout). Latency contract: the handler is bounded by timeoutMs plus one " +
        "match budget, where the budget is the smaller of 1000ms and timeoutMs -- a backtracking " +
        "regex match cannot be interrupted by the request's cancellation token, so timeoutMs " +
        "alone does not bound the scan as a whole. For a multi-targeted project, a hit is found " +
        "regardless of which TargetFrameworks entry's document instance was selected for that " +
        "path, but context and containing_symbol_id are computed from the selected instance's " +
        "syntax tree -- for a hit inside a region disabled under that instance's preprocessor " +
        "symbols (e.g. the non-selected side of an #if TFM check), these two fields may not " +
        "reflect the token's live semantic identity. Requires workspace_open first.")]
    public async Task<object> FindAsync(
        IWorkspaceHost workspaceHost,
        ILogger<FindTextTool> logger,
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal. The existing
        // string.IsNullOrEmpty(pattern) check below is [NotNullWhen(false)]-annotated, so it
        // narrows pattern for the rest of this method once ArgumentGuard's own check has already
        // rejected an absent/null pattern before dispatch -- no separate null-forgiveness needed.
        [CosyRequired]
        [Description("Literal text to search for, or a .NET regex pattern when regex:true. Required, non-empty.")] string? pattern = null,
        [Description("Case-sensitive match (default true).")] bool caseSensitive = true,
        [Description("Max items to return (default 500). Lower to bound response size; higher to raise the cap.")] int? max = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        [Description("Optional snapshot id to search on top of (ADR-0007 §1.2), instead of the current workspace state. Defaults to the current solution when absent.")] string? fromSnapshotId = null,
        [Description("Optional glob to restrict which solution-relative document paths are searched (e.g. 'Sample.Contracts/**/*.cs'). Case-insensitive; supports **. Absent means every document.")] string? pathGlob = null,
        [Description("Treat pattern as a .NET regular expression instead of literal text (default false). An uncompilable pattern returns invalid_argument/invalid_regex; a pattern that exceeds the per-call match budget returns invalid_argument/regex_match_timeout.")] bool regex = false,
        CancellationToken ct = default)
    {
        // --- ARGUMENT VALIDATION (cheap first, before any workspace access) ---

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<FindTextToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        if (timeoutMs is not null && (timeoutMs < 1 || timeoutMs > 600_000))
            return Envelope<FindTextToolData>.Err(
                ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: timeoutMs));

        // Per-call match budget: the smaller of 1000ms and timeoutMs, computed once before any
        // workspace access. Not caller-tunable in the sense that matters -- no parameter sets it
        // directly and it can only ever tighten. Deliberately NOT a fresh per-attempt timeout
        // derived from a monotonic deadline: Regex's match timeout is fixed at construction and
        // is part of the static cache key, so a per-attempt timeout would mean constructing a new
        // Regex per attempt or thrashing the engine's small cache -- a real per-hit cost on every
        // well-behaved search to shave a bounded tail off hostile ones (D-05 rejected this
        // variant with a promotion trigger; left rejected).
        var matchTimeout = TimeSpan.FromMilliseconds(timeoutMs is int msBudget ? Math.Min(1000, msBudget) : 1000);

        if (string.IsNullOrEmpty(pattern))
            return Envelope<FindTextToolData>.Err(
                ToolError.InvalidArgument("pattern", "empty", value: pattern));

        // regex mode: compile before the read lease, matching the cheap-validation-before-
        // workspace-access ordering already established above. Ordinary backtracking engine --
        // RegexOptions.NonBacktracking is explicitly rejected by D-05 because it refuses
        // lookarounds and backreferences, which agents do write.
        Regex? regexObj = null;
        if (regex)
        {
            try
            {
                // CultureInvariant is required alongside IgnoreCase: RegexOptions.IgnoreCase
                // alone folds case using the CURRENT CULTURE (e.g. Turkish tr-TR folds 'I' to
                // 'ı', not 'i'), while literal mode's OrdinalIgnoreCase (below, comparison) is
                // already locale-independent. Without this, the same caseSensitive:false
                // argument would have environment-dependent semantics depending on regex vs
                // literal mode -- a determinism violation (WR-01, 10.1-REVIEW.md).
                var options = (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant;
                regexObj = new Regex(pattern, options, matchTimeout);
            }
            catch (ArgumentException)
            {
                return Envelope<FindTextToolData>.Err(
                    ToolError.InvalidArgument("pattern", "invalid_regex", value: pattern));
            }
        }

        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body; not
        // a `using` because the snapshot-not-found branch below must dispose it before an early
        // return (ApplyEditsVerifiedTool.cs:115-138 mirror) -- the `finally` below covers every
        // other exit path.
        var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
        {
            lease.Dispose();
            return Envelope<FindTextToolData>.Err(ToolError.WorkspaceNotLoaded());
        }

        // ADR-0007 §1.2: if fromSnapshotId is supplied, the search base is that snapshot's
        // Solution rather than CurrentSolution. The read lease still gates CloseAsync; the
        // snapshot itself is an immutable Roslyn pointer that survives lease release.
        var baseSolution = solution;
        if (fromSnapshotId is not null)
        {
            if (!workspaceHost.TryGetSnapshot(fromSnapshotId, out var snapshotEntry) || snapshotEntry is null)
            {
                lease.Dispose();
                return Envelope<FindTextToolData>.Err(ToolError.SnapshotNotFound(fromSnapshotId));
            }
            baseSolution = snapshotEntry.Snapshot;
        }

        logger.LogDebug("find_text: pattern={Pattern}, caseSensitive={CaseSensitive}, max={Max}, timeoutMs={TimeoutMs}, fromSnapshotId={FromSnapshotId}, pathGlob={PathGlob}",
            pattern, caseSensitive, max, timeoutMs, fromSnapshotId, pathGlob);

        var sw = Stopwatch.StartNew();

        // D-08: compose caller CT with optional timeout via linked CTS.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
            linkedCts.CancelAfter(ms);
        var effectiveCt = linkedCts.Token;

        try
        {
            var cap = max ?? 500; // D-08 default.
            var solutionDir = SolutionPaths.GetSolutionDirectory(baseSolution);

            // D-07: one glob dialect, built once before the scan. Filters the already-
            // materialised in-memory path list only -- never the filesystem (D-02).
            var pathMatcher = SolutionGlob.Create(pathGlob);

            // --- PHASE ONE: cheap scan, no semantic binding (D-04) ---
            // Ordinary documents only -- source-generated / additional-document enumeration
            // is the v2 slice deferred by D-15.
            var candidates = baseSolution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath is not null)
                .Select(d => (Doc: d, RelPath: Path.GetRelativePath(solutionDir, d.FilePath!).Replace('\\', '/')))
                // M1 fix: collapse to one candidate per solution-relative path. A linked file or
                // a multi-targeted project yields one Document instance per project/TFM for the
                // same physical file; scanning every instance would emit duplicate
                // (file, span.start) rows and inflate total_count (the planned Sample.MultiTfm
                // fixture in 10.1-02 makes this reachable). Pick a deterministic representative
                // ordered by the owning project's Name (Ordinal) -- ProjectId/DocumentId are
                // GUIDs and are NOT stable across loads, so they cannot be the tiebreaker.
                .GroupBy(x => x.RelPath, StringComparer.Ordinal)
                .Select(g => g.OrderBy(x => x.Doc.Project.Name, StringComparer.Ordinal).First())
                .Where(x => pathMatcher is null || pathMatcher.IsMatch(x.RelPath))
                // Sort BEFORE scanning so scan order is reproducible across runs (later plans
                // depend on this for the late-in-scan deadline fact).
                .OrderBy(x => x.RelPath, StringComparer.Ordinal)
                .ToList();

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            // M2 fix: bounded retention instead of collect-everything-then-sort-then-truncate.
            // Candidates are unique per path (M1) and sorted Ordinal; IndexOf below yields
            // strictly ascending offsets within a document. This sequential scan therefore
            // already visits hits in exactly (path, offset) order, so the first `cap` hits
            // encountered ARE the deterministic top-K -- no discovery/completion-order bias
            // (D-04, non-negotiable; the sort-after-truncate defect once logged against
            // find_references/get_members is exactly what this ordering avoids). Only
            // the bounded survivors list is retained; `total` still counts every hit so
            // total_count stays exact even when survivors is capped. Length is stored per-hit
            // (not just taken from pattern.Length) because a regex match's length varies hit to
            // hit; the literal path always stores pattern.Length.
            var survivors = new List<(string RelPath, int Offset, int Length, DocumentId DocId)>();
            var total = 0;

            foreach (var (doc, relPath) in candidates)
            {
                var text = (await doc.GetTextAsync(effectiveCt)).ToString();
                var start = 0;
                var hitsInDoc = 0;
                if (regex)
                {
                    // Match walk: repeatedly re-anchor at the previous match's end. A zero-width
                    // match advances by at least one char (Math.Max(1, m.Length)) so the scan
                    // cannot loop forever on a pattern like "a*".
                    while (start <= text.Length)
                    {
                        var m = regexObj!.Match(text, start);
                        if (!m.Success) break;
                        total++;
                        if (survivors.Count < cap)
                            survivors.Add((relPath, m.Index, m.Length, doc.Id));
                        start = m.Index + Math.Max(1, m.Length);
                        if (++hitsInDoc % 256 == 0)
                            effectiveCt.ThrowIfCancellationRequested();
                    }
                }
                else
                {
                    while (true)
                    {
                        var idx = text.IndexOf(pattern, start, comparison);
                        if (idx < 0) break;
                        total++;
                        if (survivors.Count < cap)
                            survivors.Add((relPath, idx, pattern.Length, doc.Id));
                        start = idx + pattern.Length; // non-overlapping occurrences
                        // M2 fix: observe cancellation inside high-hit documents too, not only
                        // between documents -- one huge file with many matches must not be able
                        // to run past the deadline undetected.
                        if (++hitsInDoc % 256 == 0)
                            effectiveCt.ThrowIfCancellationRequested();
                    }
                }
                effectiveCt.ThrowIfCancellationRequested();
            }

            // --- PHASE TWO: enrich survivors only, unconditionally (no opt-out in v1) ---
            var items = new List<FindTextItem>();
            foreach (var group in survivors.GroupBy(h => h.DocId))
            {
                var doc = baseSolution.GetDocument(group.Key)!;
                var semanticModel = await doc.GetSemanticModelAsync(effectiveCt);
                var tree = await doc.GetSyntaxTreeAsync(effectiveCt);
                var root = tree is null ? null : await tree.GetRootAsync(effectiveCt);
                var sourceText = await doc.GetTextAsync(effectiveCt);

                foreach (var hit in group)
                {
                    var containing = semanticModel?.GetEnclosingSymbol(hit.Offset, effectiveCt);
                    var context = root is null ? "code" : ClassifyContext(root, hit.Offset);
                    var startPos = sourceText.Lines.GetLinePosition(hit.Offset);
                    var endPos = sourceText.Lines.GetLinePosition(hit.Offset + hit.Length);
                    var lineText = sourceText.Lines[startPos.Line].ToString();

                    items.Add(new FindTextItem(
                        File: hit.RelPath,
                        Span: new Span(hit.Offset, hit.Offset + hit.Length),
                        Line: startPos.Line + 1,
                        Column: startPos.Character + 1,
                        EndLine: endPos.Line + 1,
                        EndColumn: endPos.Character + 1,
                        ContainingSymbolId: containing?.GetDocumentationCommentId(),
                        Context: context,
                        LineText: lineText));
                }
            }

            // GroupBy(DocId) does not preserve the global (path, offset) order established
            // above -- restore it so the response honours the determinism invariant.
            items = items.OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Span.Start).ToList();

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            logger.LogInformation("find_text: {Count} item(s), truncated={Truncated} in {ElapsedMs}ms",
                items.Count, truncated, elapsedMs);

            var data = new FindTextToolData(items, fromSnapshotId);
            return Envelope<FindTextToolData>.Ok(data, elapsedMs, truncated: truncated, totalCount: total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RegexMatchTimeoutException)
        {
            // If the linked token has already expired by the moment this is caught, the request
            // is deadline-cancelled -- propagate cancellation rather than mislabelling it as a
            // hostile pattern (a deadline-cancelled request must never surface as
            // regex_match_timeout). Only when the token is still live is this genuinely the
            // per-call match budget being exceeded by caller input; route it through
            // invalid_argument, not the internal-error factory, so a hostile pattern is
            // attributed to `pattern`, not to Cosy.
            effectiveCt.ThrowIfCancellationRequested();
            return Envelope<FindTextToolData>.Err(
                ToolError.InvalidArgument("pattern", "regex_match_timeout", value: pattern));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during find_text");
            return Envelope<FindTextToolData>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Idempotent -- the not-loaded and snapshot-not-found branches above already
            // disposed the lease before returning; this catches every other exit path (success,
            // cancellation, internal error) now that the lease is no longer a `using`.
            lease.Dispose();
        }
    }

    // Source: 10.1-RESEARCH.md §Code Examples, live-verified. Trivia and ancestry checks run
    // BEFORE token-kind checks -- a `<see cref>` identifier or a `#if` identifier would
    // otherwise misclassify as "identifier" before its trivia/ancestor context is checked.
    private static string ClassifyContext(SyntaxNode root, int offset)
    {
        var token = root.FindToken(offset, findInsideTrivia: true);

        foreach (var trivia in token.LeadingTrivia.Concat(token.TrailingTrivia))
        {
            if (!trivia.FullSpan.Contains(offset)) continue;
            if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                return "comment";
            if (trivia.HasStructure && trivia.GetStructure() is DocumentationCommentTriviaSyntax)
                return "xmldoc";
        }

        var node = root.FindNode(new TextSpan(offset, 0), findInsideTrivia: true, getInnermostNodeForTie: true);
        if (node?.AncestorsAndSelf().Any(n => n is DocumentationCommentTriviaSyntax) == true)
            return "xmldoc";
        if (node?.AncestorsAndSelf().Any(n => n is DirectiveTriviaSyntax) == true)
            return "directive";

        var kind = token.Kind();
        if (kind is SyntaxKind.StringLiteralToken or SyntaxKind.InterpolatedStringTextToken
                 or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
                 or SyntaxKind.Utf8StringLiteralToken)
            return "string";
        if (kind == SyntaxKind.IdentifierToken)
            return "identifier";

        return "code";
    }
}
