# Cosy — a Claude Code plugin for C#

Cosy is a Roslyn-over-MCP substrate for C#: semantic navigation (find references, list
implementations, get members, text/file search and source reads over the loaded Solution)
plus verified, snapshot-guarded edits (`apply_edits_verified`, `extract_method`, `rename`).
This plugin installs the MCP server and one subagent, `cosy:dotnet-coder`, that is
constrained to use that surface instead of the default `Edit`/`Grep` path.

## Prerequisites

**A .NET SDK must already be on the machine.** This is not a packaging choice — it is a
property of what `workspace_open` does. `MSBuildWorkspace` resolves the target solution's
targeting packs and restored NuGet packages, and that is machine state no shipped artifact
can carry. Shedding it would mean hand-parsing `.csproj` files instead of using MSBuild,
which is reimplementing MSBuild, badly, and owning it forever.

**There is no published NuGet feed for `Cosy.Mcp` yet.** You build the package from this
repository. That is one extra command, not a blocker — step 1 below.

## Install

Step 1 runs in **a clone of this repository**. Steps 2–5 run in the **consumer
repository** — the C# repo you want Cosy to work in.

1. Build the package:

   ```sh
   git clone https://github.com/roobie/cosy.git
   cd cosy
   dotnet pack src/Cosy.Mcp/Cosy.Mcp.csproj -c Release     # -> artifacts/nupkg/
   ```

   **Steps 2–4 collapse into one** if you have [`just`](https://github.com/casey/just).
   From this clone, naming your own repo, and it runs `doctor` at the end:

   ```sh
   just install-into ~/work/my-api
   ```

2. `dotnet new tool-manifest` (once, in the consumer repo). Creates the local tool
   manifest that `dotnet tool run` resolves by walking up from the current directory.
   Note: this can land at the directory root as `dotnet-tools.json` rather than under
   `.config/`, depending on the SDK version that runs it — either location works, because
   resolution walks up from wherever the file is.

3. `dotnet tool update --add-source /abs/path/to/cosy/artifacts/nupkg Cosy.Mcp` —
   installs the server as a local tool declared in that manifest.

   Two things about that path. It must point at the **directory containing the `.nupkg`**,
   which is `artifacts/nupkg` inside the clone — not the clone's root, which yields
   `cosy.mcp is not found in NuGet feeds`. And it must be **absolute**, because you are
   running this from your repository, not from the clone. The `--add-source` argument
   itself is required: with no public feed, a bare `dotnet tool install Cosy.Mcp` cannot
   resolve the package.

   `update` rather than `install` because `install` errors when the tool is already
   present; `update` installs when absent and re-pins when present, so the same command
   works on a fresh machine and after a `git pull`.

4. **On a .NET SDK older than 10 only:** `dotnet tool restore`, once, after cloning the
   consumer repo on a fresh machine. `dotnet tool run` auto-restores on SDK 10 and does
   not on SDK 8, so this step is the difference between "just works" and a confusing
   failure on an older SDK. Skip it on SDK 10+.

5. In Claude Code:

   ```sh
   claude plugin marketplace add https://github.com/roobie/cosy.git
   claude plugin install cosy
   ```

   Then restart Claude Code. A local path to your clone works too
   (`claude plugin marketplace add ./cosy`); `claude plugin marketplace add` accepts
   `owner/repo`, an `https://` URL, or a path, and rejects `ssh://` at its own format
   check.

## Updating

Pull a newer clone, re-pack, then re-pin the consumer repo's manifest — `dotnet tool
update` (not `install`) moves the pin to whatever version the pack directory now holds:

```sh
dotnet tool update --add-source <path-to-the-clone>/artifacts/nupkg Cosy.Mcp
```

Then confirm the bump actually landed with `dotnet tool run cosy-mcp doctor` — its
`version` check fails loudly (exit `5`) if the manifest and the plugin still disagree,
which is exactly the version-skew state a `git pull` without a matching update leaves you
in.

**Then restart Claude Code.** `doctor` inspects files on disk; it cannot see that the MCP
server process launched at session start is still executing the *previous* package. Until
you reconnect, the manifest reads new, `doctor` passes, and every tool call is still
answered by the old build — a green check over a stale binary. Confirm the reconnect took
by finding the live process rather than by asking `doctor` again:

```sh
pgrep -af Cosy.Mcp.dll   # the path names the version actually running:
                         # ~/.nuget/packages/cosy.mcp/<version>/tools/net8.0/any/Cosy.Mcp.dll
```

**Re-packing at an unchanged version does nothing.** `dotnet tool update` resolves an
already-extracted `~/.nuget/packages/cosy.mcp/<version>/` and serves the old binary with no
error. The pack step must therefore raise the version, not just rebuild — in a clone of the
source repository `mise run release` bumps the csproj, `plugin.json` and the manifest in
lockstep before packing, which is why it is preferred over a bare pack for anything meant to
reach another machine.

## What this plugin actually installs

One MCP server registration, launched as `dotnet tool run cosy-mcp` against the consumer
repo's tool manifest, and one subagent, `cosy:dotnet-coder`. No hooks. Nothing is written
into the consumer repository beyond the tool manifest from step 2, and your
`.claude/settings.json` is not touched.

Invoke the subagent one of three ways: `--agent cosy:dotnet-coder` on the command line,
`@agent-cosy:dotnet-coder` inline in a prompt, or by typing `@` and selecting `cosy` from
the agent picker.

## What is and is not enforced

Read this before treating low `Edit`/`Grep` traffic in a trace as evidence that Cosy helped.

- **Enforcement exists only inside the `cosy:dotnet-coder` subagent.** Its declared tool
  list omits `Edit`, `Write`, `Grep`, `Glob` and `Bash` entirely — those tools are not
  merely discouraged there, they do not exist to be reached for.
- **The main loop is unguarded by design.** Outside that subagent, `Edit`, `Write` and
  `Bash` are all available, nothing denies them, no denial log exists, and traces record
  what happened rather than preventing anything from happening.
- **Record what routing instruction your own session carries.** A session with an ambient
  preference for `Bash` over `Read`/`Edit` — observed directly during this project's own
  measurements — will produce low `Edit` traffic in the main loop regardless of whether
  Cosy is any good, because that is harness routing, not Cosy engagement. Without
  recording that instruction alongside your feedback, a quiet trace is unreadable.

## The tool surface

Eighteen Cosy tools, grouped by purpose, plus the builtin `Read`:

- **Workspace lifecycle:** `workspace_open`, `workspace_close`
- **Navigation:** `find_references`, `list_implementations`, `get_members`
- **Search:** `find_text`, `find_files`
- **Source inspection:** `read_source`, `read_source_span`
- **Verification:** `compile_check`, `run_tests`
- **Mutation:** `apply_edits_verified`, `rename`, `extract_method`
- **Snapshot lifecycle:** `read_snapshot`, `check_snapshot`, `commit_snapshot`,
  `discard_snapshot`

`Read` stays on the allowlist because the Cosy read verbs share one corpus — the loaded
Solution's documents — so removing it would blind the agent to project files, build
properties, markdown, and anything else no `.csproj` references.

Two facts a newcomer reliably gets wrong:

- **Edits are staged, not written.** `apply_edits_verified` and `extract_method` do not
  touch disk. A mutation is only terminated by `commit_snapshot` (writes and promotes) or
  `discard_snapshot` (evicts it).
- **`find_text`, `find_files` and the source-read verbs see loaded Solution documents,
  never the filesystem.** A `.cs` file no `.csproj` references, or a non-source file such
  as `.md` or `.json`, is invisible to all of them — a zero-hit result means "not in the
  loaded Solution," never "not present in the repository."

## `doctor`

Run `dotnet tool run cosy-mcp doctor` once after install. It runs three checks in order
and stops at the first failure:

| Check | What it actually observes |
|-------|----------------------------|
| `manifest` | a tool manifest reachable from the current directory declares `cosy.mcp` with command `cosy-mcp` — not that the plugin itself is installed |
| `tool_run` | that declared command restores and runs — not that Claude Code has launched it |
| `version` | the running binary's version core matches the plugin's declared version — not that the plugin is loaded in your current session |

Flags: `--json` for a single-line machine-readable payload; `--plugin-root <path>` or
`--plugin-version <version>` to supply the version to compare against (mutually exclusive
with each other).

Exit codes: `0` healthy, `1` usage error (bad flags), `3` the `manifest` check failed, `4`
`tool_run` failed, `5` `version` mismatched. Exit code `2` is never emitted by `doctor`.

`doctor` runs no network probe, and its tracing line reports whether `COSY_TRACE_PATH` is
*set*, not whether the sink can actually write there.
