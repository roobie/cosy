---
description: Re-pin this repository's Cosy build to the version the installed plugin ships against
---

Update the Cosy MCP server binary for the repository you are working in.

## Why this command exists

The plugin launches the server as `dotnet tool run cosy-mcp`, which resolves the dll from
**this repository's** local tool manifest — not from the plugin. So `claude plugin update`
refreshes the plugin's prose and launch line while the same old binary keeps answering every
tool call. This command is the step that actually changes the build.

## Do this

1. **Find the manifest** `dotnet tool run` will resolve, by walking up from the working
   directory: `.config/dotnet-tools.json`, or a root-level `dotnet-tools.json` (both are real
   — `dotnet new tool-manifest` writes one or the other depending on SDK version). Report the
   pinned `cosy.mcp` version. If there is no manifest, say so and stop: this repository has no
   local tool install to update, and the fix is `plugins/cosy/README.md` §Install, not this
   command.

2. **Locate a package source.** There is no public feed yet, so `dotnet tool update` needs
   `--add-source` pointing at a directory built by `mise run pack` inside a clone of the cosy
   repo. Try `${CLAUDE_PLUGIN_ROOT}/../../artifacts/nupkg` first — that resolves when the
   marketplace was added as a local directory (`claude plugin marketplace add <path>`), in
   which case the plugin lives inside the clone. If that directory does not exist, the
   marketplace was cloned from git and does not carry build output; ask the user for the path
   to their clone's `artifacts/nupkg`, or for one to be produced with `mise run pack`. Do not
   guess a path.

3. **Run the update** from the directory that owns the manifest:

   ```
   dotnet tool update --add-source <source> Cosy.Mcp
   ```

   Report the version transition `dotnet` prints verbatim. If it reports the tool is already
   at the latest version the source offers, say so — the source may itself be stale, and
   re-running `mise run pack` in the clone is then the real fix.

4. **Verify with `doctor`, told what the plugin version is.** A bare
   `dotnet tool run cosy-mcp doctor` reports `version: skipped -- no plugin version available`
   and exits `0`, so it is not evidence. Pass the identity explicitly:

   ```
   dotnet tool run cosy-mcp doctor --plugin-version <the plugin's version>
   ```

   Expect `version: pass`. Note this runs the newly pinned binary, so it also confirms the
   package restored.

5. **Tell the user to restart Claude Code.** The MCP server process already launched holds the
   old dll for the life of the session; the new binary is not in effect until a restart. State
   this plainly rather than implying the update took effect immediately.

6. **Offer the sweep.** Git worktrees each carry their own manifest and are updated
   independently — a sibling worktree can stay on the old build while this one is current. If
   sibling worktrees of this repository exist, list the ones whose manifest pins a different
   version and offer to update them too.

## Do not

- Do not edit `dotnet-tools.json` by hand. `dotnet tool update` maintains it, and a
  hand-written version can name a package that was never restored.
- Do not report success from a `dotnet tool update` exit code alone; quote the version line.
- Do not run this against a manifest outside the user's working tree.
