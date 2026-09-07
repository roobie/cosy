# Cosy.Mcp — build and install, in one command each.
#
# `just` is optional. Every recipe below is a couple of plain dotnet commands, and the
# README spells them out for anyone who would rather not install a task runner.
#
# Why `update` and never `install`: `dotnet tool install` fails when the tool is already
# there, so a second run of a recipe would error on a machine where the first succeeded.
# `dotnet tool update` installs when the tool is absent and re-pins when it is present —
# verified on SDK 8.0.420 and SDK 10.0.202, locally and globally. That makes every recipe
# here re-runnable, which matters because the whole point is to run one after a `git pull`.
#
# Note on comments: `just --list` shows only the LAST comment line above a recipe, so each
# recipe below gets a one-line summary there and any detail lives inside its body.

# just defaults to `sh` even on Windows, which is not there on a stock machine.
#
# This is the DEPRECATED spelling. just's current recommendation is an attribute on the
# shell setting:
#
#     [windows]
#     set shell := ["pwsh.exe", "-NoLogo", "-Command"]
#
# but attributes on *settings* need just 1.56.0 — they were recipe-only before that — and
# an older just rejects the file outright, on every platform, not just Windows. For a
# repository whose whole job is being easy to install from, raising the floor for everyone
# to fix one platform is the wrong trade. Switch to the attribute form once 1.56 is
# ordinary; `windows-shell` still works and is only documented as superseded.
#
# pwsh.exe is PowerShell 7+, not the bundled Windows PowerShell 5 (powershell.exe). The
# `doctor` recipe below chains with `&&`, which PowerShell only understands from 7.
set windows-shell := ["pwsh.exe", "-NoLogo", "-Command"]

nupkg  := justfile_directory() + "/artifacts/nupkg"
csproj := justfile_directory() + "/src/Cosy.Mcp/Cosy.Mcp.csproj"
plugin := justfile_directory() + "/plugins/cosy"

# List the recipes.
default:
    @just --list

# Build the NuGet package into artifacts/nupkg/ — everything else depends on this.
pack:
    dotnet pack {{csproj}} -c Release

# Install `cosy-mcp` globally, for MCP clients that launch the binary directly.
install-global: pack
    # dotnet may warn that ~/.dotnet/tools is not on your PATH. That is its message, not
    # ours, and it tells you the line to add.
    dotnet tool update --global --add-source {{nupkg}} Cosy.Mcp

# Remove the global install; leaves every repo's local tool manifest alone.
uninstall-global:
    dotnet tool uninstall --global Cosy.Mcp

# DIR is your own C# repository, not this one:  just install-into ~/work/my-api
#
# The plugin launches the server as `dotnet tool run cosy-mcp`, which resolves a tool
# manifest by walking up from the directory it runs in — hence a per-consumer-repo
# install rather than the global one above. The recipe body lives in
# scripts/install-into.sh — see that file for the Windows/bash caveats and the
# trailing-backslash quoting note.
# Install into DIR's local tool manifest — the path the Claude Code plugin needs (bash).
install-into dir: pack
    bash "{{justfile_directory()}}/scripts/install-into.sh" "{{dir}}" "{{nupkg}}" "{{plugin}}"

# Re-check an existing install in DIR, changing nothing.
doctor dir:
    cd "{{dir}}" && dotnet tool run cosy-mcp doctor --plugin-root "{{plugin}}"
