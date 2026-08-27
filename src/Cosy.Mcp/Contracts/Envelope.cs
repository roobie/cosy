using System.Text.Json.Serialization;

namespace Cosy.Mcp.Contracts;

/// <summary>
/// Unified tool-response envelope (ADR-0004 §1, D-01). Every Cosy tool returns
/// <c>Envelope&lt;TData&gt;</c>. Optional fields use
/// <c>[JsonIgnore(Condition = WhenWritingNull)]</c> so they are omitted when absent —
/// the envelope shape stays small for success paths that don't need them.
///
/// Construction rules:
/// - <see cref="IsError"/> is <c>true</c> iff <see cref="Error"/> is non-null (the two move together).
/// - <see cref="ResolvedSymbol"/> is present iff the tool accepted a <c>symbol</c> parameter and resolution succeeded.
/// - <see cref="Truncated"/> + <see cref="TotalCount"/> are present iff the tool returns a list payload.
/// - <see cref="TruncatedText"/> is present iff the tool returns text (ADR-0004 §1 amendment, Phase 12 D-12) —
///   a sibling conditional-shared key to the list-payload pair above, covering the character universe
///   rather than the item universe.
/// </summary>
public sealed record Envelope<TData>
{
    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }

    [JsonPropertyName("elapsed_ms")]
    public int ElapsedMs { get; init; }

    [JsonPropertyName("resolved_symbol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResolvedSymbol? ResolvedSymbol { get; init; }

    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Truncated { get; init; }

    [JsonPropertyName("total_count")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalCount { get; init; }

    [JsonPropertyName("truncated_text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? TruncatedText { get; init; }

    [JsonPropertyName("data")]
    public TData? Data { get; init; }

    /// <summary>Success factory. Pass list-related optional fields only for list-returning tools.
    /// Pass <paramref name="truncatedText"/> only for text-returning tools (ADR-0004 §1 amendment).</summary>
    public static Envelope<TData> Ok(TData data, int elapsedMs, ResolvedSymbol? resolvedSymbol = null,
                                     bool? truncated = null, int? totalCount = null, bool? truncatedText = null)
        => new()
        {
            IsError = false,
            Data = data,
            ElapsedMs = elapsedMs,
            ResolvedSymbol = resolvedSymbol,
            Truncated = truncated,
            TotalCount = totalCount,
            TruncatedText = truncatedText,
        };

    /// <summary>Failure factory. <paramref name="elapsedMs"/> may be 0 if the handler erred before timing started.</summary>
    public static Envelope<TData> Err(ToolError error, int elapsedMs = 0)
        => new() { IsError = true, Error = error, ElapsedMs = elapsedMs };
}

/// <summary>Envelope-level echo of a resolved symbol (ADR-0004 §8, D-10).</summary>
public sealed record ResolvedSymbol(
    [property: JsonPropertyName("doc_id")]       string? DocId,
    [property: JsonPropertyName("display_name")] string DisplayName);
