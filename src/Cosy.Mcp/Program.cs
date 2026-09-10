using System.Diagnostics;
using Cosy.Mcp;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Cli;
using Cosy.Mcp.Symbols;
using Cosy.Mcp.Tracing;
using Cosy.Mcp.Workspace;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// 0. CLI subcommand dispatch — MUST precede statement 1 (MSBuildLocator.RegisterDefaults()
//    has no business running for a JSON decision) and statement 2 (Console.SetOut neuters
//    stdout, and these subcommands exist to write stdout). No-args falls through to the
//    stdio server unchanged, so existing .mcp.json files keep working (Phase 11 D-16/D-20).
if (CliDispatch.TryRun(args, out var cliExitCode)) return cliExitCode;

// 1. MSBuild resolver hook MUST be the absolute first executable statement.
//    Any Roslyn or MSBuild type load before this causes silent MEF failures
//    (PITFALLS.md §1). The resolver cannot be retrofitted after a type loads.
//    The resolved instance is captured (not just discarded) so workspace_open can
//    report which SDK is doing the evaluation — ADR-0011 §Diagnostics.
MsBuildRegistration.Capture(MSBuildLocator.RegisterDefaults());

// 2. Neuter stdout. Belt-and-braces: MCP stdio uses stdout for JSON-RPC
//    framing; any stray Console.Write from transitive code corrupts the
//    protocol (PITFALLS.md §6). stderr is unaffected.
Console.SetOut(TextWriter.Null);

// 3. Build the generic host (DI container, logging, MCP wiring).
var builder = Host.CreateApplicationBuilder(args);

// 4. Route ALL ILogger output to stderr so stdout stays pure JSON-RPC.
builder.Logging.AddConsole(opts =>
    opts.LogToStandardErrorThreshold = LogLevel.Trace);

// 5. Register WorkspaceHost as a DI singleton BEFORE the MCP server so the tool
//    method's IWorkspaceHost parameter resolves (STACK.md §Idiomatic Pattern).
//    The MSBuildWorkspace inside is created lazily on first LoadAsync — ctor is cheap.
builder.Services.AddSingleton<IWorkspaceHost, WorkspaceHost>();

// 5b. FuzzySymbolResolver: stateless, shared across tool invocations (Phase 6 D-13).
//     Rule 3: registered here in Plan 01 so RenameTests can exercise the retrofit
//     end-to-end; Plan 05 will re-confirm placement when new navigation tools land.
builder.Services.AddSingleton<FuzzySymbolResolver>();

// 5c. TraceSink: opt-in via COSY_TRACE_PATH env var (ADR-0007 §2, TEL-02).
//     Null/empty path → Emit is a no-op (zero overhead when disabled).
//     Read once into a local: statement 6 needs the same value to decide whether the
//     initialize instructions carry the "tracing is off" hint, and two reads of a mutable
//     environment could disagree.
//     The logger is passed so an unusable path degrades to no-op with a stderr warning
//     rather than silently (ADR-0015).
var tracePath = Environment.GetEnvironmentVariable("COSY_TRACE_PATH");
builder.Services.AddSingleton(sp => new TraceSink(tracePath, sp.GetService<ILogger<TraceSink>>()));

// 6. Register MCP server; attribute discovery picks up [McpServerToolType] classes.
//    WithToolsFromAssembly now sees WorkspaceOpenTool, so tools/list is populated
//    automatically — the Phase 1 empty-list stub is no longer needed.
//
//    AddCallToolFilter wraps every tools/call dispatch with tracing (ADR-0007 §2,
//    TEL-03). Resolves sink via context.Services; one record emitted per call.
//
//    The emit is in a catch, NOT only on the return path. Registration-time coverage
//    (every tool goes through the filter) is not the same as execution-time coverage:
//    a handler that throws propagates straight through an unguarded `await next(...)`
//    and the record is never written. That made the trace a success-only log, blind to
//    exactly the crash and cancellation population it would be consulted about
//    (ADR-0007 §2.7, amended 2026-09-03).
builder.Services
    .AddMcpServer(options => options.ServerInstructions = ServerInstructionsText.Build(tracePath))
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithRequestFilters(filters => filters
        .AddCallToolFilter(next => async (context, ct) =>
        {
            var toolName = context.Params?.Name ?? "(unknown)";
            // SDK exposes Arguments as IDictionary<string, JsonElement>?; Emit accepts
            // the same type to avoid an unnecessary copy on a hot path. Sink does not mutate.
            var args = context.Params?.Arguments;

            // ADR-0019. Two placement decisions, both load-bearing:
            //  - INSIDE the traced dispatch, so a repaired argument fault is traced as the
            //    invalid_argument it now is rather than as the unhandled_exception it used to be.
            //  - OUTSIDE the sink null-check below, so the envelope a caller receives never depends
            //    on whether COSY_TRACE_PATH happens to be configured.
            var schema = ArgumentGuard.SchemaOf(context.MatchedPrimitive as McpServerTool);
            ValueTask<CallToolResult> Guarded() => ArgumentGuard.InvokeAsync(schema, args, () => next(context, ct));

            var sink = context.Services?.GetService<TraceSink>();
            if (sink is null) return await Guarded();

            // Body lives in TraceDispatch so the throw path is unit-testable — see that type.
            return await TraceDispatch.InvokeAsync(sink, toolName, args, Guarded);
        }));

var app = builder.Build();

// 7. D-02(a): stamp the REQUIRED. prose marker onto every [CosyRequired] parameter's emitted
//    description — ONCE, at initialization, before the transport serves anything.
//
//    This was originally an AddListToolsFilter that rewrote tool.InputSchema on every
//    tools/list. Measured 2026-09-10, that was unsafe: McpServerTool.ProtocolTool returns a
//    SHARED Tool instance and ListToolsResult.Tools hands back those same instances, so the
//    filter mutated process-wide state on every request. Proved by disabling Apply's
//    idempotency guard and calling tools/list twice — the second response came back
//    "REQUIRED. REQUIRED. Edits to apply...", which can only happen if the filter re-applied
//    on top of its own earlier mutation of a shared object.
//
//    Two distinct problems, only one of which the idempotency guard addressed:
//      - Correctness under concurrency. Tool.InputSchema is a JsonElement — a STRUCT, not a
//        reference — so assigning it copies several fields (the owning JsonDocument plus a
//        token index). That write is not atomic, and two concurrent tools/list calls could
//        tear it: a reader can observe a new document reference beside a stale index.
//        Idempotency prevents a doubled prefix; it says nothing about thread safety.
//      - Wasted work. Every tools/list re-parsed and re-serialized each marked tool's schema,
//        for a result that never changes after the first pass.
//
//    Doing it here fixes both: the mutation happens once, on the startup thread, strictly
//    before RunAsync opens the transport, so no reader can ever observe a partial write.
//    The catalog-completeness argument for filtering over per-tool McpServerTool.Create
//    registration still holds and is why this walks the registered tools rather than
//    hand-listing them: a newly added tool is picked up automatically
//    (12.3-RESEARCH.md Q1, ADR-0023).
//
//    Depends on ProtocolTool being a stable shared instance. That is not incidental — it is
//    the property that makes a one-shot mutation reach every later response, so it is pinned
//    by ProtocolTool_IsAStableSharedInstance_SoStartupStampingReachesEveryResponse. If a
//    future SDK returns a fresh Tool per request, that fact goes red and this must move back
//    to a per-request transform.
foreach (var tool in app.Services.GetServices<McpServerTool>())
{
    var required = CosyRequiredMap.For(tool.ProtocolTool.Name);
    if (required.Count == 0) continue;
    tool.ProtocolTool.InputSchema = SchemaRequiredPrefix.Apply(tool.ProtocolTool.InputSchema, required);
}

await app.RunAsync();
return 0;
