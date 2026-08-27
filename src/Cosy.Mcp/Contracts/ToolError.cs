using System.Text.Json.Serialization;

namespace Cosy.Mcp.Contracts;

/// <summary>
/// Closed enum of tool-error kinds on the wire (ADR-0004 §4, D-06; extended by ADR-0007 §3.2,
/// D-14, D-15; extended by Phase 12 D-03/D-04). The const strings ARE the wire values — do not
/// rename casually. Exactly these 13 strings ever appear as <see cref="ToolError.Kind"/>.
/// </summary>
public static class ToolErrorKind
{
    public const string WorkspaceNotLoaded = "workspace_not_loaded";
    public const string InvalidArgument    = "invalid_argument";
    public const string UnsupportedOption  = "unsupported_option";
    public const string SymbolNotFound     = "symbol_not_found";
    public const string AmbiguousSymbol    = "ambiguous_symbol";
    public const string OutOfBounds        = "out_of_bounds";
    public const string OverlappingEdits   = "overlapping_edits";
    public const string InternalError      = "internal_error";

    // Phase 9 additions (ADR-0007 §3.2, D-14, D-15) — total 12 kinds after this change.
    public const string TestRunnerFailed = "test_runner_failed";
    public const string BuildFailed      = "build_failed";
    public const string SnapshotNotFound = "snapshot_not_found";
    public const string DiskConflict     = "disk_conflict";

    // Phase 12 addition (D-03/D-04) — 13th kind. A symbol resolved successfully but has no
    // syntax references at all (metadata-only, or IsImplicitlyDeclared) -- deliberately
    // distinct from symbol_not_found, because the resolve succeeded and the source did not exist.
    public const string NoSourceAvailable = "no_source_available";
}

/// <summary>
/// Discriminated error union (ADR-0004 §4). Wire shape is <c>{kind, message, details}</c>;
/// <c>details</c> schema is fixed per kind and is enforced by the static constructors below —
/// callers should always use the named factories rather than <c>new ToolError(...)</c> directly
/// so the details record-type matches the kind.
/// </summary>
public sealed record ToolError(
    [property: JsonPropertyName("kind")]    string Kind,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("details")] object Details)
{
    public static ToolError WorkspaceNotLoaded(string message = "workspace not loaded; call workspace_open first")
        => new(ToolErrorKind.WorkspaceNotLoaded, message, new { });

    public static ToolError InvalidArgument(string param, string reason, object? value = null, string? message = null)
        => new(ToolErrorKind.InvalidArgument, message ?? $"{param}: {reason}",
               new InvalidArgumentDetails(param, reason, value));

    public static ToolError UnsupportedOption(string param, string requested, IReadOnlyList<string> supported)
        => new(ToolErrorKind.UnsupportedOption, $"{param}='{requested}' not supported",
               new UnsupportedOptionDetails(param, requested, supported));

    public static ToolError SymbolNotFound(string query, IReadOnlyList<SymbolCandidateDto> nearMisses)
        => new(ToolErrorKind.SymbolNotFound, $"symbol not found: {query}",
               new SymbolNotFoundDetails(query, nearMisses));

    public static ToolError AmbiguousSymbol(string query, IReadOnlyList<SymbolCandidateDto> candidates)
        => new(ToolErrorKind.AmbiguousSymbol, $"multiple candidates above threshold ({candidates.Count})",
               new AmbiguousSymbolDetails(query, candidates, candidates.Count));

    public static ToolError OutOfBounds(string file, Span span, int documentLength)
        => new(ToolErrorKind.OutOfBounds, $"span out of bounds in file {file}",
               new OutOfBoundsDetails(file, span, documentLength));

    public static ToolError OverlappingEdits(string file, IReadOnlyList<Span> spans)
        => new(ToolErrorKind.OverlappingEdits, $"overlapping edits in file {file}",
               new OverlappingEditsDetails(file, spans));

    // D-07: no stack traces — logging captures those on stderr. Exception message is still shared.
    public static ToolError Internal(Exception ex)
        => new(ToolErrorKind.InternalError, ex.Message,
               new InternalErrorDetails(ex.GetType().FullName ?? "System.Exception", ex.Message));

    // Phase 9 factories (ADR-0007 §3.2, D-14, D-15). Each new kind has a typed details record.

    public static ToolError TestRunnerFailed(string reason, int? exitCode = null,
        string? stderrTail = null, int? timeoutMs = null,
        TestRunnerPartialResults? partialResults = null)
        => new(ToolErrorKind.TestRunnerFailed, $"test runner failed: {reason}",
               new TestRunnerFailedDetails(reason, exitCode, stderrTail, timeoutMs, partialResults));

    public static ToolError BuildFailed(string project, string message,
        IReadOnlyList<BuildDiagnostic> diagnostics)
        => new(ToolErrorKind.BuildFailed, $"build failed: {message}",
               new BuildFailedDetails(project, message, diagnostics));

    public static ToolError SnapshotNotFound(string snapshotId)
        => new(ToolErrorKind.SnapshotNotFound, $"snapshot not found: {snapshotId}",
               new SnapshotNotFoundDetails(snapshotId));

    public static ToolError DiskConflict(string path,
        DateTimeOffset expectedMtime, DateTimeOffset actualMtime)
        => new(ToolErrorKind.DiskConflict,
               $"file modified on disk since snapshot was created: {path}",
               new DiskConflictDetails(path, expectedMtime, actualMtime));

    // Phase 12 factory (D-03). metadataAssembly stays nullable — not every source-free symbol
    // has a containing assembly (e.g. the `dynamic` pseudo-type, the merged global namespace).
    public static ToolError NoSourceAvailable(string symbolId, string? metadataAssembly)
        => new(ToolErrorKind.NoSourceAvailable, $"no source available for symbol: {symbolId}",
               new NoSourceAvailableDetails(symbolId, metadataAssembly));
}

// --- Per-kind details records (ADR-0004 §4 table — SHAPE FIXED). ---

public sealed record InvalidArgumentDetails(
    [property: JsonPropertyName("param")]  string Param,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("value")]  object? Value);

public sealed record UnsupportedOptionDetails(
    [property: JsonPropertyName("param")]     string Param,
    [property: JsonPropertyName("requested")] string Requested,
    [property: JsonPropertyName("supported")] IReadOnlyList<string> Supported);

/// <summary>
/// Wire-level candidate DTO used in symbol_not_found.near_misses and ambiguous_symbol.candidates
/// (ADR-0004 §8). Distinct from <c>Symbols.SymbolCandidate</c> which carries an <c>ISymbol</c>
/// reference not serializable to JSON. Tool handlers project symbol candidates to this DTO.
/// </summary>
public sealed record SymbolCandidateDto(
    [property: JsonPropertyName("doc_id")]               string? DocId,
    [property: JsonPropertyName("display_name")]         string DisplayName,
    [property: JsonPropertyName("score")]                double Score,
    [property: JsonPropertyName("match_reason")]         string MatchReason,
    [property: JsonPropertyName("containing_symbol_id")] string? ContainingSymbolId);

public sealed record SymbolNotFoundDetails(
    [property: JsonPropertyName("query")]       string Query,
    [property: JsonPropertyName("near_misses")] IReadOnlyList<SymbolCandidateDto> NearMisses);

public sealed record AmbiguousSymbolDetails(
    [property: JsonPropertyName("query")]      string Query,
    [property: JsonPropertyName("candidates")] IReadOnlyList<SymbolCandidateDto> Candidates,
    [property: JsonPropertyName("top_n")]      int TopN);

public sealed record OutOfBoundsDetails(
    [property: JsonPropertyName("file")]            string File,
    [property: JsonPropertyName("span")]            Span Span,
    [property: JsonPropertyName("document_length")] int DocumentLength);

public sealed record OverlappingEditsDetails(
    [property: JsonPropertyName("file")]  string File,
    [property: JsonPropertyName("spans")] IReadOnlyList<Span> Spans);

public sealed record InternalErrorDetails(
    [property: JsonPropertyName("exception_type")] string ExceptionType,
    [property: JsonPropertyName("message")]        string Message);

// --- Phase 9 details records (ADR-0007 §3.2). Match the on-the-wire schemas verbatim. ---

public sealed record TestRunnerFailedDetails(
    [property: JsonPropertyName("reason")]          string Reason,
    [property: JsonPropertyName("exit_code")]       int? ExitCode,
    [property: JsonPropertyName("stderr_tail")]     string? StderrTail,
    [property: JsonPropertyName("timeout_ms")]      int? TimeoutMs,
    [property: JsonPropertyName("partial_results")] TestRunnerPartialResults? PartialResults);

public sealed record TestRunnerPartialResults(
    [property: JsonPropertyName("passed_so_far")]  int PassedSoFar,
    [property: JsonPropertyName("failed_so_far")]  int FailedSoFar,
    [property: JsonPropertyName("last_test_name")] string LastTestName);

public sealed record BuildFailedDetails(
    [property: JsonPropertyName("project")]     string Project,
    [property: JsonPropertyName("message")]     string Message,
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<BuildDiagnostic> Diagnostics);

public sealed record BuildDiagnostic(
    [property: JsonPropertyName("id")]      string? Id,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("file")]    string? File,
    [property: JsonPropertyName("line")]    int? Line);

public sealed record SnapshotNotFoundDetails(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId);

public sealed record DiskConflictDetails(
    [property: JsonPropertyName("path")]           string Path,
    [property: JsonPropertyName("expected_mtime")] DateTimeOffset ExpectedMtime,
    [property: JsonPropertyName("actual_mtime")]   DateTimeOffset ActualMtime);

// --- Phase 12 details record (D-03). ---

public sealed record NoSourceAvailableDetails(
    [property: JsonPropertyName("symbol_id")]         string SymbolId,
    [property: JsonPropertyName("metadata_assembly")] string? MetadataAssembly);
