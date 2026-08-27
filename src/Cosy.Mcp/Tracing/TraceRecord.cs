using System.Text.Json.Serialization;

namespace Cosy.Mcp.Tracing;

/// <summary>
/// One JSONL record emitted per tools/call dispatch (ADR-0007 §2.1).
/// Serialized via System.Text.Json with snake_case property names; absent fields use
/// JsonIgnore(WhenWritingNull) so the line stays small for non-error / non-symbol cases.
/// </summary>
public sealed record TraceRecord
{
    [JsonPropertyName("ts")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("tool")]
    public string Tool { get; init; } = string.Empty;

    [JsonPropertyName("elapsed_ms")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    [JsonPropertyName("error_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorKind { get; init; }

    [JsonPropertyName("resolved_symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraceResolvedSymbol? ResolvedSymbol { get; init; }

    [JsonPropertyName("args")]
    public object? Args { get; init; }
}

/// <summary>
/// Subset of <see cref="Cosy.Mcp.Contracts.ResolvedSymbol"/> echoed verbatim into the JSONL record
/// (ADR-0007 §2.1). Kept as its own type so the trace schema is decoupled from the envelope
/// schema — changing one does not silently change the other.
/// </summary>
public sealed record TraceResolvedSymbol(
    [property: JsonPropertyName("doc_id")] string? DocId,
    [property: JsonPropertyName("display_name")] string DisplayName);
