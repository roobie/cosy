using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Source;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Edit input DTOs: offset-based spans per ADR-0004 §3 (D-04): {start, end}, end-exclusive.
// Breaking change from legacy {start, length} — accepted per pre-1.0 spike posture.
// Span is the canonical Cosy.Mcp.Contracts.Span record; no local span type required.
//
// Explicit JsonPropertyName on every member because the MCP SDK emits the input schema
// directly from this record (ADR-0006: fully-typed inputs). Without these, NewText becomes
// "newText" on the wire, violating the snake_case mandate in ADR-0004 §2.
public record EditRequest(
    [property: JsonPropertyName("file")]     string File,
    [property: JsonPropertyName("span")]     Span Span,
    [property: JsonPropertyName("new_text")] string NewText,
    // Phase 14 D-05/D-06: the text the caller believes currently occupies `span`, compared
    // ordinally against the document's actual content at that span. A mismatch refuses the
    // ENTIRE call (nothing stages). Optional (`= null`), per-edit, so a batch may verify some
    // edits and not others (D-07) — required is a one-way commitment this repo has already
    // shipped as an incident twice (COSY-0004, COSY-0005): the MCP SDK's schema emitter marks
    // a parameter required based on the presence of a C# default value, not nullability
    // (ADR-0023), so omitting `= null` here would put this field in the emitted `required`
    // array and reject every existing caller above Cosy's own code.
    [property: JsonPropertyName("expected_text")]
    [property: Description(
        "Optional. The text the caller believes currently occupies `span`, compared exactly " +
        "using ordinal UTF-16 string equality, with no line-ending normalization. A mismatch " +
        "refuses the entire call and stages nothing, returning expected_text_mismatch with the " +
        "text actually found at the span.")]
    string? ExpectedText = null);

// Data payload for apply_edits_verified — lives under envelope.data (ADR-0004 §1).
// Diagnostics now carry span: {start, end} adjacent to line/column per D-05.
public sealed record ApplyEditsVerifiedToolData(
    [property: JsonPropertyName("snapshot_id")]   string SnapshotId,
    [property: JsonPropertyName("files_touched")] int FilesTouched,
    [property: JsonPropertyName("edits_applied")] int EditsApplied,
    [property: JsonPropertyName("edits")]         IReadOnlyList<ApplyEditsEcho> Edits,
    [property: JsonPropertyName("diagnostics")]   IReadOnlyList<ApplyEditsDiagnostic> Diagnostics);

// Per-edit echo (D-13 extension): confirms the edit the agent submitted was
// applied. We emit new_text_length rather than new_text itself (D-17 spirit:
// edit payloads can carry secrets/noise — length is enough to round-trip-verify).
public sealed record ApplyEditsEcho(
    [property: JsonPropertyName("file")]            string File,
    [property: JsonPropertyName("span")]            Span Span,
    [property: JsonPropertyName("new_text_length")] int NewTextLength,
    [property: JsonPropertyName("applied")]         bool Applied);

public sealed record ApplyEditsDiagnostic(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("message")]    string Message,
    [property: JsonPropertyName("file")]       string File,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

/// <summary>
/// apply_edits_verified -- atomically apply text edits to the in-memory workspace, re-bind
/// touched documents, baseline-subtract diagnostics, store the result as a deferred-promote
/// snapshot in the WorkspaceHost ring (ADR-0007 §1, D-01). CurrentSolution is NOT mutated;
/// promotion happens only via commit_snapshot. No disk writes. No Console.Write (PITFALLS §6).
/// </summary>
[McpServerToolType]
public sealed class ApplyEditsVerifiedTool
{
    [McpServerTool(Name = "apply_edits_verified")]
    [Description("Atomically apply text edits to the in-memory workspace, re-bind touched documents, " +
        "and return only net-new diagnostics (baseline-subtracted). Returns a snapshot_id for caller reference. " +
        "No files are written to disk. Requires workspace_open first.")]
    public async Task<object> ApplyAsync(
        IWorkspaceHost workspaceHost,
        ILogger<ApplyEditsVerifiedTool> logger,
        // Phase 12.3 D-01/D-13: every tool parameter is schema-optional now (nullable + = null,
        // moved after the DI params — CS1737, D-12); [CosyRequired] below is the sole remaining
        // requiredness signal, advisory in the emitted description and enforced by ArgumentGuard.
        [CosyRequired]
        [Description("Edits to apply atomically; each item is {file, span: {start, end}, new_text} with span end exclusive (ADR-0004 §3). " +
            "Every span is a coordinate in the document as you last read it, NOT in the text left by the other edits in this same call — do not rebase spans against your own earlier edits (ADR-0016). " +
            "Type is EditRequest[] per ADR-0006: input parameters are statically typed so the MCP-emitted schema is unambiguous to calling agents. " +
            "Span offsets are zero-based, end-exclusive, UTF-16 code unit indices into the document's Roslyn SourceText (ADR-0004 §3) — they must NOT be derived from `wc -c`, `ls -l`, file size, or any encoded byte-stream length. " +
            "`wc -m` is ALSO wrong: it disagrees with Roslyn on surrogate pairs, so switching from `wc -c` to `wc -m` looks like a fix but is not. " +
            "A correct offset comes from a span Cosy already emitted (find_text, read_source, read_source_span), a .NET/Roslyn string position, or the document_length returned on an out_of_bounds error.")] EditRequest[]? edits = null,
        // Deliberately NOT [CosyRequired]: schema-required today only by the accident this phase
        // exists to fix (D-12). The handler already treats an omitted verify as "bind" (below);
        // marking it would move the rejection from the schema into ArgumentGuard rather than
        // removing it, defeating SC-1 and the edits-only success this phase's own facts assert.
        [Description("Verification level: 'bind' (default, and currently the only supported value). " +
            "Any other value is rejected with unsupported_option (no silent downgrade).")] string? verify = null,
        [Description("Max edit rows to echo in data.edits (default 500). Edits are applied atomically regardless; this only bounds the echoed list.")] int? max = null,
        // Wire name is camelCase ("fromSnapshotId") to match existing tool input conventions —
        // the MCP SDK uses ParameterInfo.Name directly; ADR-0004 §2 snake_case applies to
        // response keys (via [JsonPropertyName]), not request parameters.
        [Description("Optional snapshot id to apply edits on top of (chains a new snapshot off the given one — ADR-0007 §1.2). Defaults to CurrentSolution when absent.")] string? fromSnapshotId = null,
        CancellationToken ct = default)
    {
        // --- ARGUMENT VALIDATION (no workspace access required) ---

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<ApplyEditsVerifiedToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));
        var cap = max ?? 500;

        // ADR-0006: edits is statically typed. MCP SDK deserializes the JSON array into
        // EditRequest[] before this handler runs. Defensive null guard for SDK quirks.
        edits ??= Array.Empty<EditRequest>();

        // Log entry: edit count + file list, NOT new_text (D-17).
        var distinctFiles = edits.Select(e => e.File).Distinct().ToArray();
        logger.LogDebug("apply_edits_verified: {EditCount} edit(s) on files [{Files}]",
            edits.Length, string.Join(", ", distinctFiles));

        // D-12: no silent downgrade. "bind" is the only supported mode; null means default=bind.
        if (verify is not null && !string.Equals(verify, "bind", StringComparison.OrdinalIgnoreCase))
        {
            return Envelope<ApplyEditsVerifiedToolData>.Err(
                ToolError.UnsupportedOption(
                    param: "verify",
                    requested: verify,
                    supported: new[] { "bind" }));
        }

        // --- READ LEASE (ADR-0005 §D-06) ---
        // Acquired AFTER all cheap argument validation so failed-arg paths don't leak a lease.
        // Special case for apply_edits_verified: the lease MUST be released BEFORE the
        // CreateSnapshotAsync call below, because that call takes _writeLock — holding the
        // read lease while awaiting a writer lock deadlocks with a concurrent CloseAsync
        // (which holds _writeLock and waits on the reader gate). The captured editedSolution
        // is an immutable Roslyn snapshot that survives lease release.
        var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
        {
            lease.Dispose();
            return Envelope<ApplyEditsVerifiedToolData>.Err(ToolError.WorkspaceNotLoaded());
        }

        // ADR-0007 §1.2: if fromSnapshotId is supplied, the base solution for edit application
        // is that snapshot's Solution rather than CurrentSolution. The read lease still gates
        // CloseAsync; the snapshot itself is an immutable Roslyn pointer that survives lease
        // release. baseSolution is a separate local because Roslyn's out-parameter `solution`
        // cannot be reassigned (C# out semantics), and we still need `solution` (non-null) to
        // satisfy the lease lifecycle invariants captured above.
        var baseSolution = solution!;
        if (fromSnapshotId is not null)
        {
            if (!workspaceHost.TryGetSnapshot(fromSnapshotId, out var snapshotEntry) || snapshotEntry is null)
            {
                lease.Dispose();
                return Envelope<ApplyEditsVerifiedToolData>.Err(
                    ToolError.SnapshotNotFound(fromSnapshotId));
            }
            baseSolution = snapshotEntry.Snapshot;
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // --- VALIDATION PASS (D-02, D-03, D-04: no state change) ---

            // Validated edit carries the resolved DocumentId alongside the original request.
            var validated = new List<(DocumentId DocumentId, EditRequest Edit)>(edits.Length);

            // Group by file to check overlaps per file.
            var byFile = edits.GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase);
            foreach (var fileGroup in byFile)
            {
                // D-02: resolve file by suffix match against solution documents.
                var matches = baseSolution.Projects
                    .SelectMany(p => p.Documents)
                    .Where(d => d.FilePath != null &&
                                d.FilePath.EndsWith(fileGroup.Key, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 0)
                    return Envelope<ApplyEditsVerifiedToolData>.Err(
                        ToolError.InvalidArgument("edits.file", "not_found_in_workspace",
                            value: fileGroup.Key),
                        (int)sw.ElapsedMilliseconds);
                if (matches.Count > 1)
                    return Envelope<ApplyEditsVerifiedToolData>.Err(
                        ToolError.InvalidArgument("edits.file", "ambiguous_path",
                            value: fileGroup.Key,
                            message: $"ambiguous file path: {fileGroup.Key} matches {matches.Count} documents"),
                        (int)sw.ElapsedMilliseconds);

                var doc = matches[0];
                var text = await doc.GetTextAsync(ct);

                // D-03: sort by start, detect overlaps. End-exclusive: adjacent spans (a.End == b.Start)
                // do not overlap; zero-width insertions (start == end) at same position are non-overlapping.
                var sorted = fileGroup.OrderBy(e => e.Span.Start).ToArray();
                for (var i = 0; i < sorted.Length - 1; i++)
                {
                    if (sorted[i].Span.End > sorted[i + 1].Span.Start)
                        return Envelope<ApplyEditsVerifiedToolData>.Err(
                            ToolError.OverlappingEdits(fileGroup.Key, new[]
                            {
                                new Span(sorted[i].Span.Start, sorted[i].Span.End),
                                new Span(sorted[i + 1].Span.Start, sorted[i + 1].Span.End),
                            }),
                            (int)sw.ElapsedMilliseconds);
                }

                // D-04: validate each span — reject inverted/negative, check upper bound against doc length.
                foreach (var edit in sorted)
                {
                    if (edit.Span.Start < 0 || edit.Span.End < edit.Span.Start)
                        return Envelope<ApplyEditsVerifiedToolData>.Err(
                            ToolError.InvalidArgument("edits.span", "inverted_or_negative",
                                value: edit.Span),
                            (int)sw.ElapsedMilliseconds);
                    if (edit.Span.End > text.Length)
                        return Envelope<ApplyEditsVerifiedToolData>.Err(
                            ToolError.OutOfBounds(fileGroup.Key,
                                new Span(edit.Span.Start, edit.Span.End),
                                documentLength: text.Length),
                            (int)sw.ElapsedMilliseconds);

                    // Phase 14 D-05/D-06/D-08/D-09: the content check. Ordering is load-bearing —
                    // this MUST sit after the out_of_bounds check above, because
                    // text.ToString(TextSpan) throws ArgumentOutOfRangeException on a span past
                    // text.Length, which would surface as internal_error instead of the clean
                    // out_of_bounds that already exists. Skip entirely when the caller didn't
                    // opt in. `text` is this file group's original SourceText, fetched once
                    // above and never reassigned in this loop (ADR-0016) — comparing against it
                    // is what keeps the check in the caller's original coordinate system.
                    if (edit.ExpectedText is not null)
                    {
                        var actual = text.ToString(new TextSpan(edit.Span.Start, edit.Span.End - edit.Span.Start));
                        if (actual != edit.ExpectedText)
                        {
                            // Secondary, message-only signal (D-04): normalized equality does
                            // not imply unnormalized equality — we only reach here on
                            // inequality, so the two forms can never coincide unnormalized.
                            var normalizedActual = NormalizeLineEndings(actual);
                            var normalizedExpected = NormalizeLineEndings(edit.ExpectedText);
                            var differsOnlyByLineEndings = normalizedActual == normalizedExpected;

                            return Envelope<ApplyEditsVerifiedToolData>.Err(
                                ToolError.ExpectedTextMismatch(
                                    fileGroup.Key,
                                    new Span(edit.Span.Start, edit.Span.End),
                                    BoundarySnap.TruncateForDisplay(actual),
                                    differsOnlyByLineEndings),
                                (int)sw.ElapsedMilliseconds);
                        }
                    }

                    // Returning above happens before validated.Add, before baseline diagnostic
                    // collection, before the apply pass, and before CreateSnapshotAsync, so
                    // nothing stages and no snapshot is minted — the same shape out_of_bounds
                    // already has.
                    validated.Add((doc.Id, edit));
                }
            }

            // --- BASELINE COLLECTION (D-05, D-06, D-11, D-14: read-only, no lock) ---

            // Collect the set of touched project IDs and original document trees.
            var touchedProjectIds = validated
                .Select(v => v.DocumentId.ProjectId)
                .Distinct()
                .ToHashSet();

            // Baseline: (DiagnosticId, Message) pairs from touched documents only (D-06).
            var baseline = new HashSet<(string Id, string Message)>();
            // Map to quickly look up which tree ids belong to touched docs (pre-edit).
            var touchedDocIdSet = validated.Select(v => v.DocumentId).ToHashSet();

            foreach (var projectId in touchedProjectIds)
            {
                var project = baseSolution.GetProject(projectId)!;
                var compilation = await project.GetCompilationAsync(ct);
                if (compilation is null) continue;

                // Collect the syntax trees for touched documents in this project.
                var touchedTrees = new HashSet<SyntaxTree>();
                foreach (var docId in touchedDocIdSet.Where(id => id.ProjectId == projectId))
                {
                    var doc = baseSolution.GetDocument(docId)!;
                    var tree = await doc.GetSyntaxTreeAsync(ct);
                    if (tree != null) touchedTrees.Add(tree);
                }

                foreach (var d in compilation.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && !touchedTrees.Contains(d.Location.SourceTree)) continue;
                    baseline.Add((d.Id, d.GetMessage()));
                }
            }

            // --- APPLY PASS (D-04: in-memory, no disk writes, no TryApplyChanges) ---

            var editedSolution = baseSolution;
            // ADR-0016: group by document and apply each document's changes in ONE WithChanges
            // call. SourceText.WithChanges resolves every change in the batch against the text it
            // is called on, so batching keeps all spans in the caller's ORIGINAL coordinate
            // system. Applying them one at a time re-read the already-edited text and applied the
            // next edit at its original offset, shifting every later edit in the same file by the
            // preceding edits' length delta. Overlap is already rejected above (D-03), so the
            // batch cannot contain conflicting changes.
            foreach (var docGroup in validated.GroupBy(v => v.DocumentId))
            {
                var doc = editedSolution.GetDocument(docGroup.Key)!;
                var text = await doc.GetTextAsync(ct);
                // Convert end-exclusive {start, end} to Roslyn's {start, length}.
                var changes = docGroup
                    .Select(v => new TextChange(
                        new TextSpan(v.Edit.Span.Start, v.Edit.Span.End - v.Edit.Span.Start),
                        v.Edit.NewText))
                    .ToArray();
                editedSolution = editedSolution.WithDocumentText(docGroup.Key, text.WithChanges(changes));
            }

            // --- POST-EDIT DIAGNOSTIC COLLECTION (D-05, D-06, D-11, D-13, D-14) ---

            var netNewDiagnostics = new List<ApplyEditsDiagnostic>();
            foreach (var projectId in touchedProjectIds)
            {
                // Pitfall 1: get project + trees from editedSolution, not original (D-11).
                var editedProject = editedSolution.GetProject(projectId)!;
                var compilation = await editedProject.GetCompilationAsync(ct);
                if (compilation is null) continue;

                // Pitfall 2: fresh tree refs from editedSolution.
                var touchedTrees = new HashSet<SyntaxTree>();
                foreach (var docId in touchedDocIdSet.Where(id => id.ProjectId == projectId))
                {
                    var doc = editedSolution.GetDocument(docId)!;
                    var tree = await doc.GetSyntaxTreeAsync(ct);
                    if (tree != null) touchedTrees.Add(tree);
                }

                foreach (var d in compilation.GetDiagnostics(ct))
                {
                    if (d.Severity < DiagnosticSeverity.Warning) continue;
                    if (d.Location.SourceTree != null && !touchedTrees.Contains(d.Location.SourceTree)) continue;

                    // D-06: subtract baseline by (id, message) — position-independent.
                    if (baseline.Contains((d.Id, d.GetMessage()))) continue;

                    // D-05: emit span {start, end} alongside 1-based line/column.
                    var lineSpan = d.Location.GetLineSpan();
                    var src = d.Location.SourceSpan;
                    netNewDiagnostics.Add(new ApplyEditsDiagnostic(
                        Severity: d.Severity.ToString(),
                        Id: d.Id,
                        Message: d.GetMessage(),
                        File: lineSpan.Path,
                        Span: new Span(src.Start, src.End),
                        Line: lineSpan.StartLinePosition.Line + 1,
                        Column: lineSpan.StartLinePosition.Character + 1,
                        EndLine: lineSpan.EndLinePosition.Line + 1,
                        EndColumn: lineSpan.EndLinePosition.Character + 1));
                }
            }

            // --- MTIME CAPTURE (ADR-0007 §1.7) ---
            // Capture per-file disk mtimes for the documents we just touched. Keys are
            // absolute paths (Document.FilePath from the workspace's view); values are second-
            // granular mtimes that CommitSnapshotAsync re-checks against disk at promote time
            // to detect out-of-band edits (D-07). Files Roslyn knows about but that do not
            // exist on disk (in-memory-only docs) get DateTimeOffset.MinValue — the commit-time
            // check treats a now-missing file as a conflict.
            var fileMtimes = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
            foreach (var (docId, _) in validated)
            {
                var doc = baseSolution.GetDocument(docId)!;
                if (doc.FilePath is null) continue;
                if (fileMtimes.ContainsKey(doc.FilePath)) continue;
                fileMtimes[doc.FilePath] = File.Exists(doc.FilePath)
                    ? new DateTimeOffset(File.GetLastWriteTimeUtc(doc.FilePath), TimeSpan.Zero)
                    : DateTimeOffset.MinValue;
            }

            // --- SNAPSHOT STORE (D-07, D-08, D-10; ADR-0007 §1.1 — NO PROMOTE) ---
            // Release the read lease BEFORE CreateSnapshotAsync. CreateSnapshotAsync awaits
            // _writeLock; if we held the lease here, a concurrent CloseAsync (which itself
            // holds _writeLock and waits on the reader gate) would deadlock against us.
            // editedSolution is an immutable Roslyn snapshot and survives lease release.
            lease.Dispose();

            string snapshotId;
            try
            {
                snapshotId = await workspaceHost.CreateSnapshotAsync(
                    editedSolution,
                    parentSnapshotId: fromSnapshotId,
                    fileMtimes: fileMtimes,
                    ct);
            }
            catch (InvalidOperationException)
            {
                // Race: workspace was closed between our read phase and this snapshot mint.
                // Surface as workspace_not_loaded — the workspace the caller operated on is gone.
                return Envelope<ApplyEditsVerifiedToolData>.Err(ToolError.WorkspaceNotLoaded());
            }

            // --- RESPONSE (D-12) ---
            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var errorCount = netNewDiagnostics.Count(d => d.Severity == "Error");
            var summary = $"apply_edits: {edits.Length} edit(s), {errorCount} new error(s) in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            // D-13 extension: per-edit echo, truncated at cap. The universe (validated.Count
            // == edits.Length) is surfaced via envelope total_count; all edits were applied
            // atomically regardless of echo truncation.
            var editEchoes = validated
                .Take(cap)
                .Select(v => new ApplyEditsEcho(
                    File: v.Edit.File,
                    Span: new Span(v.Edit.Span.Start, v.Edit.Span.End),
                    NewTextLength: v.Edit.NewText.Length,
                    Applied: true))
                .ToList();

            var totalEdits = validated.Count;
            var truncated = totalEdits > editEchoes.Count;

            var data = new ApplyEditsVerifiedToolData(
                SnapshotId: snapshotId,
                FilesTouched: distinctFiles.Length,
                EditsApplied: edits.Length,
                Edits: editEchoes,
                Diagnostics: netNewDiagnostics);

            return Envelope<ApplyEditsVerifiedToolData>.Ok(
                data, elapsedMs, truncated: truncated, totalCount: totalEdits);
        }
        catch (OperationCanceledException)
        {
            throw; // let cancellation propagate; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during apply_edits_verified");
            return Envelope<ApplyEditsVerifiedToolData>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Idempotent — phase-2 disposes the lease before CreateSnapshotAsync; this catches
            // any early-error return (validation failures) that exited before phase 2 ran.
            lease.Dispose();
        }
    }

    /// <summary>
    /// Phase 14 D-04: a message-only signal, computed AFTER the ordinal mismatch comparison has
    /// already failed (D-03 — the comparison itself never normalizes). Replaces CRLF with LF
    /// first, then bare CR with LF, so all three line-ending styles collapse to one form before
    /// comparison.
    /// </summary>
    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');
}
