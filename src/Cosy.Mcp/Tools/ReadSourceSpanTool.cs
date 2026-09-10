using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Search;
using Cosy.Mcp.Source;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for read_source_span -- lives under envelope.data (ADR-0004 §1). This is a single
// OBJECT, not items[] (D-19): the tool has no item universe (one file, one span in, one span
// out), so truncated/total_count/resolved_symbol are ABSENT from the envelope -- there is no list
// to describe and no symbol to echo. truncated_text IS present because the tool always returns
// text (D-12's "present iff the tool returns text" rule).
//
// D-19 amendment (resolved 2026-08-22 at Task 1): data is a SUPERSET of a ReadSourceItem, not a
// different shape -- every ReadSourceItem member (file, span, line, column, end_line, end_column,
// text, text_truncated, snap_suggest) plus requested_span and read_snapshot_id. The only consumers
// of this surface are agents, and an agent that has already read a read_source item should meet
// the same per-site object here rather than a near-miss of it. TextTruncated deliberately
// duplicates the envelope's TruncatedText -- the point of the amendment is that the flag sits
// adjacent to the snap_suggest that acts on it, not that it is a new fact.
public sealed record ReadSourceSpanToolData(
    [property: JsonPropertyName("file")]            string File,
    // The caller's own span, echoed back unmodified -- D-19: a caller that cannot see both this
    // and Span cannot tell a snap from a short read.
    [property: JsonPropertyName("requested_span")]  Span RequestedSpan,
    // What the returned text actually covers after snapping. Equal to RequestedSpan when nothing
    // was cut; its End is always exactly SnapSuggest.Start when something was.
    [property: JsonPropertyName("span")]            Span Span,
    [property: JsonPropertyName("line")]            int Line,
    [property: JsonPropertyName("column")]          int Column,
    [property: JsonPropertyName("end_line")]        int EndLine,
    [property: JsonPropertyName("end_column")]      int EndColumn,
    [property: JsonPropertyName("text")]            string Text,
    [property: JsonPropertyName("text_truncated")]  bool TextTruncated,
    [property: JsonPropertyName("snap_suggest")]    SnapSuggest? SnapSuggest,
    // Explicit null (not omitted) when fromSnapshotId was not supplied -- D-08, same override
    // ReadSourceToolData.ReadSnapshotId carries (ReadSourceTool.cs:27).
    [property: JsonPropertyName("read_snapshot_id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    string? ReadSnapshotId);

/// <summary>
/// read_source_span -- returns a Solution document's text at a caller-supplied file+span, in the
/// same zero-based, end-exclusive UTF-16 code unit coordinate system apply_edits_verified
/// consumes (SC-2). The file+span
/// secondary to read_source's symbol-primary lookup (D-07, D-09): same "read" family, different
/// key. The corpus is Roslyn Solution documents only, resolved by the same suffix match
/// apply_edits_verified uses (D-06) -- a .csproj, .sln, .md or build-output path is unreachable by
/// design, and so is anything outside the loaded Solution.
///
/// The inner-platform guard (D-05) is the load-bearing fact of this tool: <c>span</c> is required,
/// and omitting it is invalid_argument, never a whole-file read. Without that guard this verb is
/// `cat` with a JSON envelope -- strictly worse than the harness's own Read tool.
/// </summary>
[McpServerToolType]
public sealed class ReadSourceSpanTool
{
    [McpServerTool(Name = "read_source_span")]
    [Description("Return a Solution document's text at a caller-supplied file+span -- the " +
        "file+span secondary to read_source's symbol-primary lookup, sharing its prefix " +
        "deliberately so the pair sorts together as one family. span is REQUIRED: an omitted " +
        "span is refused rather than answered with the whole file -- this tool never falls " +
        "back to a full-file read. file is resolved by the same case-insensitive suffix match " +
        "apply_edits_verified uses, over Roslyn Solution documents only; a path matching zero " +
        "documents or more than one is refused -- there is no filesystem fallback. A malformed " +
        "span (negative start, or start above end) is refused as a bad argument; a well-formed " +
        "span whose end exceeds the document is out_of_bounds carrying document_length -- " +
        "neither is ever silently clamped. The response is a single data " +
        "object, not items[]: truncated, total_count and resolved_symbol are absent from the " +
        "envelope (there is no item universe and no symbol), while truncated_text is present " +
        "because the tool always returns text. data.requested_span echoes the caller's span and " +
        "data.span is what the returned text actually covers after being snapped back to a " +
        "syntax boundary by maxChars (default 8000, range 500..200000); when nothing was " +
        "snapped, snap_suggest is null and truncated_text is false, and when something was, " +
        "snap_suggest.start equals data.span.end exactly so a second call can resume with no " +
        "gap and no overlap. When fromSnapshotId is supplied, the read resolves the file and " +
        "reads its text against that staged snapshot instead of the current workspace state; " +
        "data.read_snapshot_id echoes which base answered (the supplied id, or explicit null " +
        "for the current state). Requires workspace_open first.")]
    public async Task<object> GetAsync(
        IWorkspaceHost workspaceHost,
        ILogger<ReadSourceSpanTool> logger,
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal.
        [CosyRequired]
        [Description("Solution-relative (or longer) file path suffix, resolved case-insensitively " +
            "against Roslyn Solution documents -- the same resolution apply_edits_verified uses. " +
            "No filesystem fallback: a .csproj, .sln, .md or non-Solution file is unreachable.")] string? file = null,
        // D-05, the working precedent: already schema-optional before this phase; only the
        // attribute is new. The hand-written "REQUIRED --" marker is removed from the prose
        // below so the marker comes from [CosyRequired]/SchemaRequiredPrefix like every other
        // parameter, not from hand-authored text that can drift from it.
        [CosyRequired]
        [Description("Span to read, {start, end} zero-based, end-exclusive UTF-16 code unit " +
            "offsets into the document's Roslyn SourceText -- never byte offsets from wc -c, and " +
            "wc -m is also wrong (surrogate pairs). An omitted span is refused, never answered " +
            "with a whole-file read.")] Span? span = null,
        [Description("Max characters of text to return (default 8000, range 500..200000). A real " +
            "ceiling: the returned text is never longer, and the cut is snapped back to a " +
            "statement/member boundary, then a line break, then a hard cut.")] int? maxChars = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        [Description("Optional snapshot id to read on top of (ADR-0007 §1.2), instead of the current workspace state. Defaults to the current solution when absent.")] string? fromSnapshotId = null,
        CancellationToken ct = default)
    {
        // ArgumentGuard rejects an absent/null file (CosyRequired) before this handler ever
        // runs -- this binding is a compiler satisfaction only, not a second requiredness check.
        var fileText = file!;

        // --- ARGUMENT VALIDATION (cheap first, before any workspace access) ---

        // D-14: same per-response character budget range read_source enforces. Response-side
        // details.param is snake_case "max_chars"; the wire PARAMETER stays camelCase "maxChars"
        // (ADR-0004 §2's snake_case mandate binds response keys only).
        if (maxChars is not null && (maxChars < 500 || maxChars > 200_000))
            return Envelope<ReadSourceSpanToolData>.Err(
                ToolError.InvalidArgument("max_chars", "out_of_range_500_to_200000", value: maxChars));

        if (timeoutMs is not null && (timeoutMs < 1 || timeoutMs > 600_000))
            return Envelope<ReadSourceSpanToolData>.Err(
                ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: timeoutMs));

        // D-05: the inner-platform guard made literal. A missing span is invalid_argument, never a
        // whole-file read -- this is the single line that keeps this verb from degenerating into
        // `cat` with a JSON envelope. Checked before the solution is rented, per the established
        // validate-cheap-first ordering (FindTextTool.cs:93-115).
        if (span is null)
            return Envelope<ReadSourceSpanToolData>.Err(
                ToolError.InvalidArgument("span", "required_with_file", value: null));

        // Read lease blocks workspace_close from disposing the workspace while we hold a captured
        // snapshot (ADR-0005 §D-06). Not a `using` because the corpus-miss and snapshot-not-found
        // branches below must dispose it before an early return (FindTextTool.cs:142-166 mirror);
        // the `finally` at the bottom covers every other exit path.
        var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
        {
            lease.Dispose();
            return Envelope<ReadSourceSpanToolData>.Err(ToolError.WorkspaceNotLoaded());
        }

        // ADR-0007 §1.2 / D-08: if fromSnapshotId is supplied, both file resolution and text come
        // from that snapshot's Solution rather than CurrentSolution.
        var baseSolution = solution;
        if (fromSnapshotId is not null)
        {
            if (!workspaceHost.TryGetSnapshot(fromSnapshotId, out var snapshotEntry) || snapshotEntry is null)
            {
                lease.Dispose();
                return Envelope<ReadSourceSpanToolData>.Err(ToolError.SnapshotNotFound(fromSnapshotId));
            }
            baseSolution = snapshotEntry.Snapshot;
        }

        logger.LogDebug("read_source_span: file={File}, span={Span}, maxChars={MaxChars}, timeoutMs={TimeoutMs}, fromSnapshotId={FromSnapshotId}",
            fileText, span, maxChars, timeoutMs, fromSnapshotId);

        var sw = Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
            linkedCts.CancelAfter(ms);
        var effectiveCt = linkedCts.Token;

        try
        {
            // D-06: resolve file by the same case-insensitive suffix match
            // ApplyEditsVerifiedTool.cs:154-170 uses -- byte-identical resolution behaviour is the
            // point, so a caller's mental model of "what file resolves" cannot fork between read
            // and write. There is no filesystem fallback: a path matching no Solution document
            // simply fails, never falls through to disk.
            var matches = baseSolution.Projects
                .SelectMany(p => p.Documents)
                .Where(d => d.FilePath != null &&
                            d.FilePath.EndsWith(fileText, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                return Envelope<ReadSourceSpanToolData>.Err(
                    ToolError.InvalidArgument("file", "not_found_in_workspace", value: fileText),
                    (int)sw.ElapsedMilliseconds);
            if (matches.Count > 1)
                return Envelope<ReadSourceSpanToolData>.Err(
                    ToolError.InvalidArgument("file", "ambiguous_path", value: fileText,
                        message: $"ambiguous file path: {fileText} matches {matches.Count} documents"),
                    (int)sw.ElapsedMilliseconds);

            var document = matches[0];
            var text = await document.GetTextAsync(effectiveCt);

            // D-19/D-06: the same two-way bounds split ApplyEditsVerifiedTool.cs:193-203 ships,
            // mirrored exactly so a caller's error handling does not fork between read and write
            // for one physical fact. Malformedness is checked FIRST: an inverted span's end may be
            // perfectly in-range and would otherwise slip through to a misleading success. Neither
            // branch clamps -- a silent clamp is the exact defect class this phase exists to close.
            if (span.Start < 0 || span.End < span.Start)
                return Envelope<ReadSourceSpanToolData>.Err(
                    ToolError.InvalidArgument("span", "inverted_or_negative", value: span),
                    (int)sw.ElapsedMilliseconds);
            if (span.End > text.Length)
                return Envelope<ReadSourceSpanToolData>.Err(
                    ToolError.OutOfBounds(fileText, span, documentLength: text.Length),
                    (int)sw.ElapsedMilliseconds);

            // A2 (12-RESEARCH.md): the requested span has no enclosing declaration the way a
            // read_source symbol does, so BoundarySnap's scope generalises from "the declaration
            // node" to "the document root" -- the whole document is the only search space an
            // arbitrary span can rely on having.
            var root = await document.GetSyntaxRootAsync(effectiveCt);
            var charBudget = maxChars ?? 8000; // D-14's default, same as read_source.

            var snap = BoundarySnap.Snap(
                scope: root!,
                text: text,
                start: span.Start,
                maxChars: charBudget,
                extentEnd: span.End,
                ct: effectiveCt);

            // The emitted span reports the SNAPPED end, so span and text keep agreeing even when
            // the text was cut (SC-2's invariant, same as read_source). line/column are
            // recomputed from the effective span, not the requested one.
            var emitted = TextSpan.FromBounds(span.Start, snap.End);
            var returnedText = text.ToString(emitted);
            var startPos = text.Lines.GetLinePosition(emitted.Start);
            var endPos = text.Lines.GetLinePosition(emitted.End);

            var solutionDir = SolutionPaths.GetSolutionDirectory(baseSolution);
            var relFile = document.FilePath is not null
                ? Path.GetRelativePath(solutionDir, document.FilePath).Replace('\\', '/')
                : fileText;

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            logger.LogInformation("read_source_span: file={File}, truncated={Truncated} in {ElapsedMs}ms",
                relFile, snap.Truncated, elapsedMs);

            var data = new ReadSourceSpanToolData(
                File: relFile,
                RequestedSpan: span,
                Span: new Span(emitted.Start, emitted.End),
                Line: startPos.Line + 1,
                Column: startPos.Character + 1,
                EndLine: endPos.Line + 1,
                EndColumn: endPos.Character + 1,
                Text: returnedText,
                TextTruncated: snap.Truncated,
                SnapSuggest: snap.Suggest,
                ReadSnapshotId: fromSnapshotId);

            // D-19: nothing is passed for resolvedSymbol, truncated or totalCount -- all three
            // must be absent from the wire (no item universe, no symbol), and Envelope.Ok's
            // WhenWritingNull defaults achieve that by leaving them unset.
            return Envelope<ReadSourceSpanToolData>.Ok(data, elapsedMs, truncatedText: snap.Truncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during read_source_span");
            return Envelope<ReadSourceSpanToolData>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Idempotent -- the not-loaded and snapshot-not-found branches above already disposed
            // the lease before returning; this catches every other exit path.
            lease.Dispose();
        }
    }
}
