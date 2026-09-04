using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Cosy.Mcp.Tracing;

/// <summary>
/// The traced-dispatch body used by the SDK call-tool filter (ADR-0007 §2.5, §2.7).
///
/// This lives here rather than inline in Program.cs for one reason: the guarantee it provides —
/// "a record is written whether the dispatch returns or throws" — was previously an assertion in
/// a comment with no test behind it, and the code did not actually provide it. A top-level lambda
/// inside <c>WithRequestFilters</c> cannot be unit-tested without standing up a server, so the
/// claim went unchecked from Phase 9 until 2026-09-03. The signature deliberately takes a bare
/// <c>Func&lt;ValueTask&lt;CallToolResult&gt;&gt;</c> instead of the SDK's filter delegate, so a
/// test can hand it a continuation that throws.
/// </summary>
public static class TraceDispatch
{
    /// <summary>
    /// Invoke <paramref name="next"/>, emit exactly one trace record for the outcome, and
    /// propagate the result or the original exception unchanged.
    /// </summary>
    public static async ValueTask<CallToolResult> InvokeAsync(
        TraceSink sink,
        string toolName,
        IDictionary<string, JsonElement>? args,
        Func<ValueTask<CallToolResult>> next)
    {
        var sw = Stopwatch.StartNew();
        var ts = DateTimeOffset.UtcNow;

        try
        {
            var result = await next();
            sw.Stop();
            sink.Emit(toolName, ts, sw.ElapsedMilliseconds, result, args);
            return result;
        }
        catch (Exception ex)
        {
            // Record, then rethrow unchanged. Tracing observes the dispatch; it does not
            // participate in it. `throw;` (not `throw ex;`) preserves the original stack.
            sw.Stop();
            try
            {
                sink.EmitFault(toolName, ts, sw.ElapsedMilliseconds, ex, args);
            }
            catch (IOException)
            {
                // Sink unwritable (disk full, handle revoked). Losing one trace record is
                // strictly better than masking the fault the caller needs to see, so this is
                // swallowed and the original exception continues to propagate.
            }
            throw;
        }
    }
}
