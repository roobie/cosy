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
        // Quick task 260918-2qp / issue #15, D-2/D-3: NOW [CosyRequired] -- the handler no
        // longer resolves an omitted project to an arbitrary solution.Projects.FirstOrDefault().
        // The `= null` default is KEPT regardless (ADR-0023): dropping it would put `project`
        // into the live emitted required[], rejecting every caller ABOVE Cosy where no envelope
        // can ever be returned -- this repo has shipped that exact incident twice (COSY-0004,
        // COSY-0005). The handler's null binding below is a compiler satisfaction only, exactly
        // like `snippet`'s, never a second requiredness check.
        [CosyRequired]
        [Description("Project's Project.AssemblyName (quick task 260918-2qp / issue #15, D-1) -- " +
                     "the same value workspace_open's data.projects[].assembly_name reports, NOT " +
                     "Project.Name (which carries a Roslyn-appended (tfm) suffix on a multi-targeted " +
                     "project instance and is rejected here). If the named assembly has more than one " +
                     "loaded framework instance, also pass tfm to pick one -- omitting tfm there is " +
                     "refused as ambiguous, never resolved by an arbitrary pick.")] string? project = null,
        [Description("Optional target_framework filter (matches workspace_open's data.projects[].target_framework) -- " +
                     "disambiguates when the named project has more than one loaded framework instance. " +
                     "Required only when that project is multi-targeted; ignored otherwise.")] string? tfm = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // ArgumentGuard rejects an absent/null snippet or project (both [CosyRequired]) before
        // this handler ever runs -- these bindings are compiler satisfactions only, not a second
        // requiredness check.
        var snippetText = snippet!;
        var projectSpec = project!;

        // Read lease blocks workspace_close from disposing the workspace while we hold a
        // captured snapshot (ADR-0005 §D-06). Lease lifetime covers the entire method body.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<CompileCheckToolData>.Err(ToolError.WorkspaceNotLoaded());

        // design_questions_settled §1: ONE resolution predicate, shared with find_files. The
        // count rule below is compile_check's own disposition -- it needs exactly one instance,
        // never a filter over several.
        var instances = ProjectResolution.Resolve(solution, projectSpec, tfm);
        if (instances.Count == 0)
        {
            // §5: distinguish "the assembly itself doesn't exist" (blame project) from "the
            // assembly exists but not with this tfm" (blame tfm).
            var assemblyExists = ProjectResolution.Resolve(solution, projectSpec, tfm: null).Count > 0;
            if (assemblyExists)
            {
                var tfms = ProjectResolution.Resolve(solution, projectSpec, tfm: null)
                    .Select(ProjectResolution.TargetFrameworkOf)
                    .Where(t => t is not null)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal);
                return Envelope<CompileCheckToolData>.Err(
                    ToolError.InvalidArgument("tfm", "not_found", value: tfm,
                        message: $"project '{projectSpec}' has no loaded instance targeting tfm '{tfm}'. " +
                                  $"It targets: {string.Join(", ", tfms)}."));
            }

            // §6: distinct AssemblyName values, Ordinal-sorted, never a flavored Project.Name --
            // plus a pointer to where a legal value comes from.
            var assemblyNames = solution.Projects.Select(p => p.AssemblyName)
                .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
            return Envelope<CompileCheckToolData>.Err(
                ToolError.InvalidArgument("project", "not_found", value: project,
                    message: $"project '{projectSpec}' not found. Valid projects: {string.Join(", ", assemblyNames)}. " +
                              "Call workspace_open and read data.projects[].assembly_name."));
        }

        if (instances.Count > 1)
        {
            // design_questions_settled §2: refuse rather than pick -- an arbitrary assembly-scoped
            // pick is the same defect this issue closes, at a smaller scope. `ambiguous` blames
            // `project`, never `tfm`: a shared explicit <AssemblyName> across two csprojs is NOT
            // fixable with tfm, so a tfm-blaming reason would mis-advise that caller.
            var describe = instances
                .Select(p => $"{p.Name} (target_framework={ProjectResolution.TargetFrameworkOf(p) ?? "unknown"}, file_path={p.FilePath})");
            var tfmHint = instances.Select(ProjectResolution.TargetFrameworkOf).Distinct().Count() > 1
                ? " Pass tfm to disambiguate."
                : "";
            return Envelope<CompileCheckToolData>.Err(
                ToolError.InvalidArgument("project", "ambiguous", value: project,
                    message: $"project '{projectSpec}' matches more than one loaded instance: " +
                              $"{string.Join("; ", describe)}.{tfmHint}"));
        }

        var targetProject = instances[0];

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
}
