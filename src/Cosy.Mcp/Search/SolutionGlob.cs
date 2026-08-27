using Microsoft.Extensions.FileSystemGlobbing;

namespace Cosy.Mcp.Search;

/// <summary>
/// The one glob dialect both find_text (pathGlob) and find_files (nameGlob, pathGlob) use
/// (D-07). Matches in-memory only against the solution's already-materialised document path
/// list -- never against the filesystem. Built on <see cref="Matcher"/>'s parameterless
/// constructor, which matches case-insensitively; that is the pinned v1 semantic for both
/// verbs (D-07, pinned 2026-08-20). find_text's <c>caseSensitive</c> parameter governs the
/// content pattern only -- the asymmetry between a case-insensitive path glob and a
/// case-sensitive content match is deliberate and must not be "fixed".
/// </summary>
public sealed class SolutionGlob
{
    private readonly Matcher _matcher;

    private SolutionGlob(string pattern)
    {
        _matcher = new Matcher(); // parameterless => case-insensitive (D-07).
        _matcher.AddInclude(pattern);
    }

    /// <summary>Returns null for a null or whitespace pattern -- "no filter" is the caller's
    /// responsibility to interpret, not this type's.</summary>
    public static SolutionGlob? Create(string? pattern) =>
        string.IsNullOrWhiteSpace(pattern) ? null : new SolutionGlob(pattern);

    /// <summary>Matches a single solution-relative path string in memory (D-02: no traversal,
    /// no filesystem enumeration).</summary>
    public bool IsMatch(string candidate) =>
        _matcher.Match(new[] { candidate }).HasMatches;
}
