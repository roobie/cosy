using Microsoft.CodeAnalysis;

namespace Cosy.Mcp.Search;

/// <summary>
/// Shared solutionDir-fallback chain used by every tool that converts a Document's absolute
/// FilePath to a solution-relative path (find_text, find_files, find_references, get_members,
/// list_implementations, rename). Project-only workspaces (workspace_open on a .csproj)
/// synthesize a Solution with no backing .sln file -- solution.FilePath is genuinely null, not
/// just unlikely. A bare `?? ""` still throws: Path.GetRelativePath rejects an empty
/// relativeTo (verified live). Fall back to the directory of the loaded project itself -- the
/// only meaningful root when there is no solution file. If even that is unavailable (a Solution
/// with zero projects carrying a FilePath -- not reachable through today's workspace_open, but
/// not structurally impossible for a hypothetically staged/edited solution), throw rather than
/// silently feeding an empty relativeTo into every downstream Path.GetRelativePath call: an
/// empty relativeTo throws its own opaque ArgumentException, and this diagnostic names the
/// actual condition instead. Callers' existing `catch (Exception ex)` turns this into
/// internal_error, matching that same fallback's failure surface.
/// </summary>
public static class SolutionPaths
{
    public static string GetSolutionDirectory(Solution solution) =>
        Path.GetDirectoryName(solution.FilePath)
            ?? Path.GetDirectoryName(solution.Projects.FirstOrDefault(p => p.FilePath is not null)?.FilePath)
            ?? throw new InvalidOperationException(
                "loaded solution has no project with a FilePath; cannot compute solution-relative paths");
}
