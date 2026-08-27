using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Cosy.Mcp.Tracing;

/// <summary>
/// Process-singleton JSONL writer (ADR-0007 §2.2, §2.6). Opens the file once at construction
/// when <c>COSY_TRACE_PATH</c> is set (TEL-02 opt-in semantic — null/empty path → no file
/// handle, <see cref="Emit"/> is a no-op). Uses <see cref="FileOptions.WriteThrough"/> +
/// <see cref="StreamWriter.AutoFlush"/> for the "no record lost on process crash short of kernel
/// panic" guarantee without paying for per-record fsync (RESEARCH.md §5).
///
/// Single-writer-per-process invariant (D-09): Cosy.Mcp is one process per stdio session;
/// operator manages external rotation via logrotate or similar.
/// </summary>
public sealed class TraceSink : IDisposable
{
    // null when path unset → no-op mode (TEL-02 zero-overhead).
    private readonly StreamWriter? _writer;
    // Lock guards _writer.WriteLine — the MCP filter pipeline can dispatch tools/call concurrently
    // (subject to the SDK's own handler scheduling); a single shared StreamWriter is not
    // thread-safe across writers. Cheap monitor; contention only when trace is enabled.
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // No indentation — JSONL is one record per line, compact.
    };

    public TraceSink(string? tracePath, ILogger<TraceSink>? logger = null)
    {
        if (string.IsNullOrEmpty(tracePath)) return; // TEL-02: opt-in.

        try
        {
            // ADR-0015: the operator names a file, not a prepared directory tree. A missing
            // parent chain is intent to be created, not an error — CreateDirectory builds the
            // whole chain and is a no-op when it already exists. Without this, FileMode.Append
            // throws DirectoryNotFoundException on the FIRST tools/call (the sink is a lazy DI
            // singleton, so the throw lands inside a tool dispatch, not at startup).
            var parent = Path.GetDirectoryName(Path.GetFullPath(tracePath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // ADR-0007 §2.6: WriteThrough bypasses the OS write buffer; AutoFlush bypasses the
            // StreamWriter user-space buffer. Together they give immediate-disk-handoff semantics
            // without paying for per-record fsync (which would add 5–15 ms on spinning disks).
            var stream = new FileStream(
                tracePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,           // allow external readers (logrotate, lk one-liners)
                bufferSize: 4096,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(stream, leaveOpen: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ArgumentException)
        {
            // ADR-0015: telemetry is a side channel (ADR-0007 §2.4). A path that cannot be made
            // writable degrades to the same no-op mode as an unset variable rather than failing
            // the tool call that happened to trigger construction. Logged at Warning so the
            // operator is not left believing tracing is on — ILogger goes to stderr
            // (Program.cs §4), which is the only channel safe to write on stdio transport.
            _writer = null;
            logger?.LogWarning(ex,
                "COSY_TRACE_PATH '{TracePath}' could not be opened for append; tracing is off for this session.",
                tracePath);
        }
    }

    /// <summary>
    /// Emit one JSONL record. Parses the Cosy envelope from <c>result.Content[0].Text</c> to
    /// extract <c>is_error</c>, <c>error.kind</c>, and <c>resolved_symbol</c> — these are NOT on
    /// the MCP-layer <see cref="CallToolResult.IsError"/> (RESEARCH.md §1 Pitfall 1).
    /// No-op when the sink was constructed with a null/empty path.
    /// </summary>
    /// <remarks>
    /// The <paramref name="args"/> parameter type matches the SDK's
    /// <see cref="CallToolRequestParams.Arguments"/> shape (<c>IDictionary&lt;string, JsonElement&gt;?</c>);
    /// it is NOT mutated — accepted as <see cref="IDictionary{TKey, TValue}"/> only to match the
    /// SDK wire type without forcing a copy at every call.
    /// </remarks>
    public void Emit(string toolName, DateTimeOffset ts, long elapsedMs,
                     CallToolResult result, IDictionary<string, JsonElement>? args)
    {
        if (_writer is null) return; // no-op when sink unconfigured (TEL-02).

        // Parse Cosy envelope from Content[0].Text — Pitfall 1 from RESEARCH.md §1.
        // Defensive: malformed/missing content yields default fields rather than dropping the record.
        bool isError = false;
        string? errorKind = null;
        TraceResolvedSymbol? resolvedSymbol = null;
        try
        {
            if (result.Content?.Count > 0 && result.Content[0] is TextContentBlock tb && tb.Text is not null)
            {
                using var doc = JsonDocument.Parse(tb.Text);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True)
                        isError = true;
                    if (root.TryGetProperty("error", out var err)
                        && err.ValueKind == JsonValueKind.Object
                        && err.TryGetProperty("kind", out var k)
                        && k.ValueKind == JsonValueKind.String)
                        errorKind = k.GetString();
                    if (root.TryGetProperty("resolved_symbol", out var rs)
                        && rs.ValueKind == JsonValueKind.Object)
                    {
                        string? docId = rs.TryGetProperty("doc_id", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                        string displayName = rs.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(displayName))
                            resolvedSymbol = new TraceResolvedSymbol(docId, displayName);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Unexpected SDK content shape — leave fields default. Don't drop the record.
        }

        // STJ pitfall: serializing IReadOnlyDictionary<string, JsonElement> directly can produce
        // a wrapper object instead of inline values. Boxing each value to object lets STJ
        // recognize JsonElement and write it verbatim — matches D-10 "no redaction" (the args
        // dictionary serializes byte-for-byte equivalent to what the agent sent).
        object? argsPayload = args is null
            ? null
            : args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        var record = new TraceRecord
        {
            Timestamp = ts,
            Tool = toolName,
            ElapsedMs = elapsedMs,
            IsError = isError,
            ErrorKind = errorKind,
            ResolvedSymbol = resolvedSymbol,
            Args = argsPayload,
        };

        var line = JsonSerializer.Serialize(record, _jsonOptions);
        // Single writer, single line + newline. WriteLine handles the platform EOL — fine for
        // JSONL (line-delimited, not byte-delimited).
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer?.Dispose();
}
