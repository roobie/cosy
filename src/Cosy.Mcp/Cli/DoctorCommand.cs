using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cosy.Mcp.Cli;

/// <summary>
/// `cosy-mcp doctor` -- three install-health checks with three distinct non-zero exit codes,
/// plus an optional single-line JSON payload for machine consumers (Phase 11 D-12, D-20).
/// Deliberately does not reuse the ADR-0004 wire contract or its closed error-kind union --
/// those are built for Roslyn failures, not install diagnostics.
///
/// Each check is labelled by what its reporter can actually observe, never by what a tester
/// might infer: `manifest` proves a manifest reachable from this directory declares cosy.mcp
/// with command cosy-mcp -- not that the plugin is installed. `tool_run` proves that declared
/// tool restores and runs -- not that Claude Code launched it. `version` proves the running
/// binary's version core matches the plugin's declared version -- not that the plugin is
/// loaded. `doctor` runs no network probe and cannot report an unreachable marketplace; that
/// surfaces inside `claude plugin marketplace add` (D-04).
///
/// Exit code 2 is reserved for the harness's own hook-blocking signal and must never be
/// emitted by a diagnostic (ADR-0012 D-05) -- a broken install must not read as a blocked
/// session.
/// </summary>
public static class DoctorCommand
{
    private const int ExitHealthy      = 0;
    private const int ExitUsage        = 1;
    private const int ExitManifestFail = 3;
    private const int ExitToolRunFail  = 4;
    private const int ExitVersionFail  = 5;

    private static readonly TimeSpan SubprocessTimeout = TimeSpan.FromSeconds(30);

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var json = false;
        string? pluginRoot = null;
        string? pluginVersionArg = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json":
                    json = true;
                    break;
                case "--plugin-root":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("doctor: --plugin-root requires a value");
                        return ExitUsage;
                    }
                    pluginRoot = args[++i];
                    break;
                case "--plugin-version":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("doctor: --plugin-version requires a value");
                        return ExitUsage;
                    }
                    pluginVersionArg = args[++i];
                    break;
                default:
                    stderr.WriteLine($"doctor: unrecognised argument '{args[i]}'");
                    return ExitUsage;
            }
        }

        if (pluginRoot is not null && pluginVersionArg is not null)
        {
            stderr.WriteLine("doctor: --plugin-root and --plugin-version are mutually exclusive");
            return ExitUsage;
        }

        var checks = new List<CheckResult>();

        var manifest = CheckManifest();
        checks.Add(manifest);
        if (manifest.Status != "pass") return Finish(checks, ExitManifestFail, json, stdout);

        var toolRun = CheckToolRun(out var runningVersionRaw);
        checks.Add(toolRun);
        if (toolRun.Status != "pass") return Finish(checks, ExitToolRunFail, json, stdout);

        var pluginVersion = pluginVersionArg ?? ResolvePluginVersion(pluginRoot);
        if (pluginVersion is null)
        {
            checks.Add(new CheckResult("version", "skipped",
                "no plugin version available -- pass --plugin-root, --plugin-version, or set CLAUDE_PLUGIN_ROOT"));
            checks.Add(CheckTracing());
            return Finish(checks, ExitHealthy, json, stdout);
        }

        var runningCore = CliDispatch.VersionCore(runningVersionRaw!.Trim());
        var pluginCore = CliDispatch.VersionCore(pluginVersion);
        if (runningCore == pluginCore)
        {
            checks.Add(new CheckResult("version", "pass",
                $"running version core '{runningCore}' matches plugin version core '{pluginCore}'"));
            checks.Add(CheckTracing());
            return Finish(checks, ExitHealthy, json, stdout);
        }

        checks.Add(new CheckResult("version", "fail",
            $"running version core '{runningCore}' does not match plugin version core '{pluginCore}'"));
        return Finish(checks, ExitVersionFail, json, stdout);
    }

    /// <summary>
    /// Reports whether tracing is switched on. Purely informational: the returned status is
    /// never `fail` and this check never contributes to the exit code, because tracing off is
    /// the documented default (ADR-0007 §2, TEL-02 opt-in) and not an install fault. Doctor's
    /// non-zero codes mean "this install is broken"; a healthy install with tracing off must
    /// still exit 0.
    ///
    /// Only reached on the healthy paths. A failed manifest/tool_run/version check short-circuits
    /// before this runs, which is correct ordering -- a broken install is the larger news, and
    /// mentioning a telemetry knob underneath it would bury the actual fault.
    ///
    /// Reports the path as observed, not as validated: an unwritable or misspelled path still
    /// reads as `on` here. Doctor observes what is configured; it does not open the file, and
    /// labelling this `on` claims exactly that much (the class-level naming rule above).
    /// </summary>
    private static CheckResult CheckTracing()
    {
        var tracePath = Environment.GetEnvironmentVariable("COSY_TRACE_PATH");
        return string.IsNullOrEmpty(tracePath)
            ? new CheckResult("tracing", "off",
                "COSY_TRACE_PATH is unset, so no trace records are written. Set it to a local " +
                "file path in this server's environment to record one JSONL record per tool call")
            : new CheckResult("tracing", "on", $"COSY_TRACE_PATH is set to '{tracePath}'");
    }

    private static string? ResolvePluginVersion(string? pluginRoot)
    {
        var root = pluginRoot ?? Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_ROOT");
        if (string.IsNullOrEmpty(root)) return null;

        try
        {
            var manifestPath = Path.Combine(root, ".claude-plugin", "plugin.json");
            if (!File.Exists(manifestPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// `dotnet tool list --format json` walks up from cwd exactly as `dotnet tool run` resolves
    /// (RESEARCH §3c) -- unlike a hand-parse of a manifest file, it survives a manifest format
    /// change and does not assume the manifest lives under .config/ (11-02 P-11 found it does
    /// not, always). Never invokes a restore -- doctor observes, it does not mutate (T-11-25).
    /// </summary>
    private static CheckResult CheckManifest()
    {
        var result = RunSubprocess("dotnet", new[] { "tool", "list", "--format", "json" }, SubprocessTimeout);
        if (result.TimedOut)
            return new CheckResult("manifest", "fail", "dotnet tool list --format json timed out");
        if (result.ExitCode != 0)
            return new CheckResult("manifest", "fail", $"dotnet tool list --format json exited {result.ExitCode}");

        try
        {
            using var doc = JsonDocument.Parse(result.Stdout);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in data.EnumerateArray())
                {
                    var packageId = entry.TryGetProperty("packageId", out var p) ? p.GetString() : null;
                    if (packageId != "cosy.mcp") continue;
                    if (!entry.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) continue;
                    foreach (var cmd in commands.EnumerateArray())
                    {
                        if (cmd.GetString() == "cosy-mcp")
                        {
                            return new CheckResult("manifest", "pass",
                                "a manifest reachable from this directory declares cosy.mcp with command cosy-mcp");
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return new CheckResult("manifest", "fail", "dotnet tool list --format json produced unparseable output");
        }

        return new CheckResult("manifest", "fail",
            "no manifest reachable from this directory declares cosy.mcp with command cosy-mcp");
    }

    /// <summary>
    /// Collapses "server starts" and "version matches" into one invocation (RESEARCH §3c): a
    /// non-zero exit is a manifest/binary problem reported by this check; a zero exit is handed
    /// to the version check, which compares the printed string against the plugin's declared
    /// version. Spawning the full stdio server would cost more and prove nothing extra about the
    /// same binary.
    /// </summary>
    private static CheckResult CheckToolRun(out string? versionOutput)
    {
        var result = RunSubprocess("dotnet", new[] { "tool", "run", "cosy-mcp", "--version" }, SubprocessTimeout);
        versionOutput = result.ExitCode == 0 ? result.Stdout : null;

        if (result.TimedOut)
            return new CheckResult("tool_run", "fail", "dotnet tool run cosy-mcp --version timed out");
        if (result.ExitCode != 0)
            return new CheckResult("tool_run", "fail", $"dotnet tool run cosy-mcp --version exited {result.ExitCode}");

        return new CheckResult("tool_run", "pass", "the manifest-declared cosy-mcp command restores and runs");
    }

    private static int Finish(IReadOnlyList<CheckResult> checks, int exitCode, bool json, TextWriter stdout)
    {
        if (json)
        {
            var payload = new
            {
                checks = checks.Select(c => new { id = c.Id, status = c.Status, observed = c.Observed }),
                exit_code = exitCode,
            };
            stdout.WriteLine(JsonSerializer.Serialize(payload));
        }
        else
        {
            foreach (var c in checks)
                stdout.WriteLine($"{c.Id}: {c.Status} -- {c.Observed}");
        }

        return exitCode;
    }

    /// <summary>
    /// Every doctor spawn uses a fixed argv via ArgumentList, never a shell command string
    /// (T-11-24) -- no manifest value or flag value is ever interpolated into a command line.
    /// Bounded by a timeout so an unresponsive dotnet subprocess degrades to a failed check
    /// rather than hanging the whole invocation (T-11-26).
    /// </summary>
    private static SubprocessResult RunSubprocess(string fileName, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception)
            {
                // Any kill failure here -- already exited between the check and the kill
                // (InvalidOperationException), insufficient permission to signal the child
                // or platform-specific reap timing (Win32Exception), or anything else --
                // must not prevent reporting TimedOut: true below. The check result already
                // communicates the failure correctly; an unhandled throw would just replace
                // that clear message with a crash and a stack trace (WR-03).
            }
            process.WaitForExit(2000);
            return new SubprocessResult(stdoutBuilder.ToString(), stderrBuilder.ToString(), -1, TimedOut: true);
        }

        process.WaitForExit(); // ensure the async output/error events have flushed
        return new SubprocessResult(stdoutBuilder.ToString(), stderrBuilder.ToString(), process.ExitCode, TimedOut: false);
    }

    private sealed record CheckResult(string Id, string Status, string Observed);

    private sealed record SubprocessResult(string Stdout, string Stderr, int ExitCode, bool TimedOut);
}
