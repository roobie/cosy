using System.Reflection;

namespace Cosy.Mcp;

/// <summary>
/// Which build of Cosy is this, in a form a reader can act on.
///
/// Why this exists: this repository is routinely run as TWO registered servers at once — a
/// Debug build from the working tree and the Release build the plugin installs (CLAUDE.md,
/// "Two Cosy servers are registered"). Both answer `--version` with the same bare `0.1.2`,
/// so the one question an operator actually has — *which* of them just answered, and is it
/// the build I think it is — had no answer at any reported surface.
///
/// The data was already there and was being discarded. Directory.Build.props folds
/// SourceRevisionId into AssemblyInformationalVersion (`0.1.2+&lt;sha&gt;`), and the SDK emits
/// AssemblyConfigurationAttribute("Debug"/"Release") by default. Measured 2026-09-04: the
/// working-tree build and the installed nupkg differed by four commits while reporting an
/// identical version string. A label that cannot distinguish two artifacts is not evidence
/// about either.
///
/// Read once into statics: assembly attributes cannot change within a process, and both the
/// instructions string and `doctor` want the same answer.
/// </summary>
public static class BuildProvenance
{
    /// <summary>MSBuild configuration this assembly was compiled in — "Debug", "Release", or
    /// "unknown" when the attribute was suppressed (GenerateAssemblyConfigurationAttribute=false).</summary>
    public static string Configuration { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
            is { Length: > 0 } c ? c : "unknown";

    /// <summary>
    /// Full AssemblyInformationalVersion, build metadata included (e.g. `0.1.2+ebea452ed133...`).
    /// Falls back to the assembly version when the attribute is missing, which is the same
    /// fallback the CLI used before this type existed.
    /// </summary>
    public static string InformationalVersion { get; } = ReadInformationalVersion();

    /// <summary>
    /// One line naming the build: `Debug 0.1.2+ebea452ed133`. The commit is truncated to 12
    /// characters — enough to identify it against `git log` and short enough to sit in prose
    /// the agent reads every session. Use <see cref="InformationalVersion"/> when the exact
    /// revision matters, as `doctor` does.
    /// </summary>
    public static string Describe() => $"{Configuration} {ShortVersion(InformationalVersion)}";

    /// <summary>
    /// Truncates SemVer build metadata to 12 characters, leaving the core version intact.
    /// A version with no `+` is returned unchanged rather than padded or rejected — a build
    /// without SourceRevisionId is a legitimate build, not an error to report.
    /// </summary>
    public static string ShortVersion(string informational)
    {
        var plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        var meta = informational[(plus + 1)..];
        return meta.Length <= 12 ? informational : informational[..(plus + 1)] + meta[..12];
    }

    private static string ReadInformationalVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info)) return info;
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
