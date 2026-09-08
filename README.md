# Cosy.Mcp

A Roslyn-over-MCP substrate for C#. It gives a coding agent semantic navigation
(find references, list implementations, get members, search the loaded solution) and
verified, snapshot-guarded edits — edits land in memory first, report only the
diagnostics *they* introduced, and reach disk only on an explicit commit.

Eighteen tools, one response envelope. Pre-1.0, and honestly so: the API will move.

## Quick start

**A .NET SDK must be on the machine** — 8.0 or later to run it, 10 to build it, and the
steps below do build it. [Requirements](#requirements) says why, and what to check when
`workspace_open` fails.

**There is no published NuGet feed**, so you build the package from this repository:

```sh
git clone https://github.com/roobie/cosy.git
cd cosy
```

Everything below uses [`just`](https://github.com/casey/just), which is optional — every
recipe is a couple of plain `dotnet` commands, spelled out in
[Without `just`](#without-just).

### Install

Which of the two you need is decided by how your client launches the server.

**As a Claude Code plugin.** The plugin launches `dotnet tool run cosy-mcp`, which resolves
a **per-repository** tool manifest — so the install goes into the C# repo you want Cosy to
work in, and a global install does not satisfy it:

```sh
just install-into ~/work/my-api     # your C# repo, not this one
```

That packs, creates the manifest if the repo has none, pins the tool, restores it, and ends
by running `doctor` — so a broken install says so here rather than as a silent MCP failure
three steps later. Then, in Claude Code:

```sh
claude plugin marketplace add https://github.com/roobie/cosy.git
claude plugin install cosy@cosy-mcp
```

and restart it.

**Any other MCP client.** These launch the `cosy-mcp` binary directly, so a global install
is what you want:

```sh
just install-global
```

then register it:

```json
{ "mcpServers": { "cosy": { "command": "cosy-mcp", "type": "stdio" } } }
```

### Update

```sh
git pull
just install-into ~/work/my-api     # or: just install-global
```

Re-running the install recipe *is* the update — each one re-packs and re-pins, and every
recipe is safe to re-run because they use `dotnet tool update`, which installs when the
tool is absent and re-pins when it is present.

**Then restart your MCP client.** A server process started before the update keeps
executing the *old* package: the manifest reads new, `doctor` passes, and every tool call
is still answered by the previous build. Confirm the reconnect by finding the live process,
whose path names the version actually running:

```sh
pgrep -af Cosy.Mcp.dll    # …/packages/cosy.mcp/<version>/tools/net8.0/any/Cosy.Mcp.dll
```

Each consumer repo carries its own manifest and is updated independently, so one can go
stale while another is current.

### Verify

```sh
just doctor ~/work/my-api           # changes nothing
```

Three of `doctor`'s checks gate the exit code, run in order, and stop at the first failure:
`manifest` (a manifest reachable from there declares `cosy.mcp`), `tool_run` (that command
restores and runs), `version` (the running binary matches the plugin's declared version).
Exit `0` healthy, `1` bad flags, `3` `manifest`, `4` `tool_run`, `5` `version`.

It then reports two more that never change the exit code: `build` (which build is answering
— `Release 0.1.5+<sha>`, the line to read when you are not sure whether an update landed)
and `tracing`. `--json` puts all five on one machine-readable line.

`doctor` runs no network probe and cannot tell you the marketplace is unreachable — that
failure surfaces inside `claude plugin marketplace add`, with Claude Code's own error.

### Without `just`

`just install-global` is:

```sh
dotnet pack src/Cosy.Mcp/Cosy.Mcp.csproj -c Release
dotnet tool update --global --add-source ./artifacts/nupkg Cosy.Mcp
```

`just install-into DIR` is the same pack, then, **run from the consumer repository**:

```sh
dotnet new tool-manifest                                          # once per consumer repo
dotnet tool update --add-source /abs/path/to/cosy/artifacts/nupkg Cosy.Mcp
dotnet tool restore                                               # SDK 8 only; SDK 10 auto-restores
dotnet tool run cosy-mcp doctor --plugin-root /abs/path/to/cosy/plugins/cosy
```

Two things to get right, both of which produce confusing failures:

- **`--add-source` takes the folder holding the `.nupkg`, not the repository root.** That
  is `artifacts/nupkg` — and an **absolute** path once you are standing in the consumer
  repo rather than in this clone. NuGet does not search below whatever you name, so
  pointing it at the clone gets you `cosy.mcp is not found in NuGet feeds`. The directory
  does not exist until `dotnet pack` has run, since it is gitignored build output.
- **`update` rather than `install`.** `install` fails when the tool is already present, so
  it cannot be re-run after a `git pull`. `update` installs when absent and re-pins when
  present.

See [`plugins/cosy/README.md`](plugins/cosy/README.md) for what the plugin does and does
not enforce — the distinction matters if you plan to judge Cosy from a trace.

## Requirements

**Cosy does not ship MSBuild.** It resolves MSBuild from the SDK installed on the host
and uses it to evaluate your projects, so an SDK — not just the runtime — must be
present. This is inherent to the capability, not a packaging shortcut: evaluating your
solution also needs your targeting packs and your restored NuGet packages, which live on
your machine. There is no self-contained build that removes this.

**To run it: .NET SDK 8.0 or later.** The tool targets `net8.0` and rolls forward, so it
runs on .NET 8, 9 or 10 — measured, not assumed: the packed 0.1.5 tool starts and reports
its version under `dotnet exec --fx-version 8.0.26` with roll-forward disabled. The runtime
it lands on decides which SDK evaluates your code: on a .NET 10 host it drives SDK 10, on a
.NET 8 host SDK 8 — and SDK 8 cannot evaluate a project that needs SDK 10 targets.

**To build it from source: .NET SDK 10.** Stricter than running it, and not a preference.
Cosy pins Roslyn 5.3.0, whose analyzer assemblies reference compiler `4.12.0.0`; SDK 8 ships
`4.11.0.0` and the build fails with `CS9057`. `global.json` asks for `10.0.100` so that an
SDK 8 machine is told it lacks the SDK, rather than compiling for ninety seconds and dying
inside the C# compiler. This matters here because the install steps above build from source.

Verify with `dotnet --list-sdks`. If `workspace_open` fails, start with two things: the
`msbuild` object in its response (`kind` / `path` / `version`), which names the MSBuild
that was registered, and any `global.json` beside the solution you are opening — that file
governs which SDK actually evaluates it, and a version it names that you do not have
installed is the most common cause of a failed load.

## The tools

| Tool | What it does |
|---|---|
| `workspace_open` / `workspace_close` | Load a `.sln`/`.slnx`/`.csproj` into the resident workspace |
| `find_references` | Every reference to a symbol, classified `reference`/`candidate`/`declaration`/`definition`/`cref` |
| `list_implementations` | Implementations and overrides |
| `get_members` | Members of a type |
| `find_text` / `find_files` | Content search and file discovery, scoped to the loaded solution |
| `read_source` / `read_source_span` | Existing source text, in the same UTF-16 code unit coordinate system the edit verbs consume |
| `compile_check` | Diagnostics for a snippet against the live compilation, no disk write |
| `run_tests` | `dotnet test` wrapper with TRX parsing and partial results on timeout |
| `apply_edits_verified` | Atomic edits into a snapshot, reporting only newly introduced diagnostics |
| `rename` | Dry-run semantic rename returning the full edit set |
| `extract_method` | Roslyn extract-method refactoring into a snapshot |
| `read_snapshot` / `check_snapshot` | Unified diff and diagnostics for staged work, without committing |
| `commit_snapshot` / `discard_snapshot` | Write a snapshot to disk, or drop it |

Every offset in this surface — spans, `document_length` — is zero-based, end-exclusive, and
counted in UTF-16 code units, never bytes. Do not derive one from `wc -c`, `ls -l`, or file
size; `wc -m` is also wrong, since it disagrees with Roslyn on surrogate pairs. Get offsets
from a span Cosy already returned, a .NET/Roslyn string position, or the `document_length` an
`out_of_bounds` error carries.

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
