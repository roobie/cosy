using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cosy.Mcp.Contracts;
using Cosy.Mcp.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Cosy.Mcp.Tools;

// Data payload for run_tests — lives under envelope.data (ADR-0007 §3.3, D-16).
// Per-test list is always present; outcome is "passed" | "failed" | "skipped". D-17:
// tests[].outcome="failed" does NOT set envelope is_error=true — clean test failure
// is a successful tool run.
public sealed record RunTestsToolData(
    [property: JsonPropertyName("summary")] RunTestsSummary Summary,
    [property: JsonPropertyName("tests")]   IReadOnlyList<TestResult> Tests);

public sealed record RunTestsSummary(
    [property: JsonPropertyName("passed")]      int Passed,
    [property: JsonPropertyName("failed")]      int Failed,
    [property: JsonPropertyName("skipped")]     int Skipped,
    [property: JsonPropertyName("total")]       int Total,
    [property: JsonPropertyName("duration_ms")] long DurationMs);

public sealed record TestResult(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("outcome")]     string Outcome,   // "passed" | "failed" | "skipped"
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("message")]     string? Message,
    [property: JsonPropertyName("stack")]       string? Stack);

/// <summary>
/// run_tests — wrap `dotnet test` for the agent's workspace (NOT Cosy's own dev cycle).
/// ADR-0007 §3. Closes inversion FM-1 by execution. Strategy: TRX XML for full results
/// (Visual Studio 2010 TeamTest schema) with stdout-line buffering as fallback and
/// partial-results source on timeout (D-18). Subprocess is spawned via
/// <see cref="ProcessStartInfo.ArgumentList"/> so user-supplied filter strings cannot
/// inject shell metacharacters (V11 / threat T-09-25).
/// </summary>
[McpServerToolType]
public sealed class RunTestsTool
{
    // TRX TestRun namespace — stable since VS 2010.
    private static readonly XNamespace TrxNs = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    // Stdout per-test line regex matches both VSTest and MTP formats (RESEARCH.md §2):
    //   VSTest: "  Passed Test1 [0 ms]"
    //   MTP:    "  passed Test1 (3ms)"
    // The first capture is the outcome word; the second is the test name with any
    // trailing `[...]` or `(...)` suffix stripped.
    private static readonly Regex StdoutTestLine = new(
        @"^\s+(passed|Passed|failed|Failed|skipped|Skipped)\s+(.+?)(?:\s+\[.*\]|\s*\(.*\))?\s*$",
        RegexOptions.Compiled);

    // MSBuild error-line regex matches the common forms emitted by `dotnet test` when
    // the underlying build fails — e.g. `Foo.cs(10,5): error CS1002: ; expected [Foo.csproj]`.
    private static readonly Regex MsbuildError = new(
        @"^(?<file>[^:]+(?:\.cs|\.csproj))(?:\((?<line>\d+)(?:,\d+)?\))?\s*:\s*error\s+(?<id>[A-Z]+\d+)?\s*:\s*(?<msg>.+)$",
        RegexOptions.Compiled);

    [McpServerTool(Name = "run_tests")]
    [Description(
        "Run `dotnet test` against a project in the open workspace and return per-test results. " +
        "Test failures are reported under data.tests[].outcome=\"failed\" but do NOT set is_error=true. " +
        "Runner execution failures (build failed, timeout, runner not found) set is_error=true with " +
        "kind=test_runner_failed or build_failed. Optional timeout_ms kills the subprocess and returns " +
        "partial results parsed from stdout. Requires workspace_open first.")]
    public async Task<object> RunAsync(
        [Description("Absolute path to the .csproj or .sln to test. Relative paths are rejected.")] string project,
        IWorkspaceHost workspaceHost,
        ILogger<RunTestsTool> logger,
        [Description("Optional dotnet-test --filter expression (VSTest filter syntax). Silently ignored by MTP-backed projects.")] string? filter = null,
        [Description("Optional timeout in milliseconds (1..600000). On timeout the subprocess tree is killed and partial results are returned in error.details.partial_results.")] int? timeoutMs = null,
        [Description("If true, pass --no-build to dotnet test (skip rebuild — caller must ensure the project is already built). Default false.")] bool noBuild = false,
        CancellationToken ct = default)
    {
        // --- ARG VALIDATION (no workspace lease required for arg checks) ---

        if (string.IsNullOrEmpty(project))
            return Envelope<RunTestsToolData>.Err(
                ToolError.InvalidArgument("project", "must_not_be_empty", value: project));

        if (!Path.IsPathRooted(project))
            return Envelope<RunTestsToolData>.Err(
                ToolError.InvalidArgument("project", "relative_path_rejected", value: project));

        if (timeoutMs is int rangeCheck && (rangeCheck < 1 || rangeCheck > 600_000))
            return Envelope<RunTestsToolData>.Err(
                ToolError.InvalidArgument("timeout_ms", "out_of_range_1_to_600000", value: rangeCheck));

        // --- WORKSPACE GUARD ---
        // Tool requires a workspace to be open so this is anchored to the agent's task
        // boundary. The lease is short — we do not hold it across the subprocess (the
        // subprocess does not touch Roslyn state).
        string projectAbsolute;
        using (var lease = workspaceHost.RentSolution(out var solution))
        {
            if (solution is null)
                return Envelope<RunTestsToolData>.Err(ToolError.WorkspaceNotLoaded());

            projectAbsolute = Path.GetFullPath(project);
            if (!File.Exists(projectAbsolute))
                return Envelope<RunTestsToolData>.Err(
                    ToolError.InvalidArgument("project", "file_not_found", value: project));
            // Note: Phase 9.x SEC-02 will add path-allowlist enforcement here (T-09-26).
            // For Phase 9 we accept any path that exists. ADR-0007 §3 documents this scope.
        }

        // --- SUBPROCESS SETUP ---

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutMs is int ms) linkedCts.CancelAfter(ms);
        var effectiveCt = linkedCts.Token;

        // Per-call temp dir under system temp — created here, deleted in finally.
        var tmpDir = Path.Combine(Path.GetTempPath(), "cosy-run-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        var trxPath = Path.Combine(tmpDir, "results.trx");

        var sw = Stopwatch.StartNew();
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        try
        {
            // V11 subprocess hygiene: every argument is a separate ArgumentList entry —
            // no shell, no string interpolation of user input into a command line.
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("test");
            psi.ArgumentList.Add(projectAbsolute);
            // VSTest projects (nunit, mstest, classic xunit) respond to --logger trx.
            // MTP-runner projects (xunit.v3 with UseMicrosoftTestingPlatformRunner=true)
            // silently ignore --logger (warning MTP0001) and need --report-xunit-trx
            // passed after `--` instead. We pass both so the same argv works for either
            // project type — exactly one of the two paths will produce results.trx.
            psi.ArgumentList.Add("--logger");
            psi.ArgumentList.Add($"trx;LogFileName={trxPath}");
            psi.ArgumentList.Add("--results-directory");
            psi.ArgumentList.Add(tmpDir);
            if (!string.IsNullOrEmpty(filter))
            {
                psi.ArgumentList.Add("--filter");
                psi.ArgumentList.Add(filter);
            }
            if (noBuild)
            {
                psi.ArgumentList.Add("--no-build");
            }
            // Everything after `--` is forwarded to the test runner itself (MTP),
            // not to dotnet test. --report-xunit-trx-filename is the bare file name;
            // --results-directory (the MTP option, NOT the VSTest one above) directs
            // it to our tmpDir. The TRX schema produced is the standard VS 2010 TeamTest
            // schema that our XDocument parser already targets.
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("--report-xunit-trx");
            psi.ArgumentList.Add("--report-xunit-trx-filename");
            psi.ArgumentList.Add("results.trx");
            psi.ArgumentList.Add("--results-directory");
            psi.ArgumentList.Add(tmpDir);

            // T-09-32: dotnet may not be on PATH — Process.Start can return null in that case.
            var procNullable = Process.Start(psi);
            if (procNullable is null)
            {
                sw.Stop();
                return Envelope<RunTestsToolData>.Err(
                    ToolError.TestRunnerFailed(
                        reason: "runner_not_found",
                        exitCode: null,
                        stderrTail: "dotnet executable not found on PATH",
                        timeoutMs: null,
                        partialResults: null),
                    (int)sw.ElapsedMilliseconds);
            }
            using var proc = procNullable;

            // Capture both streams asynchronously. We lock on the list because
            // OutputDataReceived/ErrorDataReceived fire on threadpool threads.
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stdoutLines) stdoutLines.Add(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                lock (stderrLines) stderrLines.Add(e.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            bool timedOut = false;
            try
            {
                await proc.WaitForExitAsync(effectiveCt);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Linked CTS canceled by CancelAfter, not by outer caller — that's a timeout.
                timedOut = true;
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                // Wait briefly for the process to actually die so the async stdout/stderr
                // callbacks finish appending lines before we read the buffers.
                try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            }
            // OperationCanceledException with ct.IsCancellationRequested propagates to the
            // outer catch (OperationCanceledException) and re-throws — SDK handles cancel.

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            if (timedOut)
            {
                var (passedSoFar, failedSoFar, lastTestName) = ParseStdoutForPartialResults(stdoutLines);
                logger.LogInformation(
                    "run_tests: timeout after {ElapsedMs}ms; partial p={Passed} f={Failed} last={Last}",
                    elapsedMs, passedSoFar, failedSoFar, lastTestName);
                return Envelope<RunTestsToolData>.Err(
                    ToolError.TestRunnerFailed(
                        reason: "timeout",
                        exitCode: null,
                        stderrTail: TailLines(stderrLines, 20),
                        timeoutMs: timeoutMs,
                        partialResults: new TestRunnerPartialResults(passedSoFar, failedSoFar, lastTestName)),
                    elapsedMs);
            }

            // --- TRX PARSE (happy path) ---

            if (File.Exists(trxPath))
            {
                var doc = XDocument.Load(trxPath);
                var tests = doc.Descendants(TrxNs + "UnitTestResult")
                    .Select(r => new TestResult(
                        Name: r.Attribute("testName")?.Value ?? "",
                        Outcome: MapOutcome(r.Attribute("outcome")?.Value),
                        DurationMs: ParseTrxDuration(r.Attribute("duration")?.Value),
                        Message: r.Descendants(TrxNs + "Message").FirstOrDefault()?.Value,
                        Stack: r.Descendants(TrxNs + "StackTrace").FirstOrDefault()?.Value))
                    .ToList();

                var passed = tests.Count(t => t.Outcome == "passed");
                var failed = tests.Count(t => t.Outcome == "failed");
                var skipped = tests.Count(t => t.Outcome == "skipped");
                var data = new RunTestsToolData(
                    Summary: new RunTestsSummary(passed, failed, skipped, tests.Count, elapsedMs),
                    Tests: tests);
                // D-17: test failures do NOT set is_error=true. Use Ok() even when failed > 0.
                logger.LogInformation(
                    "run_tests: TRX parsed — {Total} tests ({Passed}/{Failed}/{Skipped} p/f/s) in {ElapsedMs}ms",
                    tests.Count, passed, failed, skipped, elapsedMs);
                return Envelope<RunTestsToolData>.Ok(data, elapsedMs);
            }

            // --- TRX MISSING — distinguish "build failed" from "no tests found" ---

            if (proc.ExitCode != 0)
            {
                var diagnostics = ParseMsbuildErrors(stdoutLines, stderrLines);
                if (diagnostics.Count > 0)
                {
                    return Envelope<RunTestsToolData>.Err(
                        ToolError.BuildFailed(
                            project: projectAbsolute,
                            message: diagnostics[0].Message,
                            diagnostics: diagnostics),
                        elapsedMs);
                }
                // Non-zero exit + no recognizable build errors — treat as runner failure.
                return Envelope<RunTestsToolData>.Err(
                    ToolError.TestRunnerFailed(
                        reason: "non_zero_exit",
                        exitCode: proc.ExitCode,
                        stderrTail: TailLines(stderrLines, 20),
                        timeoutMs: null,
                        partialResults: null),
                    elapsedMs);
            }

            // Exit 0 but no TRX — try stdout fallback for MTP projects that don't emit TRX cleanly.
            var stdoutTests = ParseStdoutForAllResults(stdoutLines);
            if (stdoutTests.Count > 0)
            {
                var passed = stdoutTests.Count(t => t.Outcome == "passed");
                var failed = stdoutTests.Count(t => t.Outcome == "failed");
                var skipped = stdoutTests.Count(t => t.Outcome == "skipped");
                var data = new RunTestsToolData(
                    Summary: new RunTestsSummary(passed, failed, skipped, stdoutTests.Count, elapsedMs),
                    Tests: stdoutTests);
                return Envelope<RunTestsToolData>.Ok(data, elapsedMs);
            }

            // Exit 0, no TRX, no stdout matches — zero tests discovered. Return an empty success.
            var emptyData = new RunTestsToolData(
                Summary: new RunTestsSummary(0, 0, 0, 0, elapsedMs),
                Tests: Array.Empty<TestResult>());
            return Envelope<RunTestsToolData>.Ok(emptyData, elapsedMs);
        }
        catch (OperationCanceledException)
        {
            throw; // outer ct cancellation propagates; SDK handles it.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Exception during run_tests");
            return Envelope<RunTestsToolData>.Err(ToolError.Internal(ex));
        }
        finally
        {
            // Best-effort cleanup; if the dir is busy (e.g., a lingering file handle on Windows)
            // we'd rather leak a temp dir than crash the tool.
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }

    // --- helpers ---

    private static string MapOutcome(string? trxOutcome) => trxOutcome switch
    {
        "Passed"      => "passed",
        "Failed"      => "failed",
        "NotExecuted" => "skipped",
        _             => "skipped"
    };

    private static long ParseTrxDuration(string? duration)
    {
        if (string.IsNullOrEmpty(duration)) return 0;
        return TimeSpan.TryParse(duration, out var ts) ? (long)ts.TotalMilliseconds : 0;
    }

    /// <summary>
    /// D-18 partial-results path. Scans the buffered stdout lines (captured live during the
    /// subprocess's run) and counts per-test markers. Used only on timeout.
    /// </summary>
    private static (int passedSoFar, int failedSoFar, string lastTestName) ParseStdoutForPartialResults(
        IReadOnlyList<string> lines)
    {
        int p = 0, f = 0;
        string last = "";
        // Snapshot the buffer under the same lock as the callbacks use.
        string[] snapshot;
        lock (lines) snapshot = lines.ToArray();

        foreach (var line in snapshot)
        {
            var m = StdoutTestLine.Match(line);
            if (!m.Success) continue;
            var outcome = m.Groups[1].Value.ToLowerInvariant();
            var name = m.Groups[2].Value.Trim();
            if (outcome == "passed") { p++; last = name; }
            else if (outcome == "failed") { f++; last = name; }
            else if (outcome == "skipped") { last = name; }
        }
        return (p, f, last);
    }

    /// <summary>
    /// Fallback parser for MTP projects that exit 0 without emitting a TRX file. Reconstructs
    /// the test list from stdout markers. Durations are zero because the regex strips the
    /// `[N ms]` / `(Nms)` suffix to keep the name capture clean.
    /// </summary>
    private static List<TestResult> ParseStdoutForAllResults(IReadOnlyList<string> lines)
    {
        var results = new List<TestResult>();
        string[] snapshot;
        lock (lines) snapshot = lines.ToArray();

        foreach (var line in snapshot)
        {
            var m = StdoutTestLine.Match(line);
            if (!m.Success) continue;
            var outcome = m.Groups[1].Value.ToLowerInvariant();
            var name = m.Groups[2].Value.Trim();
            results.Add(new TestResult(name, outcome, 0, Message: null, Stack: null));
        }
        return results;
    }

    /// <summary>
    /// Extract MSBuild-style error lines from stdout/stderr to populate
    /// <see cref="BuildFailedDetails.Diagnostics"/>. Cap at 20 entries (T-09-03) to keep
    /// the payload bounded.
    /// </summary>
    private static List<BuildDiagnostic> ParseMsbuildErrors(IReadOnlyList<string> stdout, IReadOnlyList<string> stderr)
    {
        var diags = new List<BuildDiagnostic>();
        string[] stdoutSnap, stderrSnap;
        lock (stdout) stdoutSnap = stdout.ToArray();
        lock (stderr) stderrSnap = stderr.ToArray();

        foreach (var line in stdoutSnap.Concat(stderrSnap))
        {
            var m = MsbuildError.Match(line);
            if (!m.Success) continue;
            var lineNum = int.TryParse(m.Groups["line"].Value, out var n) ? (int?)n : null;
            diags.Add(new BuildDiagnostic(
                Id: m.Groups["id"].Success ? m.Groups["id"].Value : null,
                Message: m.Groups["msg"].Value.Trim(),
                File: m.Groups["file"].Value,
                Line: lineNum));
            if (diags.Count >= 20) break; // T-09-30 cap.
        }
        return diags;
    }

    /// <summary>Tail N lines of a buffered stream, joined by '\n'. Null if the buffer is empty.</summary>
    private static string? TailLines(IReadOnlyList<string> lines, int count)
    {
        string[] snapshot;
        lock (lines) snapshot = lines.ToArray();
        if (snapshot.Length == 0) return null;
        var tail = snapshot.Skip(Math.Max(0, snapshot.Length - count));
        return string.Join("\n", tail);
    }
}
