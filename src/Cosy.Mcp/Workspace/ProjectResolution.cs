using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace Cosy.Mcp.Workspace;

/// <summary>
/// ONE TFM derivation, THREE readers (quick task 260918-2qp / issue #15). MOVED here, not
/// copied, from <c>WorkspaceHost.ResolveProjectTfm</c> and <c>WorkspaceHost.ResolveTfm</c> so
/// that <c>workspace_open</c>'s <c>data.projects[].target_framework</c> and both
/// <c>compile_check</c>'s and <c>find_files</c>' optional <c>tfm</c> filter are the SAME
/// derivation, not two derivations that happen to agree. A copy is the specific failure this
/// structure exists to prevent: a caller reading a TFM value out of
/// <c>workspace_open</c>'s response and handing it straight to <c>compile_check</c> or
/// <c>find_files</c> is the entire discoverability contract PR #14 shipped, and if the tools
/// re-derived the value themselves that contract could drift silently on the one shape nobody's
/// fixture exercises much: a multi-targeted project.
/// </summary>
public static class ProjectResolution
{
    /// <summary>Reads TargetFramework/TargetFrameworks from csproj XML.
    /// Roslyn's Project API does not expose the TFM string directly.</summary>
    public static string? ResolveTfm(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            return doc.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value
                ?? doc.Descendants(ns + "TargetFrameworks").FirstOrDefault()?.Value?.Split(';')[0].Trim();
        }
        catch { return null; }
    }

    /// <summary>Roslyn's own name/flavor convention for a multi-targeted project instance
    /// ("Microsoft.CodeAnalysis.Features (netcoreapp3.1)"). Not RegexOptions.Compiled -- this
    /// runs a handful of times per load, at load time, so compiling would pay JIT cost for
    /// nothing.</summary>
    private static readonly Regex NameFlavorPattern = new(@"\(([^()]+)\)\s*$");

    /// <summary>Shape guard on the extracted flavor token: accept only if it matches
    /// Roslyn's TFM-flavor convention. This is what stops a project genuinely named
    /// "Foo (Debug)" from reporting "Debug" as its TFM -- a rejected flavor falls through to
    /// the csproj read (design_decisions §3 step 2), so the guard's failure mode is safe.</summary>
    private static readonly Regex FlavorShapePattern = new(@"^[a-z][a-z0-9.\-]*$");

    /// <summary>design_decisions §3: derive a project row's target_framework. Step 1 -- if
    /// Project.Name ends with a parenthesized token matching the TFM-flavor shape, use it (this
    /// is how Roslyn tags each instance of a multi-targeted project). Step 2 -- otherwise fall
    /// back to the existing csproj-XML read via <see cref="ResolveTfm"/>. Step 3 -- otherwise
    /// null. ResolveTfm's known weakness (it returns only the FIRST of
    /// &lt;TargetFrameworks&gt;) cannot produce a wrong per-row answer here: any project with
    /// more than one TFM is loaded by Roslyn as several flavored instances and is answered by
    /// the step-1 branch instead.</summary>
    public static string? TargetFrameworkOf(Project project)
    {
        var match = NameFlavorPattern.Match(project.Name);
        if (match.Success && FlavorShapePattern.IsMatch(match.Groups[1].Value))
            return match.Groups[1].Value;

        return project.FilePath is not null ? ResolveTfm(project.FilePath) : null;
    }

    /// <summary>
    /// The one resolution predicate <c>compile_check</c> and <c>find_files</c> both call
    /// (design_questions_settled §1, quick task 260918-2qp). When <paramref name="project"/> is
    /// supplied, narrows to instances whose <c>Project.AssemblyName</c> equals it; when
    /// <paramref name="tfm"/> is supplied, further narrows to instances whose
    /// <see cref="TargetFrameworkOf"/> equals it. Either or both may be null -- an omitted
    /// <paramref name="project"/> means "every project" (D-4, <c>find_files</c> only;
    /// <c>compile_check</c> never calls this with a null <paramref name="project"/> because
    /// <c>[CosyRequired]</c> rejects that upstream, in ArgumentGuard, before dispatch).
    /// Ordered Ordinal by <c>Name</c> so an enumeration built from the result (e.g. an
    /// <c>ambiguous</c> error message) is deterministic across calls, not an accident of
    /// <c>Solution.Projects</c>' own unordered-across-loads sequence.
    /// </summary>
    public static IReadOnlyList<Project> Resolve(Solution solution, string? project, string? tfm)
    {
        IEnumerable<Project> instances = solution.Projects;

        if (project is not null)
            instances = instances.Where(p => p.AssemblyName == project);

        if (tfm is not null)
            instances = instances.Where(p => TargetFrameworkOf(p) == tfm);

        return instances.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
    }
}
