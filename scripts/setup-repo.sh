#!/usr/bin/env bash
# Bring a repository to a working Cosy plugin install -- first time, or after a `git pull`.
#
#   scripts/setup-repo.sh                      # this checkout
#   scripts/setup-repo.sh ~/work/my-api        # someone else's C# repo
#   scripts/setup-repo.sh ~/work/my-api --tracing
#
# WHY BASH AND NOT `lk`. Some machines this has to run on are Windows, where this repo's
# usual `lk` does not run; Git Bash does (see the caveats at the bottom). The one step that
# genuinely needs a JSON implementation -- editing ~/.claude/settings.json -- is delegated to
# `cosy-mcp settings set-trace-path`, a subcommand of the binary this script has already
# installed by then. So the only prerequisites stay: a .NET SDK, git bash, and (for the plugin
# steps) the `claude` CLI.
#
# IT IS THE UPDATE PATH TOO, not just first-time install. Every mutating step is chosen so a
# second run is a no-op rather than an error:
#   * `dotnet tool update` installs when absent and re-pins when present (`install` fails when
#     the tool is already there);
#   * the marketplace is added only when missing, otherwise refreshed;
#   * the plugin is installed when absent, updated when its version differs, skipped when it
#     already matches;
#   * the settings edit refuses rather than overwrites, and is a no-op when already correct.
# The intended post-pull flow is therefore: `git pull && scripts/setup-repo.sh`.
#
# FOUR THINGS CARRY THE VERSION and only three used to be guarded (see
# docs/decisions/0011-packaging-and-distribution.md):
#   1. src/Cosy.Mcp/Cosy.Mcp.csproj              -- what the nupkg is named for
#   2. plugins/cosy/.claude-plugin/plugin.json   -- what the marketplace publishes
#   3. the consumer's dotnet-tools.json          -- what `dotnet tool run cosy-mcp` RESOLVES
#   4. the INSTALLED plugin (`claude plugin list`) -- what Claude Code actually LOADS
# (4) is a `claude` CLI action outside the build, and `doctor --plugin-root` cannot see it: it
# compares the running binary against THIS repo's plugin.json, never against the copy Claude
# Code installed into its own cache. Measured 2026-09-10: the installed plugin sat at 0.1.7
# through two releases while the manifest pinned 0.1.9, with `doctor` reporting version: pass.
# Asserting (4) is this script's real job.

set -euo pipefail

SELF_DIR=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$SELF_DIR/.." && pwd)

CSPROJ="$ROOT/src/Cosy.Mcp/Cosy.Mcp.csproj"
PLUGIN_JSON="$ROOT/plugins/cosy/.claude-plugin/plugin.json"
PLUGIN_DIR="$ROOT/plugins/cosy"
NUPKG_DIR="$ROOT/artifacts/nupkg"
MARKETPLACE="cosy-mcp"          # .claude-plugin/marketplace.json `name`
PLUGIN_ID="cosy@$MARKETPLACE"

TARGET="$ROOT"
SCOPE="user"
TRACING=""                      # empty = leave tracing alone (ADR-0007: opt-in)
DRY_RUN=0
NO_PACK=0
SKIP_PLUGIN=0

TRACE_PREFIX_DEFAULT="$HOME/.local/cosy/traces/cosy-trace"

die() { printf 'setup-repo: %s\n' "$*" >&2; exit 1; }
say() { printf '%s\n' "$*"; }

usage() {
    cat <<'EOF'
setup-repo.sh [DIR] [--tracing[=PREFIX]] [--scope user|project|local] [--dry-run] [--no-pack]

  DIR                 the repository to set up (default: this Cosy checkout)
  --tracing[=PREFIX]  also switch on trace collection for THIS MACHINE by setting
                      env.COSY_TRACE_PATH in ~/.claude/settings.json. Default prefix:
                      ~/.local/cosy/traces/cosy-trace. Off unless asked for.
  --scope             plugin install scope passed to `claude plugin` (default: user)
  --dry-run           report what would change; pack, install, update and write nothing
  --no-pack           never run `dotnet pack`; fail if the pinned version has no package
  --skip-plugin       set up the tool manifest (and tracing) only; touch no marketplace or
                      plugin. For a worktree whose marketplace belongs to the main checkout.
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --help|-h) usage; exit 0 ;;
        --dry-run) DRY_RUN=1 ;;
        --no-pack) NO_PACK=1 ;;
        --skip-plugin) SKIP_PLUGIN=1 ;;
        --tracing) TRACING="$TRACE_PREFIX_DEFAULT" ;;
        --tracing=*) TRACING="${1#--tracing=}" ;;
        --scope) [ $# -ge 2 ] || die "--scope requires a value"; SCOPE="$2"; shift ;;
        --scope=*) SCOPE="${1#--scope=}" ;;
        -*) die "unrecognised argument '$1' (--help for usage)" ;;
        *)
            # A Windows path pasted with its trailing backslash would otherwise break the
            # quoting downstream, exactly as scripts/install-into.sh documents.
            TARGET="${1%\\}"
            ;;
    esac
    shift
done

command -v dotnet >/dev/null 2>&1 || die "dotnet is not on PATH; a .NET SDK is the one hard prerequisite"
[ -d "$TARGET" ] || die "target '$TARGET' is not a directory"
TARGET=$(cd "$TARGET" && pwd)

run() {
    if [ "$DRY_RUN" -eq 1 ]; then
        printf '  would run: %s\n' "$*"
        return 0
    fi
    "$@"
}

# --- 1. version floor -------------------------------------------------------------------
# Refuse to act on drift that already exists in the checkout: reconciling it by picking a
# side destroys the evidence that the two ever differed.

first_json_version() {   # $1 = file; prints the first "version": "X.Y.Z"
    awk '
        /"version"[[:space:]]*:/ {
            if (match($0, /"version"[[:space:]]*:[[:space:]]*"[^"]+"/)) {
                field = substr($0, RSTART, RLENGTH)
                sub(/.*:[[:space:]]*"/, "", field); sub(/"$/, "", field)
                print field; exit
            }
        }' "$1"
}

manifest_path() {        # $1 = repo dir; prints the tool manifest it would resolve, if any
    if   [ -f "$1/.config/dotnet-tools.json" ]; then printf '%s\n' "$1/.config/dotnet-tools.json"
    elif [ -f "$1/dotnet-tools.json" ];         then printf '%s\n' "$1/dotnet-tools.json"
    fi
}

manifest_version() {     # $1 = manifest file; prints the cosy.mcp entry's version
    awk '
        /"cosy\.mcp"/ { in_entry = 1 }
        in_entry && /"version"[[:space:]]*:/ {
            if (match($0, /"version"[[:space:]]*:[[:space:]]*"[^"]+"/)) {
                field = substr($0, RSTART, RLENGTH)
                sub(/.*:[[:space:]]*"/, "", field); sub(/"$/, "", field)
                print field; exit
            }
        }' "$1"
}

[ -f "$CSPROJ" ] || die "$CSPROJ is missing -- run this from a Cosy checkout"
WANT=$(sed -n 's:.*<Version>[[:space:]]*\([0-9][0-9.]*\)[[:space:]]*</Version>.*:\1:p' "$CSPROJ" | head -n1)
[ -n "$WANT" ] || die "no <Version>X.Y.Z</Version> in $CSPROJ"

if [ -f "$PLUGIN_JSON" ]; then
    PLUGIN_VERSION=$(first_json_version "$PLUGIN_JSON")
    [ -n "$PLUGIN_VERSION" ] || die "no \"version\" in $PLUGIN_JSON"
    [ "$PLUGIN_VERSION" = "$WANT" ] || die \
"version drift inside the checkout: csproj is $WANT but plugin.json is $PLUGIN_VERSION.
  These are bumped in lockstep by \`mise run release\`; reconcile them before continuing."
fi

say "cosy.mcp $WANT  (checkout: $ROOT)"
say "target repository: $TARGET"
[ "$DRY_RUN" -eq 1 ] && say "(dry run)"

# --- 2. a package for the pinned version has to exist somewhere restore can find it -----
# artifacts/ is gitignored, so a `git pull` that advances the pin brings the PIN but not the
# PACKAGE: on the machine that cut the release it is already there, on any other it is not.

NUPKG="$NUPKG_DIR/Cosy.Mcp.$WANT.nupkg"
EXTRACTED="$HOME/.nuget/packages/cosy.mcp/$WANT"

if [ -f "$NUPKG" ] || [ -d "$EXTRACTED" ]; then
    say "  package $WANT: present"
elif [ "$NO_PACK" -eq 1 ]; then
    die "no package for $WANT (looked in $NUPKG_DIR and $EXTRACTED), and --no-pack was given"
else
    say "  package $WANT: missing -- packing"
    run dotnet pack "$CSPROJ" -c Release
    if [ "$DRY_RUN" -eq 0 ] && [ ! -f "$NUPKG" ]; then
        die "pack reported success but $NUPKG is missing"
    fi
fi

# --- 3. the consumer's local tool manifest, PINNED ---------------------------------------
# `--add-source` ADDS a source, it does not constrain selection: without `--version` a higher
# version in that directory or in any configured feed wins, and the consumer's manifest is
# rewritten to something this checkout never verified.

EXISTING_MANIFEST=$(manifest_path "$TARGET")
if [ -z "$EXISTING_MANIFEST" ]; then
    say "  tool manifest: creating one in $TARGET"
    ( cd "$TARGET" && run dotnet new tool-manifest )
else
    PINNED=$(manifest_version "$EXISTING_MANIFEST")
    if [ -n "$PINNED" ]; then
        if [ "$PINNED" = "$WANT" ]; then
            say "  tool manifest: already pins $WANT"
        else
            # Refuse to move a consumer BACKWARDS. `dotnet tool update --version` would
            # happily downgrade, and a consumer running a newer build than this checkout is a
            # state to report, not to quietly undo.
            NEWEST=$(printf '%s\n%s\n' "$PINNED" "$WANT" | sort -V | tail -n1)
            if [ "$NEWEST" = "$PINNED" ]; then
                die "$EXISTING_MANIFEST pins $PINNED, which is NEWER than this checkout's $WANT.
  Refusing to downgrade it. Pull this repo, or pass the older checkout deliberately."
            fi
            say "  tool manifest: $PINNED -> $WANT"
        fi
    fi
fi

if [ -d "$NUPKG_DIR" ]; then
    ( cd "$TARGET" && run dotnet tool update --add-source "$NUPKG_DIR" --version "$WANT" Cosy.Mcp )
else
    ( cd "$TARGET" && run dotnet tool update --version "$WANT" Cosy.Mcp )
fi
# SDK 8 needs this; SDK 10 auto-restores and it is a no-op there.
( cd "$TARGET" && run dotnet tool restore )

if [ "$DRY_RUN" -eq 0 ]; then
    AFTER_MANIFEST=$(manifest_path "$TARGET")
    [ -n "$AFTER_MANIFEST" ] || die "no tool manifest in $TARGET after the install"
    AFTER_PINNED=$(manifest_version "$AFTER_MANIFEST")
    [ "$AFTER_PINNED" = "$WANT" ] || die \
"$AFTER_MANIFEST pins '$AFTER_PINNED' after the install, expected '$WANT'"
    say "  tool manifest: pins $WANT ($AFTER_MANIFEST)"
fi

# --- 4. marketplace and plugin ------------------------------------------------------------
# Both steps read current state first, so this is the update path as much as the install one.

if [ "$SKIP_PLUGIN" -eq 1 ]; then
    say "  marketplace/plugin: skipped (--skip-plugin); the tool manifest above is still pinned"
elif ! command -v claude >/dev/null 2>&1; then
    say "  claude CLI: not on PATH -- skipping the marketplace and plugin steps"
    say "               (the tool is installed; run this again from a machine with Claude Code)"
else
    if claude plugin marketplace list 2>/dev/null | grep -qE "(^|[[:space:]])$MARKETPLACE\$"; then
        # A marketplace of this name pointing at a DIFFERENT clone is fatal, not cosmetic, and
        # measured so on 2026-09-17: `claude plugin update cosy` answered "already at the latest
        # version (0.1.9)" while this checkout was 0.1.10, because the AVAILABLE version is
        # whatever that other tree's plugin.json says. The install then cannot reach this
        # checkout's version at all, so refusing early with the remedy beats failing at the
        # assertion below with a misleading "update did not take".
        MARKET_SOURCE=$(claude plugin marketplace list 2>/dev/null | awk -v id="$MARKETPLACE" '
            $0 ~ ("(^|[[:space:]])" id "$") { in_block = 1; next }
            in_block && /Source:/ { sub(/.*Source:[[:space:]]*/, ""); print; exit }
        ')
        case "$MARKET_SOURCE" in
            *"$ROOT"*|"") : ;;
            *) die "marketplace $MARKETPLACE is registered against a different checkout:
    $MARKET_SOURCE
  This one is:
    $ROOT
  The plugin version offered comes from THERE, so this checkout's $WANT is unreachable.
  Either re-point it:
    claude plugin marketplace remove $MARKETPLACE && claude plugin marketplace add $ROOT
  or skip the plugin steps entirely and set up only the tool and tracing:
    $0 $TARGET --skip-plugin${TRACING:+ --tracing=$TRACING}" ;;
        esac
        say "  marketplace $MARKETPLACE: refreshing"
        run claude plugin marketplace update "$MARKETPLACE"
    else
        say "  marketplace $MARKETPLACE: adding $ROOT"
        run claude plugin marketplace add "$ROOT" --scope "$SCOPE"
    fi

    # `claude plugin list` prints an indented block per plugin:
    #     ❯ cosy@cosy-mcp
    #       Version: 0.1.9
    #       Scope: user
    #       Status: ✔ enabled
    # Read that block rather than `--json`, because this script has no JSON parser and the
    # block is line-oriented. Scope and status are read too: a project-scoped or disabled
    # entry must not be mistaken for a working user-scoped install.
    plugin_field() {     # $1 = field name; prints its value from the PLUGIN_ID block
        claude plugin list 2>/dev/null | awk -v id="$PLUGIN_ID" -v field="$1" '
            index($0, id) { in_block = 1; next }
            in_block && $0 ~ ("^[[:space:]]*" field ":") {
                sub("^[[:space:]]*" field ":[[:space:]]*", ""); print; exit
            }
            in_block && /^[[:space:]]*.[[:space:]]/ && !/:/ { exit }
        '
    }

    INSTALLED_VERSION=$(plugin_field Version || true)
    if [ -z "$INSTALLED_VERSION" ]; then
        say "  plugin $PLUGIN_ID: not installed -- installing"
        run claude plugin install "$PLUGIN_ID" --scope "$SCOPE" -y
    elif [ "$INSTALLED_VERSION" != "$WANT" ]; then
        say "  plugin $PLUGIN_ID: $INSTALLED_VERSION -> $WANT"
        run claude plugin update cosy --scope "$SCOPE" -y
    else
        say "  plugin $PLUGIN_ID: already $WANT"
    fi

    if [ "$DRY_RUN" -eq 0 ]; then
        FINAL_VERSION=$(plugin_field Version || true)
        FINAL_SCOPE=$(plugin_field Scope || true)
        FINAL_STATUS=$(plugin_field Status || true)
        [ "$FINAL_VERSION" = "$WANT" ] || die \
"the installed plugin reports '$FINAL_VERSION' but this checkout is '$WANT'.
  \`claude plugin update cosy\` did not take. Check: claude plugin list"
        [ "$FINAL_SCOPE" = "$SCOPE" ] || die \
"the installed plugin is at scope '$FINAL_SCOPE', not the requested '$SCOPE'.
  Remove it at that scope first if you meant to move it."
        case "$FINAL_STATUS" in
            *enabled*) : ;;
            *) die "the installed plugin is '$FINAL_STATUS'. Enable it deliberately -- this
  script will not flip it for you." ;;
        esac
        say "  plugin $PLUGIN_ID: $FINAL_VERSION, scope $FINAL_SCOPE, $FINAL_STATUS"
    fi
fi

# --- 5. tracing, only when asked ----------------------------------------------------------
# ADR-0007 keeps tracing opt-in, so the default is to leave settings.json alone. The edit is
# delegated to the binary installed above, which is what makes this work on Windows.
#
# NOTE ON SCOPE vs ENVIRONMENT. This writes Claude Code's settings, which is where its MCP
# servers get the variable from (measured 2026-09-17: settings env reaches a plugin-shipped
# stdio server, proven by a trace file appearing after one real tool call).
#
# Whether `doctor`'s `tracing` line agrees depends on WHO launched this shell, and both answers
# are correct: run from a shell Claude Code spawned, the variable is inherited and doctor says
# `tracing: on`; run from a plain terminal it is not, and doctor says `off` while Claude Code's
# own servers still trace. Either way doctor reports the VARIABLE, never a written file -- only
# a trace file appearing proves collection (ADR-0015).

if [ -n "$TRACING" ]; then
    # GUARD, and not a theoretical one: an UNKNOWN subcommand falls through CliDispatch to the
    # stdio server, which reads stdin and exits 0 -- so a build predating `settings` reports
    # SUCCESS while writing nothing. Measured 2026-09-17 against the pinned 0.1.9: the tracing
    # step printed its success banner with the settings file untouched. `check-tool-version.sh`
    # records the same fallthrough for a hypothetical `update` subcommand.
    #
    # `</dev/null` everywhere below for the same reason: if the fallthrough ever happens again,
    # the server must hit EOF and exit rather than hang a setup script forever.
    if [ "$DRY_RUN" -eq 1 ]; then
        # Nothing was installed in a dry run, so `dotnet tool run` has nothing to resolve and
        # the guard below cannot distinguish "no such subcommand" from "no tool yet". Say what
        # would happen and stay honest about what was not checked.
        say "  tracing: would set env.COSY_TRACE_PATH = $TRACING in ~/.claude/settings.json"
        say "           (not probed: a dry run installs no tool to ask)"
    else
        probe_out=$( cd "$TARGET" && dotnet tool run cosy-mcp settings set-trace-path "$TRACING" \
                         --dry-run </dev/null 2>&1 || true )
        case "$probe_out" in
            *"settings:"*) : ;;
            *) die "the Cosy build this repo pins ($WANT) has no \`settings\` subcommand, so the
  tracing step cannot run: an unknown subcommand falls through to the stdio server and exits 0,
  which would look like success. Bump and repack from the checkout (\`mise run release\`), re-run
  this script, and only then pass --tracing." ;;
        esac

        say "  tracing: setting env.COSY_TRACE_PATH = $TRACING"
        set +e
        ( cd "$TARGET" && dotnet tool run cosy-mcp settings set-trace-path "$TRACING" </dev/null )
        settings_exit=$?
        set -e
        case "$settings_exit" in
            0) : ;;
            6) die "settings already set a different COSY_TRACE_PATH (exit 6) -- see the message above" ;;
            7) die "~/.claude/settings.json is not in a shape this can edit (exit 7)" ;;
            8) die "settings.json is locked or changed underneath (exit 8) -- re-run" ;;
            *) die "cosy-mcp settings failed (exit $settings_exit)" ;;
        esac
    fi
else
    say "  tracing: left alone (pass --tracing to switch it on for this machine)"
fi

# --- 6. verify the install the way a consumer experiences it ------------------------------
# doctor's exit code IS the assertion: 3 manifest, 4 tool_run, 5 version mismatch.

if [ "$DRY_RUN" -eq 0 ]; then
    say ""
    say "verifying"
    ( cd "$TARGET" && dotnet tool run cosy-mcp doctor --plugin-root "$PLUGIN_DIR" )
fi

say ""
if [ "$DRY_RUN" -eq 1 ]; then
    say "(dry run -- nothing packed, installed, updated or written)"
    exit 0
fi

say "cosy $WANT is installed in $TARGET and the tool manifest resolves it."
say "RESTART Claude Code before any of it takes effect -- a running MCP server holds its old"
say "dll for the life of the session."
if [ -n "$TRACING" ]; then
    say ""
    say "Then PROVE tracing works, because a set variable is not a written file: make one Cosy"
    say "tool call and confirm a file appears under"
    say "    $TRACING.<session>.jsonl"
    say "and that its records parse. doctor's tracing check reads the variable, not the sink."
fi
