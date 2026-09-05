#!/bin/sh
# SessionStart: warn when the tool manifest `dotnet tool run` will resolve pins a
# different Cosy build than this plugin ships against.
#
# WHY THIS LIVES IN THE PLUGIN AND NOT IN THE TOOL. `plugins/cosy/.mcp.json` launches
# the server as `dotnet tool run cosy-mcp`, which resolves the dll from the CONSUMER's
# local tool manifest -- so the plugin ships no binary and `claude plugin update` can
# never change which build answers. A `cosy-mcp update` subcommand cannot close the gap
# either: it would run the pinned build, and a build predating the subcommand does not
# recognise it. Measured 2026-09-05 against 0.1.2: an unknown subcommand falls through
# CliDispatch to the stdio server and exits 0, so the bootstrap failure is silent AND
# reports success. This check never executes the pinned binary, which is the whole point
# -- it works no matter how old that binary is.
#
# Exits 0 unconditionally. A version check that can block a session is worse than the
# skew it reports.
#
# POSIX sh with no jq: this ships to consumers whose only declared prerequisite is the
# .NET SDK (plugins/cosy/README.md). The repo's usual `lk` is deliberately not used here
# for that reason.

set -eu

# Resolve the manifest the same way `dotnet tool run` does: walk up from the session's
# working directory. Both locations are real -- `dotnet new tool-manifest` writes
# `.config/dotnet-tools.json` or a root-level `dotnet-tools.json` depending on SDK
# version (README §Install step 1), and resolution accepts either.
find_manifest() {
    dir=$1
    while [ -n "$dir" ] && [ "$dir" != "/" ]; do
        if [ -f "$dir/.config/dotnet-tools.json" ]; then
            printf '%s\n' "$dir/.config/dotnet-tools.json"
            return 0
        fi
        if [ -f "$dir/dotnet-tools.json" ]; then
            printf '%s\n' "$dir/dotnet-tools.json"
            return 0
        fi
        dir=$(dirname "$dir")
    done
    return 1
}

# The `version` of the `cosy.mcp` entry. Scoped to that entry rather than the first
# `version` in the file, because a manifest legitimately holds several tools and its own
# top-level `"version": 1` schema field comes first.
manifest_version() {
    awk '
        /"cosy\.mcp"/ { in_entry = 1 }
        in_entry && /"version"/ {
            if (match($0, /"version"[[:space:]]*:[[:space:]]*"[^"]+"/)) {
                field = substr($0, RSTART, RLENGTH)
                sub(/.*:[[:space:]]*"/, "", field)
                sub(/"$/, "", field)
                print field
                exit
            }
        }
    ' "$1"
}

plugin_version() {
    [ -f "$1" ] || return 1
    awk '
        /"version"/ {
            if (match($0, /"version"[[:space:]]*:[[:space:]]*"[^"]+"/)) {
                field = substr($0, RSTART, RLENGTH)
                sub(/.*:[[:space:]]*"/, "", field)
                sub(/"$/, "", field)
                print field
                exit
            }
        }
    ' "$1"
}

# `cwd` is the session's working directory, which is what `dotnet tool run` inherits --
# read it from the hook's stdin payload rather than using $PWD, which is the hook's own.
payload=$(cat 2>/dev/null || true)
cwd=$(printf '%s' "$payload" | tr ',{}' '\n\n\n' | awk -F'"' '/"cwd"[[:space:]]*:/ { print $4; exit }')
[ -n "${cwd:-}" ] || cwd=$(pwd)

manifest=$(find_manifest "$cwd") || exit 0        # not a repo with local tools
pinned=$(manifest_version "$manifest")
[ -n "${pinned:-}" ] || exit 0                    # manifest exists, Cosy not among its tools

shipped=$(plugin_version "${CLAUDE_PLUGIN_ROOT:-}/.claude-plugin/plugin.json") || exit 0
[ -n "${shipped:-}" ] || exit 0

[ "$pinned" = "$shipped" ] && exit 0              # the ordinary case: silent

# Skew. Report it as plainly as possible and name the one command that fixes it; do not
# run that command here. Rewriting a consumer's manifest as a side effect of opening a
# session is a change they did not ask for, and `/cosy:update` exists to make it explicit.
printf '{"systemMessage":"Cosy version skew: the tool manifest at %s pins %s, but the installed cosy plugin ships against %s. The MCP server answering this session is %s, NOT %s -- `claude plugin update` cannot change that, because the plugin launches `dotnet tool run cosy-mcp` and the manifest decides the build. Run /cosy:update to re-pin, then restart Claude Code (the running server holds the old build until then).","suppressOutput":false}\n' \
    "$manifest" "$pinned" "$shipped" "$pinned" "$shipped"
exit 0
