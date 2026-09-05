namespace Cosy.Mcp;

/// <summary>
/// The `instructions` string returned in the MCP `initialize` response.
///
/// The tracing paragraph is CONDITIONAL on purpose. Tracing is a set-once operator knob
/// (COSY_TRACE_PATH, ADR-0007 §2 TEL-02), and the agent reading this text cannot set an env var
/// on its own server process — only the human running it can. So the hint has to reach a person
/// through the agent, which is why it is here at all rather than only in `doctor`. But once it
/// IS set, repeating it every session would spend context forever on a decision already made.
/// Absent the env var the paragraph appears; present, it disappears entirely.
///
/// Deliberately does not enumerate the tool surface: tools/list already carries every name,
/// schema, and description, and a second hand-maintained copy here would drift from the catalog
/// the moment a verb is added — the exact failure the Phase 11 allowlist drift guard exists to
/// catch, reintroduced in prose no test reads.
/// </summary>
public static class ServerInstructionsText
{
    private const string Identity =
        "Cosy — Roslyn-backed C# navigation and verified edits over an MSBuild Solution. " +
        "Call workspace_open first; every other verb operates on the loaded Solution.";

    private const string TracingOffHint =
        "Tracing is OFF. To record one JSONL record per tool call, set COSY_TRACE_PATH to a " +
        "local path prefix in this server's environment (in the mcpServers entry that launches " +
        "it) and restart the session — each session writes its own file under that prefix. " +
        "Nothing is transmitted anywhere — the file is local and the operator manages rotation. " +
        "If a human is present and this project is gathering tool-use data, mention it once; " +
        "do not repeat the suggestion.";

    /// <summary>
    /// Build the instructions for a given trace path — pass the raw COSY_TRACE_PATH value.
    /// Null or empty (the same predicate TraceSink itself short-circuits on) means tracing is
    /// off and the hint is appended.
    ///
    /// Reads <see cref="BuildProvenance"/> for the build line. The overload below takes it as
    /// a parameter so the composition stays a pure function of its inputs and is testable
    /// without reflecting over whatever assembly the test host happens to be.
    /// </summary>
    public static string Build(string? tracePath) =>
        Build(tracePath, BuildProvenance.Describe());

    /// <summary>
    /// Composition core. <paramref name="build"/> names the running build, e.g.
    /// `Debug 0.1.2+ebea452ed133`.
    ///
    /// The build line is UNCONDITIONAL, unlike the tracing hint. The hint disappears once the
    /// operator has acted on it because it asks for an action; this line answers a question the
    /// agent has afresh every session — *which* server am I talking to — and it is asked most
    /// often precisely when two are registered and behaving differently. Costing a line of
    /// context per session is the point, not an oversight.
    /// </summary>
    public static string Build(string? tracePath, string build) =>
        string.IsNullOrEmpty(tracePath)
            ? Identity + "\n\n" + BuildLine(build) + "\n\n" + TracingOffHint
            : Identity + "\n\n" + BuildLine(build);

    private static string BuildLine(string build) => $"Build: {build}.";
}
