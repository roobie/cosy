using System.Reflection;

namespace Cosy.Mcp.Cli;

/// <summary>
/// argv dispatch for the CLI subcommands that must branch before Program.cs's MSBuild
/// registration and stdout neutering. No-args falls through to the stdio server so existing
/// .mcp.json files keep working.
/// </summary>
public static class CliDispatch
{
    /// <summary>
    /// Recognises `--version`/`-v` and `doctor` (Phase 11 D-20; src/Cosy.Mcp/Cli/DoctorCommand.cs).
    /// Returns false for the no-args case, and for anything else — a flag
    /// Host.CreateApplicationBuilder(args) will consume, or simply not a known subcommand — so
    /// the caller falls through to the stdio server rather than guessing.
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
        {
            Console.Out.WriteLine(BuildProvenance.Describe());
            return true;
        }

        if (args[0] == "doctor")
        {
            exitCode = DoctorCommand.Run(args[1..], Console.Out, Console.Error);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Strips SemVer build metadata (everything from the first '+') from an informational
    /// version string. Shared with `doctor` tier 2 (11-03-PLAN.md) so both sides of the
    /// version-skew comparison normalise identically — Directory.Build.props folds
    /// SourceRevisionId into AssemblyInformationalVersion (e.g. "0.1.0+2be16fa..."), while
    /// plugin.json's version is the bare "0.1.0".
    ///
    /// Also drops a leading configuration word. Since ace4c6e, `--version` prints
    /// <see cref="BuildProvenance.Describe"/> -- "Release 0.1.3+&lt;sha&gt;" -- so the running side
    /// reaches `doctor` with "Release " in front while plugin.json's side stays bare. Without
    /// this, the two sides no longer normalise identically and the version check reports skew
    /// against every consumer, which is what it did between ace4c6e and this fix.
    /// </summary>
    public static string VersionCore(string version)
    {
        var lastSpace = version.LastIndexOf(' ');
        if (lastSpace >= 0) version = version[(lastSpace + 1)..];
        var plusIndex = version.IndexOf('+');
        return plusIndex < 0 ? version : version[..plusIndex];
    }

    /// <summary>
    /// Delegates to <see cref="BuildProvenance"/>, which reads the same attribute and is also
    /// consulted by the instructions string and `doctor` — one reader, so the three surfaces
    /// cannot disagree about which build is running.
    /// </summary>
    private static string GetInformationalVersion() => BuildProvenance.InformationalVersion;
}
