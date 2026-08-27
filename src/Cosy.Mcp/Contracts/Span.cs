using System.Text.Json.Serialization;

namespace Cosy.Mcp.Contracts;

/// <summary>
/// Canonical zero-based char-offset span, <c>end</c>-exclusive (ADR-0004 §3, D-04).
/// Matches LSP convention so <c>source.Substring(start, end - start)</c> needs no arithmetic.
/// Zero-width insertions are <c>start == end</c>.
/// Replaces the legacy <c>{start, length}</c> shape on the wire.
/// </summary>
public sealed record Span(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")]   int End);
