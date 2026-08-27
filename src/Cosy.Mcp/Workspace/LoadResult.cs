namespace Cosy.Mcp.Workspace;

/// <summary>Mirrors the locked JSON response schema (02-CONTEXT.md §Response schema).
/// IsError/ErrorMessage are internal control-flow fields — the tool method translates
/// them into the MCP-level isError envelope; they are not serialised on success.</summary>
public sealed record LoadResult
{
    public int ProjectCount { get; init; }
    public int DocumentCount { get; init; }
    public int ElapsedMs { get; init; }
    public IReadOnlyList<LoadDiagnostic> Diagnostics { get; init; } = Array.Empty<LoadDiagnostic>();
    public string? ResolvedTfm { get; init; }

    /// <summary>The MSBuild instance that evaluated this load (ADR-0011). Null only if
    /// registration never ran, which in practice means the host process is misconfigured.</summary>
    public MsBuildRegistration? MsBuild { get; init; } = MsBuildRegistration.Current;

    public bool IsError { get; init; }
    public string? ErrorMessage { get; init; }

    public static LoadResult Error(string message) =>
        new() { IsError = true, ErrorMessage = message };
}

/// <summary>WorkspaceDiagnostic surfaced verbatim — Kind is "Failure" or "Warning".</summary>
public sealed record LoadDiagnostic(string Kind, string Message);

/// <summary>Result of <see cref="IWorkspaceHost.CloseAsync"/>. Closed=false is the idempotent
/// "nothing was loaded" answer — not an error. PriorPath is the absolute path of the workspace
/// that was just disposed (null when Closed=false).</summary>
public sealed record CloseResult
{
    public bool Closed { get; init; }
    public string? PriorPath { get; init; }
    public int ElapsedMs { get; init; }
}
