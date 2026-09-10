using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Dispatch;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for compile_check — lives under envelope.data (ADR-0004 §1).
// Old camelCase end_line/end_column fields are snake_case per TC-02. Span unification (Plan 04) not yet applied.
public sealed record CompileCheckToolData(
    [property: JsonPropertyName("diagnostics")] IReadOnlyList<CompileCheckDiagnostic> Diagnostics);

// Diagnostic carries span: {start, end} (char offsets into ORIGINAL snippet text, not
// the wrapped tree — already adjusted when useWrapped=true) plus adjacent 1-based
// line/column/end_line/end_column per D-05.
public sealed record CompileCheckDiagnostic(
    [property: JsonPropertyName("severity")]   string Severity,
    [property: JsonPropertyName("id")]         string Id,
    [property: JsonPropertyName("message")]    string Message,
    [property: JsonPropertyName("span")]       Span Span,
    [property: JsonPropertyName("line")]       int Line,
    [property: JsonPropertyName("column")]     int Column,
    [property: JsonPropertyName("end_line")]   int EndLine,
    [property: JsonPropertyName("end_column")] int EndColumn);

/// <summary>
/// compile_check -- parse a C# snippet against the resident workspace's live Compilation.
/// Auto-wraps bare method bodies; no workspace mutation, no disk writes.
/// Caller brings their own using directives (D-07).
/// </summary>
[McpServerToolType]
public sealed class CompileCheckTool
{
    // Parse-error IDs that suggest the snippet is a bare method body, not a compilation unit.
    private static readonly HashSet<string> WrapHintDiagnostics = new()
    {
        "CS1022", "CS1513", "CS8803", "CS1525", "CS1002", "CS1519"
    };

    [McpServerTool(Name = "compile_check")]
    [Description(
        "Parse a C# snippet and bind it against the resident workspace's live Compilation. " +
        "Returns structured diagnostics (severity, id, message, line, column, end_line, end_column). " +
        "A bare method body is auto-wrapped in 'class __CosySnippet { void __M() { ... } }' and " +
        "diagnostic coordinates are remapped back to the original snippet. " +
        "Caller is responsible for 'using' directives -- no usings are auto-propagated. " +
        "Does not modify the workspace or write to disk. Requires workspace_open first.")]
    public async Task<object> CheckAsync(
        IWorkspaceHost workspaceHost,
        ILogger<CompileCheckTool> logger,
        // Phase 12.3 D-01/D-13: schema-optional now (nullable + = null, moved after DI params —
        // CS1737, D-12); [CosyRequired] is the sole remaining requiredness signal for snippet.
        [CosyRequired]
        [Description("C# source snippet. May be a compilation unit, a class member, or a bare method body.")] string? snippet = null,
        // Deliberately NOT [CosyRequired] (D-14 anchor 2): this description already said
        // "Optional ... Default: solution.Projects.First()" while the schema said required —
        // the second confirmed instance of the phase's own named defect. The handler already
        // resolves a null project to the default via ResolveProject below; marking it would
        // keep rejecting an omitted project, only with better wording.
        [Description("Optional project name or file-path suffix to bind against. Default: solution.Projects.First(). " +
                     "Resolution order: exact Name match, FilePath suffix match, case-insensitive Name match.")] string? project = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // ArgumentGuard rejects an absent/null snippet (CosyRequired) before this handler ever
        // runs -- this binding is a compiler satisfaction only, not a second requiredness check.
        var snippetText = snippet!;

        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<CompileCheckToolData>.Err(ToolError.WorkspaceNotLoaded());

        var targetProject = ResolveProject(solution, project);
        if (targetProject is null)
            // User-input error (unknown project name) → invalid_argument per D-06.
            return Envelope<CompileCheckToolData>.Err(
                ToolError.InvalidArgument("project", "not_found", value: project,
                    message: $"project '{project}' not found. Valid projects: {string.Join(", ", solution.Projects.Select(p => p.Name))}"));

        logger.LogDebug("compile_check: snippet length={Length}, project={Project}, timeoutMs={TimeoutMs}",
            snippetText.Length, targetProject.Name, timeoutMs);

        // D-15: compose caller CT with optional timeout via linked CTS.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
        {
            if (ms < 1 || ms > 600_000)
                return Envelope<CompileCheckToolData>.Err(
                    ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: ms));
            linkedCts.CancelAfter(ms);
        }
        var effectiveCt = linkedCts.Token;

        try
        {
            var sw = Stopwatch.StartNew();

            var parseOptions = (CSharpParseOptions?)targetProject.ParseOptions;

            // Parse the raw snippet to check if it's a valid compilation unit.
            var rawTree = CSharpSyntaxTree.ParseText(snippetText, parseOptions, path: "snippet.cs", cancellationToken: effectiveCt);
            var rawParseErrors = rawTree.GetDiagnostics(effectiveCt)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();

            var chosenTree = rawTree;
            var useWrapped = false;
            var columnOffsetLine1 = 0;

            // Auto-wrap heuristic: if parse errors suggest a bare method body (missing
            // class/member context), try wrapping and use the result only if it reduces errors.
            if (rawParseErrors.Any(d => WrapHintDiagnostics.Contains(d.Id)))
            {
                const string wrapPrefix = "class __CosySnippet { void __M() { ";
                const string wrapSuffix = " } }";

                var wrappedSource = wrapPrefix + snippetText + wrapSuffix;
                var wrappedTree = CSharpSyntaxTree.ParseText(wrappedSource, parseOptions, path: "snippet.cs", cancellationToken: effectiveCt);
                var wrappedParseErrors = wrappedTree.GetDiagnostics(effectiveCt)
                    .Count(d => d.Severity == DiagnosticSeverity.Error);

                if (wrappedParseErrors < rawParseErrors.Length)
                {
                    chosenTree = wrappedTree;
                    useWrapped = true;
                    columnOffsetLine1 = wrapPrefix.Length;
                }
            }

            var compilation = await targetProject.GetCompilationAsync(effectiveCt);
            if (compilation is null)
                // Compilation==null is a Roslyn anomaly, not user input — keep internal_error,
                // but use InvalidOperationException for a clearer exception_type on the wire.
                return Envelope<CompileCheckToolData>.Err(ToolError.Internal(new InvalidOperationException(
                    $"project '{targetProject.Name}' produced no compilation")));

            var newComp = compilation.AddSyntaxTrees(chosenTree);

            // Filter to snippet-only diagnostics at Warning+ severity (D-10).
            var diags = newComp.GetDiagnostics(effectiveCt)
                .Where(d => d.Location.SourceTree == chosenTree)
                .Where(d => d.Severity >= DiagnosticSeverity.Warning)
                .ToArray();

            // Remap coordinates to 1-based; adjust for wrap preamble if active.
            var errorCount = 0;
            var remapped = new List<CompileCheckDiagnostic>(diags.Length);
            foreach (var d in diags)
            {
                var span = d.Location.GetLineSpan();
                var startLine = span.StartLinePosition.Line + 1;
                var startCol = span.StartLinePosition.Character + 1;
                var endLineN = span.EndLinePosition.Line + 1;
                var endCol = span.EndLinePosition.Character + 1;

                // D-05: span is char offsets into the ORIGINAL snippet text so callers can do
                // substring math on their input. When wrapped, subtract the wrap prefix length.
                var src = d.Location.SourceSpan;
                var adjustedStart = useWrapped ? src.Start - columnOffsetLine1 : src.Start;
                var adjustedEnd = useWrapped ? src.End - columnOffsetLine1 : src.End;

                if (useWrapped)
                {
                    // Preamble is on line 1 only (no newlines). Shift columns on line 1.
                    if (startLine == 1) startCol -= columnOffsetLine1;
                    if (endLineN == 1) endCol -= columnOffsetLine1;

                    // Drop diagnostics that straddle / fall inside the wrap prefix.
                    if (startCol < 1 || endCol < 1 || adjustedStart < 0 || adjustedEnd < 0)
                    {
                        logger.LogWarning("compile_check: diagnostic {Id} outside snippet body after remap; dropping", d.Id);
                        continue;
                    }
                }

                if (d.Severity == DiagnosticSeverity.Error) errorCount++;

                remapped.Add(new CompileCheckDiagnostic(
                    Severity: d.Severity.ToString(),
                    Id: d.Id,
                    Message: d.GetMessage(),
                    Span: new Span(adjustedStart, adjustedEnd),
                    Line: startLine,
                    Column: startCol,
                    EndLine: endLineN,
                    EndColumn: endCol));
            }

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var warningCount = remapped.Count - errorCount;
            var summary = $"compile_check: {errorCount} error(s), {warningCount} warning(s) in {elapsedMs}ms";
            logger.LogInformation("{Summary}", summary);

            return Envelope<CompileCheckToolData>.Ok(
                new CompileCheckToolData(remapped),
                elapsedMs);
        }
        catch (OperationCanceledException)
        {
            throw; // let cancellation propagate; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during compile_check");
            return Envelope<CompileCheckToolData>.Err(ToolError.Internal(ex));
        }
    }

    /// <summary>
    /// Resolve project by name or path suffix. Default: first project in solution.
    /// Order: exact Name, FilePath suffix, case-insensitive Name.
    /// </summary>
    private static Project? ResolveProject(Solution solution, string? projectSpec)
    {
        if (string.IsNullOrEmpty(projectSpec))
            return solution.Projects.FirstOrDefault();

        return solution.Projects.FirstOrDefault(p => p.Name == projectSpec)
            ?? solution.Projects.FirstOrDefault(p => p.FilePath != null && p.FilePath.EndsWith(projectSpec, StringComparison.Ordinal))
            ?? solution.Projects.FirstOrDefault(p => string.Equals(p.Name, projectSpec, StringComparison.OrdinalIgnoreCase));
    }
}
