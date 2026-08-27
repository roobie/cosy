using Microsoft.Build.Locator;

namespace Cosy.Mcp.Workspace;

/// <summary>
/// Which MSBuild instance MSBuildLocator resolved at startup, captured so
/// <c>workspace_open</c> can echo it back.
///
/// Cosy does not ship MSBuild — <c>Cosy.Mcp.csproj</c> excludes the Microsoft.Build.*
/// runtime assets on purpose (they cause MSBL001 version conflicts in the output dir),
/// so the SDK on the host is what actually evaluates projects. That makes "which SDK
/// did you pick?" the first question worth answering whenever a solution fails to load
/// on a machine we cannot inspect — see ADR-0011.
/// </summary>
public sealed record MsBuildRegistration(string Kind, string Path, string Version)
{
    /// <summary>Set once at startup, before any Roslyn or MSBuild type loads.</summary>
    public static MsBuildRegistration? Current { get; private set; }

    public static void Capture(VisualStudioInstance instance) =>
        Current = new MsBuildRegistration(
            Kind: instance.DiscoveryType.ToString(),
            Path: instance.MSBuildPath,
            Version: instance.Version.ToString());
}
