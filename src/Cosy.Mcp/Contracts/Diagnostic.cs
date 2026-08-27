using System.Text.Json.Serialization;

namespace Cosy.Mcp.Contracts;

/// <summary>
/// Canonical Diagnostic DTO (ADR-0004 §3, D-05). Replaces the ad-hoc diagnostic shapes
/// currently emitted by compile_check, apply_edits_verified, and rename.
/// Carries both a 0-based offset <see cref="Span"/> AND adjacent 1-based line/column
/// fields so consumers can pick whichever representation they prefer — both are always
/// kept consistent.
/// </summary>
public sealed record Diagnostic(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("message")]    string Message,
    [property: JsonPropertyName("file")]       string? File,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);
