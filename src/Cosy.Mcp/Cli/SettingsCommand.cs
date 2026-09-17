using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cosy.Mcp.Cli;

/// <summary>
/// `cosy-mcp settings set-trace-path &lt;prefix&gt;` -- set one `env` key in a Claude Code
/// settings.json, safely, from a machine that has nothing but a .NET SDK.
///
/// WHY THIS LIVES IN THE TOOL. `scripts/setup-repo.sh` has to work on Windows through Git
/// Bash, where the repo's usual `lk` does not run, and editing JSON in bash means either
/// hand-rolling a parser or adding `jq`/`node` to a plugin whose only declared prerequisite is
/// a .NET SDK (plugins/cosy/README.md). The SDK is already there, this binary is already being
/// installed by the same script two steps earlier, and putting the edit here buys typed
/// fixtures in the suite CI already gates.
///
/// WHY THE CONCURRENCY CHECK EXISTS, and why an atomic rename is not enough. Claude Code
/// rewrites this file itself (theme, model, plugin installs), and so can a second `setup-repo`.
/// Read-modify-rename with no re-check would replace THEIR write with our stale copy, and our
/// own verification re-read would still pass, because the key we wrote is present. So the exact
/// text read at the start is compared again immediately before the move, and any difference
/// refuses without writing. There is no retry and no merge: a settings file that changed under
/// us is the operator's to look at.
///
/// The lock file is a courtesy against two concurrent `setup-repo` runs. It cannot bind Claude
/// Code, which knows nothing about it -- which is exactly why the text comparison above, not
/// the lock, is the real guard.
///
/// FORMATTING IS NOT PRESERVED. The file is re-serialised indented; values survive exactly,
/// key order and whitespace normalise. The alternative is a bespoke format-preserving JSON
/// editor, which is a worse thing to own than a documented normalisation.
///
/// Exit codes are distinct so a script can branch: 0 written or already correct, 1 usage,
/// 6 a different value is already there, 7 the file is not a JSON object we understand,
/// 8 contention (locked, or changed under us). Exit 2 is never emitted -- ADR-0012 D-05
/// reserves it for the harness's hook-blocking signal, and a refused settings edit must not
/// read as a blocked session.
/// </summary>
public static class SettingsCommand
{
    public const int ExitOk          = 0;
    public const int ExitUsage       = 1;
    public const int ExitConflict    = 6;
    public const int ExitInvalidFile = 7;
    public const int ExitContention  = 8;

    private const string TraceKey = "COSY_TRACE_PATH";

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] != "set-trace-path")
        {
            stderr.WriteLine("settings: expected `set-trace-path <prefix>`");
            return ExitUsage;
        }

        string? prefix = null;
        string? settingsPath = null;
        var dryRun = false;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--settings":
                    if (i + 1 >= args.Length)
                    {
                        stderr.WriteLine("settings: --settings requires a value");
                        return ExitUsage;
                    }
                    settingsPath = args[++i];
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        stderr.WriteLine($"settings: unrecognised argument '{args[i]}'");
                        return ExitUsage;
                    }
                    if (prefix is not null)
                    {
                        stderr.WriteLine("settings: expected exactly one <prefix>");
                        return ExitUsage;
                    }
                    prefix = args[i];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(prefix))
        {
            stderr.WriteLine("settings: <prefix> is required and must not be empty");
            return ExitUsage;
        }

        // A prefix, never a file name: the sink appends `.<session>.jsonl` to it (13-03's
        // contract). A value ending in .jsonl still works through the degradation rule, but it
        // is not what the analysis tooling's --agent-prefix expects, so say so rather than
        // silently accepting it.
        if (prefix.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            stderr.WriteLine($"settings: warning -- '{prefix}' ends in .jsonl; this is a PREFIX, " +
                             "each session appends .<id>.jsonl to it");

        settingsPath ??= DefaultSettingsPath();
        return SetTracePath(settingsPath, prefix!, dryRun, stdout, stderr);
    }

    /// <summary>`~/.claude/settings.json`, the user-scope file (see plugins/cosy/README.md).</summary>
    public static string DefaultSettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                     ".claude", "settings.json");

    /// <summary>
    /// The edit itself, separated from argv parsing so it is directly testable.
    ///
    /// <paramref name="afterRead"/> is a TEST SEAM and nothing else: it runs once, after this
    /// method has read the file and before it re-reads to detect a concurrent write. A fixture
    /// uses it to land a competing write at exactly the interleaving that an atomic rename
    /// alone would silently clobber; production callers leave it null. Without such a seam the
    /// fixture would have to race a real process, and a racy fixture proves nothing on a slow
    /// runner.
    /// </summary>
    public static int SetTracePath(string path, string prefix, bool dryRun,
                                   TextWriter stdout, TextWriter stderr,
                                   Action? afterRead = null)
    {
        if (!File.Exists(path))
        {
            stderr.WriteLine($"settings: {path} does not exist -- start Claude Code once, or pass --settings");
            return ExitInvalidFile;
        }

        var lockPath = Path.Combine(Path.GetDirectoryName(path)!, ".cosy-setup.lock");
        FileStream? lockHandle;
        try
        {
            lockHandle = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            stderr.WriteLine($"settings: another setup holds {lockPath}; inspect it rather than " +
                             "deleting it blindly");
            return ExitContention;
        }

        try
        {
            var before = File.ReadAllText(path);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(before);
            }
            catch (JsonException e)
            {
                stderr.WriteLine($"settings: {path} is not valid JSON ({e.Message}); nothing written");
                return ExitInvalidFile;
            }

            if (root is not JsonObject obj)
            {
                stderr.WriteLine($"settings: {path} is not a JSON object at the top level; nothing written");
                return ExitInvalidFile;
            }

            JsonObject env;
            if (obj.TryGetPropertyValue("env", out var envNode) && envNode is not null)
            {
                if (envNode is not JsonObject existingEnv)
                {
                    stderr.WriteLine($"settings: {path} has an `env` that is not an object; nothing written");
                    return ExitInvalidFile;
                }
                env = existingEnv;
            }
            else
            {
                env = new JsonObject();
                obj["env"] = env;
            }

            if (env.TryGetPropertyValue(TraceKey, out var currentNode) && currentNode is not null)
            {
                var current = currentNode.GetValue<string?>();
                if (current == prefix)
                {
                    stdout.WriteLine($"settings: {TraceKey} already set to {prefix} -- nothing to do");
                    return ExitOk;
                }

                stderr.WriteLine($"settings: {path} already sets {TraceKey} to '{current}', not " +
                                 $"'{prefix}'. Refusing to overwrite it; edit the file yourself if " +
                                 "that is what you want.");
                return ExitConflict;
            }

            if (dryRun)
            {
                stdout.WriteLine($"settings: would set env.{TraceKey} = {prefix} in {path}");
                return ExitOk;
            }

            env[TraceKey] = prefix;
            var payload = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

            afterRead?.Invoke();

            // The guard the whole command exists for: did anyone else write in the meantime?
            if (File.ReadAllText(path) != before)
            {
                stderr.WriteLine($"settings: {path} changed while this command was running; " +
                                 "nothing written. Re-run it.");
                return ExitContention;
            }

            var backup = $"{path}.bak-{DateTime.UtcNow:yyyyMMddTHHmmssZ}";
            File.Copy(path, backup, overwrite: true);
            CopyUnixMode(path, backup);

            var temp = path + ".cosy-tmp";
            File.WriteAllText(temp, payload);
            CopyUnixMode(path, temp);
            File.Move(temp, path, overwrite: true);

            // Verify by re-reading, not by trusting the write.
            var landed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var verified = landed?["env"]?[TraceKey]?.GetValue<string?>();
            if (verified != prefix)
            {
                stderr.WriteLine($"settings: wrote {path} but a re-read does not carry " +
                                 $"{TraceKey}={prefix}. Backup: {backup}");
                return ExitInvalidFile;
            }

            stdout.WriteLine($"settings: set env.{TraceKey} = {prefix} in {path} (backup: {backup})");
            stdout.WriteLine("settings: restart Claude Code before it takes effect, then prove it " +
                             "by making one tool call and finding a new file under that prefix.");
            return ExitOk;
        }
        finally
        {
            lockHandle.Dispose();
            try { File.Delete(lockPath); } catch (IOException) { /* leave it; the message names it */ }
        }
    }

    /// <summary>
    /// Carry the original's permission bits onto a copy. This file can hold tokens, so the
    /// mode is copied and never widened. No-op on Windows, where the Unix mode APIs throw.
    /// </summary>
    private static void CopyUnixMode(string from, string to)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(to, File.GetUnixFileMode(from));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort: a filesystem that refuses chmod must not fail the edit.
        }
    }
}
