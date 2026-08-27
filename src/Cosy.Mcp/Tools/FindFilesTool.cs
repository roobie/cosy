using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Search;
using Cosy.Mcp.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for find_files -- lives under envelope.data (ADR-0004 §1).
// truncated/total_count live on the envelope per §6; not duplicated here.
public sealed record FindFilesToolData(
    [property: JsonPropertyName("items")] IReadOnlyList<FindFilesItem> Items);

// D-15 v1 scope contraction: {file, projects[]} only. kind, is_generated, on_disk, declares[],
// line_count, size_bytes and target_framework are all struck -- see 10.1-CONTEXT.md D-06, D-15.
// No constant-valued `kind` field either: a field with one possible value carries no
// information (D-15 explicitly rejects that "speculative contract" shape).
public sealed record FindFilesItem(
    [property: JsonPropertyName("file")]     string File,
    [property: JsonPropertyName("projects")] IReadOnlyList<FindFilesProjectRef> Projects);

public sealed record FindFilesProjectRef(
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// find_files -- dry-read enumerate-and-filter over the loaded Solution's ordinary documents.
/// The corpus is Roslyn <see cref="Document"/>s, not the filesystem: a file no project
/// references, and a non-source file under a project directory, are invisible to it and remain
/// Bash's job (D-02). Single-phase: no enrichment, no semantic binding -- path/name glob filters
/// and a project-assembly-name filter only, over already-materialised in-memory metadata (D-02:
/// never Directory.EnumerateFiles / filesystem traversal). D-06: a document owned by more than
/// one project instance (a multi-targeted project appears once per TargetFrameworks entry)
/// collapses to one row keyed on solution-relative path, carrying every owning project in
/// projects[].
/// </summary>
[McpServerToolType]
public sealed class FindFilesTool
{
    [McpServerTool(Name = "find_files")]
    [Description("Enumerate documents in the loaded Solution -- the corpus is Roslyn documents, " +
        "not the filesystem, so a file no project references or a non-source file under a " +
        "project directory is invisible here and remains Bash's job. Returns flat items[] with " +
        "(file, projects[]); projects[] elements carry {name}. nameGlob matches the document's " +
        "basename and pathGlob the solution-relative path -- independent projections, both " +
        "case-insensitive and supporting ** (D-07); supply either, both, or neither (absent " +
        "means every document). project matches a project's assembly name, which stays stable " +
        "across a multi-targeted project's per-framework instances, and selects every framework " +
        "instance at once; a name matching no project returns invalid_argument. A document owned " +
        "by more than one project instance -- e.g. a multi-targeted project's per-framework " +
        "duplicates -- appears once, with every owning project listed in projects[]. Items are " +
        "ordered by file (Ordinal) and capped at max (default 500); envelope carries truncated " +
        "and total_count. Requires workspace_open first.")]
    public async Task<object> FindAsync(
        IWorkspaceHost workspaceHost,
        ILogger<FindFilesTool> logger,
        [Description("Optional glob matched against each document's basename (e.g. '*.Service.cs'). Case-insensitive; supports **. Absent means every basename.")] string? nameGlob = null,
        [Description("Optional glob matched against each document's solution-relative path (e.g. 'Sample.Contracts/**/*.cs'). Case-insensitive; supports **. Absent means every path.")] string? pathGlob = null,
        [Description("Optional project assembly name filter -- matches every framework instance of a multi-targeted project. A name matching no project returns invalid_argument.")] string? project = null,
        [Description("Max items to return (default 500). Lower to bound response size; higher to raise the cap.")] int? max = null,
        [Description("Optional timeout in milliseconds (1..600000). If exceeded, the tool returns via cancellation.")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // --- ARGUMENT VALIDATION (cheap first, before any workspace access) ---

        if (max is not null && (max < 1 || max > 10000))
            return Envelope<FindFilesToolData>.Err(
                ToolError.InvalidArgument("max", "out_of_range_1_to_10000", value: max));

        if (timeoutMs is not null && (timeoutMs < 1 || timeoutMs > 600_000))
            return Envelope<FindFilesToolData>.Err(
                ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: timeoutMs));

        // No snapshot branch (D-15: fromSnapshotId deferred) -- the plain `using` form is safe,
        // unlike find_text's lease which must dispose early on a snapshot-not-found return.
        using var lease = workspaceHost.RentSolution(out var solution);
        if (solution is null)
            return Envelope<FindFilesToolData>.Err(ToolError.WorkspaceNotLoaded());

        logger.LogDebug("find_files: nameGlob={NameGlob}, pathGlob={PathGlob}, project={Project}, max={Max}, timeoutMs={TimeoutMs}",
            nameGlob, pathGlob, project, max, timeoutMs);

        var sw = Stopwatch.StartNew();

        // D-08: compose caller CT with optional timeout via linked CTS.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms)
            linkedCts.CancelAfter(ms);
        var effectiveCt = linkedCts.Token;

        try
        {
            // This handler carries `async` to match every other tool's method shape, but a
            // single-phase enumerate-and-filter over already-loaded Project/Document metadata
            // (no document text, no semantic model) has no genuine Roslyn await -- and this repo
            // builds with TreatWarningsAsErrors, so a bodyless `async` method is a compile error
            // (CS1998), not just a style nit. This is the one legitimate no-op await.
            await Task.CompletedTask;
            effectiveCt.ThrowIfCancellationRequested();

            var cap = max ?? 500; // D-08 default.

            var solutionDir = SolutionPaths.GetSolutionDirectory(solution);

            // project matches Project.AssemblyName, not Project.Name -- a multi-targeted
            // project's Project.Name carries a Roslyn-appended "(tfm)" suffix per instance
            // (10.1-RESEARCH.md Priority Finding 2: "MultiTfm(net8.0)" / "MultiTfm(net9.0)"),
            // while AssemblyName ("MultiTfm") stays identical across instances -- the value that
            // actually selects "every framework instance" as the description promises. No
            // matching project anywhere in the solution -> invalid_argument.
            if (project is not null && !solution.Projects.Any(p => p.AssemblyName == project))
                return Envelope<FindFilesToolData>.Err(
                    ToolError.InvalidArgument("project", "not_found", value: project));

            // D-07: one glob dialect, one instance built per supplied parameter -- nameGlob and
            // pathGlob filter different projections (basename vs full relative path), so they
            // cannot share a single Matcher instance.
            var nameMatcher = SolutionGlob.Create(nameGlob);
            var pathMatcher = SolutionGlob.Create(pathGlob);

            // Ordinary documents only -- source-generated / additional-document enumeration is
            // the v2 slice deferred by D-15.
            var candidates = solution.Projects
                .SelectMany(p => p.Documents.Select(d => (Project: p, Document: d)))
                .Where(x => x.Document.FilePath is not null)
                .Where(x => project is null || x.Project.AssemblyName == project)
                .Select(x => (
                    x.Project,
                    RelPath: Path.GetRelativePath(solutionDir, x.Document.FilePath!).Replace('\\', '/')))
                .Where(x => pathMatcher is null || pathMatcher.IsMatch(x.RelPath))
                .Where(x => nameMatcher is null || nameMatcher.IsMatch(Path.GetFileName(x.RelPath)))
                .ToList();

            // D-06 (correctness, not polish): dedupe is path-primary, but `projects[]` dedupe
            // must be by PROJECT INSTANCE (Project.Id), not by the emitted name. A multi-targeted
            // project's two TFM instances carry the SAME AssemblyName (verified live: both
            // Sample.MultiTfm instances report AssemblyName "Sample.MultiTfm" -- this repo's
            // fixture has no explicit <AssemblyName>, so it defaults to the project file's own
            // name, unlike RESEARCH.md Priority Finding 2's throwaway "MultiTfm" probe project).
            // Deduping projects[] by name-equality would silently collapse two owning instances
            // into one row, defeating the entire "plural projects[]" contract this dedupe exists
            // to prove. Ties in the emitted name are ordered stably by solution.Projects'
            // enumeration order, which is unchanged between calls on the same loaded Solution.
            var allItems = candidates
                .GroupBy(x => x.RelPath, StringComparer.Ordinal)
                .Select(g => new FindFilesItem(
                    File: g.Key,
                    Projects: g.Select(x => x.Project)
                        .DistinctBy(p => p.Id)
                        .Select(p => new FindFilesProjectRef(p.AssemblyName))
                        .OrderBy(r => r.Name, StringComparer.Ordinal)
                        .ToList()))
                .OrderBy(i => i.File, StringComparer.Ordinal)
                .ToList();

            // total is the deduped row count computed BEFORE the cap; the cap is applied to the
            // ordered set, never to enumeration order (D-04).
            var total = allItems.Count;
            var items = allItems.Take(cap).ToList();

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            var truncated = total > items.Count;
            logger.LogInformation("find_files: {Count} item(s), truncated={Truncated} in {ElapsedMs}ms",
                items.Count, truncated, elapsedMs);

            var data = new FindFilesToolData(items);
            return Envelope<FindFilesToolData>.Ok(data, elapsedMs, truncated: truncated, totalCount: total);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Roslyn exception during find_files");
            return Envelope<FindFilesToolData>.Err(ToolError.Internal(ex));
        }
    }
}
