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

    /// <summary>
    /// The envelope's <c>error.kind</c> for a dispatch that returned, or one of the two
    /// dispatch-level kinds (<c>unhandled_exception</c>, <c>dispatch_canceled</c>) for one that
    /// threw. This field is a SUPERSET of the ADR-0004 twelve-kind taxonomy by design — see
    /// <see cref="TraceSink.EmitFault"/> for why collapsing them would lose the distinction.
    /// </summary>
    [JsonPropertyName("error_kind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorKind { get; init; }

    /// <summary>
    /// CLR type name of the exception, present only on records written by
    /// <see cref="TraceSink.EmitFault"/>. Type only — never the message or stack (see that
    /// method's remarks).
    /// </summary>
    [JsonPropertyName("exception_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("resolved_symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraceResolvedSymbol? ResolvedSymbol { get; init; }

    [JsonPropertyName("args")]
    public object? Args { get; init; }

    /// <summary>
    /// D-04/SC-1: the 8-char session discriminator that also names this record's file
    /// (<c>{prefix}.{session}.jsonl</c>). Carried on every record — not just derivable from the
    /// filename — so a concatenated or renamed corpus is still session-countable.
    /// </summary>
    [JsonPropertyName("session")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Session { get; init; }
}

/// <summary>
/// Subset of <see cref="Cosy.Mcp.Contracts.ResolvedSymbol"/> echoed verbatim into the JSONL record
/// (ADR-0007 §2.1). Kept as its own type so the trace schema is decoupled from the envelope
/// schema — changing one does not silently change the other.
/// </summary>
public sealed record TraceResolvedSymbol(
    [property: JsonPropertyName("doc_id")] string? DocId,
    [property: JsonPropertyName("display_name")] string DisplayName);
