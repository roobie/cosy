# Cosy.Mcp

A Roslyn-over-MCP substrate for C#. It gives a coding agent semantic navigation
(find references, list implementations, get members, search the loaded solution) and
verified, snapshot-guarded edits — edits land in memory first, report only the
diagnostics *they* introduced, and reach disk only on an explicit commit.

Eighteen tools, one response envelope. Pre-1.0, and honestly so: the API will move.

## Prerequisite: a .NET SDK on the machine

**Cosy does not ship MSBuild.** It resolves MSBuild from the SDK installed on the host
and uses it to evaluate your projects, so an SDK — not just the runtime — must be
present. This is inherent to the capability, not a packaging shortcut: evaluating your
solution also needs your targeting packs and your restored NuGet packages, which live on
your machine. There is no self-contained build that removes this.

**.NET SDK 8.0 or later.** The tool targets `net8.0` and rolls forward, so it runs on .NET
8, 9 or 10. The runtime it lands on decides which SDK evaluates your code: on a .NET 10
host it drives SDK 10, on a .NET 8 host SDK 8 — and SDK 8 cannot evaluate a project that
needs SDK 10 targets.

Verify with `dotnet --list-sdks`. If `workspace_open` fails, start with two things: the
`msbuild` object in its response (`kind` / `path` / `version`), which names the MSBuild
that was registered, and any `global.json` beside the solution you are opening — that file
governs which SDK actually evaluates it, and a version it names that you do not have
installed is the most common cause of a failed load.

## Install

**There is no published NuGet feed yet.** Build the package from this repository:

```sh
git clone https://github.com/roobie/cosy.git
cd cosy
```

With [`just`](https://github.com/casey/just), one command builds and installs:

```sh
just install-global
```

Without it, the same thing in two commands. For plain MCP-client use, global is fine:

```sh
dotnet pack src/Cosy.Mcp/Cosy.Mcp.csproj -c Release
dotnet tool update --global --add-source ./artifacts/nupkg Cosy.Mcp
```

> **`--add-source` takes the folder holding the `.nupkg`, not the repository root.** That
> is `./artifacts/nupkg`, and NuGet does not search below whatever you name — pointing it
> at the clone gets you `cosy.mcp is not found in NuGet feeds`. The directory does not
> exist until `dotnet pack` has run, since it is gitignored build output.
>
> `update` rather than `install` is deliberate: `install` fails when the tool is already
> present, so it cannot be re-run after a `git pull`. `update` installs when absent and
> re-pins when present.

Register it with your MCP client — for most clients, in `.mcp.json`:

```json
{
  "mcpServers": {
    "cosy": {
      "command": "cosy-mcp",
      "type": "stdio"
    }
  }
}
```

### As a Claude Code plugin

This repository is also a Claude Code plugin marketplace. The plugin ships the MCP
registration plus one subagent, `cosy:dotnet-coder`, whose tool list is the Cosy verbs
plus `Read` — no `Edit`, `Write`, `Grep`, `Glob` or `Bash`.

The plugin launches the server as `dotnet tool run cosy-mcp`, which resolves a **local**
tool manifest in the repository you are working in, not the global install above. Run
these from the C# repository you want Cosy to work in:

From *this* clone, one command does all of it — including a `doctor` check at the end:

```sh
just install-into ~/work/my-api        # your C# repo, not this one
```

Or by hand, run from the consumer repository:

```sh
dotnet new tool-manifest                                          # once per consumer repo
dotnet tool update --add-source /abs/path/to/cosy/artifacts/nupkg Cosy.Mcp
dotnet tool restore                                               # SDK 8 only; SDK 10 auto-restores
```

The `--add-source` argument must be an **absolute path ending in `/artifacts/nupkg`**, since
you are no longer standing in this clone. It is the folder that holds the `.nupkg` — naming
the clone's root instead is the common miss.

Then, in Claude Code:

```sh
claude plugin marketplace add https://github.com/roobie/cosy.git
claude plugin install cosy
```

Restart Claude Code, then run `dotnet tool run cosy-mcp doctor` to confirm the manifest,
the tool, and the plugin version all agree. See
[`plugins/cosy/README.md`](plugins/cosy/README.md) for what the plugin does and does not
enforce — the distinction matters if you plan to judge Cosy from a trace.

## The tools

| Tool | What it does |
|---|---|
| `workspace_open` / `workspace_close` | Load a `.sln`/`.slnx`/`.csproj` into the resident workspace |
| `find_references` | Every reference to a symbol, classified `reference`/`candidate`/`declaration`/`definition`/`cref` |
| `list_implementations` | Implementations and overrides |
| `get_members` | Members of a type |
| `find_text` / `find_files` | Content search and file discovery, scoped to the loaded solution |
| `read_source` / `read_source_span` | Existing source text, in the same character-offset coordinate system the edit verbs consume |
| `compile_check` | Diagnostics for a snippet against the live compilation, no disk write |
| `run_tests` | `dotnet test` wrapper with TRX parsing and partial results on timeout |
| `apply_edits_verified` | Atomic edits into a snapshot, reporting only newly introduced diagnostics |
| `rename` | Dry-run semantic rename returning the full edit set |
| `extract_method` | Roslyn extract-method refactoring into a snapshot |
| `read_snapshot` / `check_snapshot` | Unified diff and diagnostics for staged work, without committing |
| `commit_snapshot` / `discard_snapshot` | Write a snapshot to disk, or drop it |

Two things newcomers reliably get wrong:

- **Edits are staged, not applied.** `apply_edits_verified` and `extract_method` return a
  `snapshot_id` and touch nothing on disk until `commit_snapshot`. Inspect staged work
  with `read_snapshot` (diff) and `check_snapshot` (diagnostics) first. Commit detects
  out-of-band disk changes by mtime and refuses rather than clobbering.
- **The search and read verbs see loaded solution documents, never the filesystem.** A
  `.cs` file no `.csproj` references, or a non-source file such as `.md` or `.json`, is
  invisible to them. A zero-hit result means "not in the loaded solution", never "not
  present in the repository".

## Configuration

| Variable | Effect |
|---|---|
| `COSY_TRACE_PATH` | Append one JSONL trace record per tool call to this local file. Nothing is transmitted anywhere. Unset = disabled, zero overhead. |

Set it in the `env` block of the `mcpServers` entry that launches Cosy, and restart the
session:

```json
{ "mcpServers": { "cosy": { "command": "dotnet", "args": ["tool", "run", "cosy-mcp"],
  "type": "stdio", "env": { "COSY_TRACE_PATH": "/path/to/traces/cosy.jsonl" } } } }
```

The parent directory is created if it does not exist. If the path still cannot be opened,
tracing degrades to off with a warning on stderr rather than failing the tool call that
happened to construct the sink.

Trace records carry tool arguments verbatim, including edit text. Point
`COSY_TRACE_PATH` somewhere you are willing to have source code written, and note that
`cosy-mcp doctor` reports whether the variable is *set*, not whether the sink is actually
writable — a path that cannot be opened reports as "on".

**One known limitation, stated plainly:** the sink's write lock is per-process. Two Cosy
server processes pointed at the same trace file will interleave and can lose records
outright. Give each server its own path until that is fixed.

## Licence

Apache License 2.0 — see [LICENSE](LICENSE).

---

*This repository is a published subset of a larger private working repository. It carries
the plugin, the server source, and the build files needed to produce a working install.
The design records, test suite and planning history are not published.*

*One consequence is visible in the source. Comments cite design records by marker —
`ADR-0007`, `D-12`, `Phase 9`, `T-03` — and those records are not here, so the markers do
not resolve to anything you can open. They are left in place because the sentences around
them carry the actual reasoning, which is the part worth reading; treat a marker as a
label on a decision rather than a link. The same applies to the occasional citation of a
test file: the test suite is not part of this mirror.*
