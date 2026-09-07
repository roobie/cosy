#!/usr/bin/env bash
set -euo pipefail
# Body of the justfile's `install-into` recipe, factored out because a multi-step
# shell script reads better as a file than as a `just` shebang-recipe block.
#
# ON WINDOWS THIS SCRIPT NEEDS bash ON PATH (Git for Windows or WSL). The justfile's
# `windows-shell` setting does not help here: that setting controls how `just` runs a
# recipe LINE, and the `install-into` recipe's line is exactly `bash this-script.sh`,
# which still needs a real bash to exist regardless of what ran it.
#
# A stock Windows machine can have a `bash` on PATH that resolves to the WSL launcher
# stub at C:\Windows\System32\bash.exe rather than Git for Windows' bash — that stub
# fails or behaves differently here, and lacks the `cygpath` Git bash carries. If this
# script can't find a working bash, prepend Git's own bin dir (typically
# `C:\Program Files\Git\usr\bin`) to PATH so its bash and cygpath resolve first.
#
# Windows without bash: run the four commands from the README by hand in pwsh. A
# PowerShell translation of this script is not shipped because it could not be tested
# here, and an untested install path is worse than a documented manual one.
#
# Args, all supplied by the justfile recipe:
#   $1 dir    — the consumer's own C# repo, not this one
#   $2 nupkg  — the local nupkg feed directory (`just pack`'s output)
#   $3 plugin — the plugin root, passed to `doctor --plugin-root`
dir="$1"
nupkg="$2"
plugin="$3"

# Strip a trailing backslash before it ever reaches a quoted string below: `just`
# substitutes $1 verbatim onto this script's command line, so a Windows-style path
# pasted with its trailing `\` (e.g. `C:\work\my-api\`) arrives here still carrying
# it, and would otherwise produce `cd "C:\work\my-api\"` — inside bash double quotes,
# `\"` is an escaped literal quote, not a closing one, so the string never closes and
# bash fails with "unexpected EOF while looking for matching `"'" instead of running.
dir="${dir%\\}"
cd "$dir"
# The manifest lands at .config/dotnet-tools.json on SDK 8 and at the directory root on
# SDK 10. Either is resolved the same way, so check both before creating a second one.
if [ ! -f dotnet-tools.json ] && [ ! -f .config/dotnet-tools.json ]; then
  dotnet new tool-manifest
fi
dotnet tool update --add-source "$nupkg" Cosy.Mcp
# SDK 8 needs this; SDK 10 auto-restores and it is a no-op there.
dotnet tool restore
# Ending on doctor means a broken install says so here, rather than as a silent MCP
# failure three steps later. A non-zero exit is doctor's own: 3 manifest, 4 tool_run,
# 5 version mismatch.
dotnet tool run cosy-mcp doctor --plugin-root "$plugin"
