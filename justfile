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

# Install into DIR's local tool manifest — the path the Claude Code plugin needs.
install-into dir: pack
    #!/usr/bin/env bash
    set -euo pipefail
    # DIR is your own C# repository, not this one:  just install-into ~/work/my-api
    #
    # The plugin launches the server as `dotnet tool run cosy-mcp`, which resolves a tool
    # manifest by walking up from the directory it runs in — hence a per-consumer-repo
    # install rather than the global one above.
    cd "{{dir}}"
    # The manifest lands at .config/dotnet-tools.json on SDK 8 and at the directory root on
    # SDK 10. Either is resolved the same way, so check both before creating a second one.
    if [ ! -f dotnet-tools.json ] && [ ! -f .config/dotnet-tools.json ]; then
      dotnet new tool-manifest
    fi
    dotnet tool update --add-source "{{nupkg}}" Cosy.Mcp
    # SDK 8 needs this; SDK 10 auto-restores and it is a no-op there.
    dotnet tool restore
    # Ending on doctor means a broken install says so here, rather than as a silent MCP
    # failure three steps later. A non-zero exit is doctor's own: 3 manifest, 4 tool_run,
    # 5 version mismatch.
    dotnet tool run cosy-mcp doctor --plugin-root "{{plugin}}"

# Re-check an existing install in DIR, changing nothing.
doctor dir:
    cd "{{dir}}" && dotnet tool run cosy-mcp doctor --plugin-root "{{plugin}}"
