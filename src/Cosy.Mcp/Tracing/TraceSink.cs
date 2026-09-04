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

    /// <summary>
    /// This process's 8-char session discriminator (D-04) — null/empty when the sink is in
    /// no-op mode (unset prefix, or the resolved path could not be opened). The same value is
    /// the filename suffix (<see cref="ResolveSessionPath"/>) and the <c>session</c> key on
    /// every record this sink emits, so a test can assert filename/record agreement without
    /// recomputing the id.
    /// </summary>
    public string? SessionId { get; }

    public TraceSink(string? tracePath, ILogger<TraceSink>? logger = null)
    {
        if (string.IsNullOrEmpty(tracePath)) return; // TEL-02: opt-in.

        // D-04: tracePath is now a PREFIX, not a literal file. Each process resolves its own
        // file once, at construction, so no two processes ever hold a handle to the same path —
        // the cross-process tearing/overwrite defect (13-RESEARCH.md Pitfall 1) becomes
        // structurally impossible rather than merely less likely.
        var sessionId = Guid.NewGuid().ToString("N")[..8]; // same shape as WorkspaceHost.cs:319
        var resolvedPath = ResolveSessionPath(tracePath, sessionId);

        try
        {
            // ADR-0015: the operator names a prefix, not a prepared directory tree. A missing
            // parent chain is intent to be created, not an error — CreateDirectory builds the
            // whole chain and is a no-op when it already exists. Without this, FileMode.Append
            // throws DirectoryNotFoundException on the FIRST tools/call (the sink is a lazy DI
            // singleton, so the throw lands inside a tool dispatch, not at startup).
            var parent = Path.GetDirectoryName(Path.GetFullPath(resolvedPath));
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // ADR-0007 §2.6: WriteThrough bypasses the OS write buffer; AutoFlush bypasses the
            // StreamWriter user-space buffer. Together they give immediate-disk-handoff semantics
            // without paying for per-record fsync (which would add 5–15 ms on spinning disks).
            var stream = new FileStream(
                resolvedPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,           // allow external readers (logrotate, lk one-liners)
                bufferSize: 4096,
                FileOptions.WriteThrough);
            _writer = new StreamWriter(stream, leaveOpen: false) { AutoFlush = true };
            SessionId = sessionId;
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
                "COSY_TRACE_PATH prefix '{TracePathPrefix}' could not be opened for append; tracing is off for this session.",
                tracePath);
        }
    }

    /// <summary>
    /// D-04: resolves this process's own per-session file from the operator-supplied prefix.
    /// Two legacy value shapes are handled so an existing operator configuration degrades to a
    /// sane name rather than an unusable one (13-RESEARCH.md Assumptions Log A2):
    /// <list type="bullet">
    /// <item>a prefix ending in a directory separator gets the default basename
    /// <c>cosy-trace</c> appended, so the result is never a dot-prefixed hidden file — that
    /// would read to the operator as "tracing is off" (ADR-0015's whole point is to make an
    /// unusable path loud, not silently look empty);</item>
    /// <item>a prefix already ending in <c>.jsonl</c> (the literal value the operator's ambient
    /// shell and <c>.mcp.json</c> both hold today) has that suffix stripped first, so the
    /// result carries exactly one <c>.jsonl</c>, not a doubled extension.</item>
    /// </list>
    /// </summary>
    private static string ResolveSessionPath(string prefix, string sessionId)
    {
        if (prefix.EndsWith(Path.DirectorySeparatorChar) || prefix.EndsWith(Path.AltDirectorySeparatorChar))
            prefix += "cosy-trace";
        if (prefix.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            prefix = prefix[..^".jsonl".Length];
        return $"{prefix}.{sessionId}.jsonl";
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

        // D-06/D-12: every free-text argument is shaped to a length (never written verbatim)
        // before it reaches TraceRecord.Args -- TraceArgShaping.Shape is the ONE point where
        // that invariant is either true or false, so this replaces the old verbatim boxing
        // rather than supplementing it. The values Shape keeps verbatim (identity args like
        // `symbol`/`file`) are still raw JsonElements internally, so the STJ pitfall the old
        // comment named still applies to them: serializing IReadOnlyDictionary<string, JsonElement>
        // directly can produce a wrapper object instead of inline values, so each verbatim value
        // stays boxed to object inside Shape's returned Dictionary<string, object?> rather than
        // being re-typed.
        object? argsPayload = TraceArgShaping.Shape(args, toolName);

        var record = new TraceRecord
        {
            Timestamp = ts,
            Tool = toolName,
            ElapsedMs = elapsedMs,
            IsError = isError,
            ErrorKind = errorKind,
            ResolvedSymbol = resolvedSymbol,
            Args = argsPayload,
            Session = SessionId,
        };

        var line = JsonSerializer.Serialize(record, _jsonOptions);
        // Single writer, single line + newline. WriteLine handles the platform EOL — fine for
        // JSONL (line-delimited, not byte-delimited).
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    /// <summary>
    /// Emit one JSONL record for a dispatch that threw instead of returning an envelope
    /// (ADR-0007 §2.7). There is no <see cref="CallToolResult"/> to parse in this case, so
    /// <c>is_error</c> is set directly and <c>error_kind</c> comes from the exception.
    /// No-op when the sink was constructed with a null/empty path.
    /// </summary>
    /// <remarks>
    /// <para><b>error_kind here is deliberately outside the ADR-0004 twelve-kind envelope
    /// taxonomy.</b> Those kinds describe faults a tool <i>reported</i>; these describe a
    /// dispatch that never got as far as reporting one. Folding them into
    /// <c>internal_error</c> would make a handled internal error and an unhandled throw
    /// indistinguishable in the trace, which is the distinction this method exists to record.
    /// The trace's <c>error_kind</c> is therefore a superset of the envelope's.</para>
    /// <para><b>On <c>elapsed_ms</c>:</b> for a cancellation this is time-to-abort, not
    /// time-to-serve. It is still a true measurement of the dispatch and is recorded, but any
    /// consumer computing latency must filter on <c>error_kind</c> first — mixing the two
    /// silently biases the result. See the Phase 7 latency todo.</para>
    /// </remarks>
    public void EmitFault(string toolName, DateTimeOffset ts, long elapsedMs,
                          Exception ex, IDictionary<string, JsonElement>? args)
    {
        if (_writer is null) return; // no-op when sink unconfigured (TEL-02).

        // Cancellation is a different story from a handler fault and is kept distinct: one is
        // the client or the deadline withdrawing the request, the other is a defect.
        var errorKind = ex is OperationCanceledException
            ? "dispatch_canceled"
            : "unhandled_exception";

        object? argsPayload = args is null
            ? null
            : args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        var record = new TraceRecord
        {
            Timestamp = ts,
            Tool = toolName,
            ElapsedMs = elapsedMs,
            IsError = true,
            ErrorKind = errorKind,
            // Type name only, never ex.Message or the stack: a message can carry a file path,
            // a symbol name, or a fragment of source, and D-10's "no redaction" applies to args
            // the agent already chose to send — not to text the process happened to produce.
            ExceptionType = ex.GetType().FullName,
            // D-04: carried on EVERY record, fault path included. Omitting it here would make the
            // faults — the population this sink exists to surface — the one kind of record that
            // cannot be attributed to a session in a concatenated corpus.
            Session = SessionId,
            Args = argsPayload,
        };

        var line = JsonSerializer.Serialize(record, _jsonOptions);
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose() => _writer?.Dispose();
}
