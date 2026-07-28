#!/usr/bin/env bash
# Installs the BAPBAP mod manager (Linux / Proton).
#
# This installs the manager only. Individual mods are meant to be fetched from the in-game
# downloader, so anything listed in dist/manifest.json is deliberately just the manager.
#
#   curl -fsSL https://raw.githubusercontent.com/ItsKarlin/bapbap-modkit/main/install/install.sh | bash
#
# Options (as env vars):
#   GAME_DIR=/path/to/BAPBAP    skip auto-detection
#   REF=main                    branch or tag to install from
#   FORCE=1                     overwrite differing DLLs without asking
#   INSTALL_LOADER=1            install MelonLoader without asking, if it's missing
#
# Every DLL is verified against the sha256 in the repo manifest before it is written.

set -euo pipefail

REPO="ItsKarlin/bapbap-modkit"
REF="${REF:-main}"
BASE_URL="https://raw.githubusercontent.com/${REPO}/${REF}"
APPID=2226280

# MelonLoader is pinned to the CI build the BAPHub launcher uses. The public 0.7.3 release is
# NOT interchangeable: BAPHub's mods are built against this one. Windows x64 is correct even on
# Linux — the game binary is a Windows PE running under Proton.
ML_VERSION="0.7.2-ci.2388"
ML_URL="https://raw.githubusercontent.com/Sonic0810/BAPBAPLauncher/main/melonloader/${ML_VERSION}/MelonLoader.Windows.x64.CI.Release.zip"
ML_SHA256="2c3bf21c06dd6248f47514be4468abd56f7aaa3800ea3b6aedad0a64eb4366a8"

step() { printf '\033[36m==> %s\033[0m\n' "$1"; }
ok()   { printf '\033[32m    %s\033[0m\n' "$1"; }
warn() { printf '\033[33m    %s\033[0m\n' "$1"; }
die()  { printf '\033[31m%s\033[0m\n' "$1" >&2; exit 1; }

for cmd in curl sha256sum; do
    command -v "$cmd" >/dev/null 2>&1 || die "'$cmd' is required but not installed."
done

# ------------------------------------------------------------------ find the game

find_game_dir() {
    local roots=(
        "$HOME/.local/share/Steam"
        "$HOME/.steam/steam"
        "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    )

    # Pick up extra library folders Steam knows about.
    local vdf lib
    for root in "${roots[@]}"; do
        vdf="$root/steamapps/libraryfolders.vdf"
        [ -f "$vdf" ] || continue
        while IFS= read -r lib; do
            [ -n "$lib" ] && roots+=("$lib")
        done < <(grep -oP '"path"\s+"\K[^"]+' "$vdf" 2>/dev/null || true)
    done

    local manifest installdir candidate
    for root in "${roots[@]}"; do
        # Trust the appmanifest over a guessed folder name.
        manifest="$root/steamapps/appmanifest_${APPID}.acf"
        if [ -f "$manifest" ]; then
            installdir=$(grep -oP '"installdir"\s+"\K[^"]+' "$manifest" 2>/dev/null | head -1 || true)
            if [ -n "$installdir" ] && [ -d "$root/steamapps/common/$installdir" ]; then
                printf '%s\n' "$root/steamapps/common/$installdir"
                return 0
            fi
        fi
        candidate="$root/steamapps/common/BAPBAP"
        [ -d "$candidate" ] && { printf '%s\n' "$candidate"; return 0; }
    done
    return 1
}

step "Locating BAPBAP"
GAME_DIR="${GAME_DIR:-$(find_game_dir || true)}"
[ -n "$GAME_DIR" ] && [ -d "$GAME_DIR" ] || die "Could not find BAPBAP. Re-run with GAME_DIR=/path/to/BAPBAP"
ok "$GAME_DIR"

# ------------------------------------------------------------------ MelonLoader

install_melonloader() {
    command -v unzip >/dev/null 2>&1 || die "'unzip' is required to install MelonLoader."

    local zip="$STAGING_ML/MelonLoader-${ML_VERSION}.zip"
    printf '    downloading %s (18 MB)...\n' "$ML_VERSION"
    curl -fsSL "$ML_URL" -o "$zip" || die "MelonLoader download failed."

    local got
    got=$(sha256sum "$zip" | cut -d' ' -f1)
    if [ "$got" != "$ML_SHA256" ]; then
        die "sha256 MISMATCH on the MelonLoader download — refusing to extract.
  expected $ML_SHA256
  got      $got"
    fi
    ok "sha256 verified"

    # The archive is laid out to drop straight into the game folder: MelonLoader/ + version.dll
    unzip -oq "$zip" -d "$GAME_DIR" || die "Could not extract MelonLoader into $GAME_DIR"
    ok "MelonLoader $ML_VERSION installed"
    LOADER_WAS_INSTALLED=1
}

step "Checking MelonLoader"
STAGING_ML=$(mktemp -d)
STAGING=""
cleanup() { rm -rf "$STAGING_ML" ${STAGING:+"$STAGING"}; }
trap cleanup EXIT
LOADER_WAS_INSTALLED=0

if [ -d "$GAME_DIR/MelonLoader" ] && [ -f "$GAME_DIR/version.dll" ]; then
    # The loader writes its version into the log on every run — cheapest way to see what's there.
    installed=""
    if [ -f "$GAME_DIR/MelonLoader/Latest.log" ]; then
        installed=$(grep -oP 'MelonLoader v\K\S+' "$GAME_DIR/MelonLoader/Latest.log" 2>/dev/null | head -1 || true)
    fi

    if [ -n "$installed" ] && [ "$installed" != "$ML_VERSION" ]; then
        warn "found v$installed, but BAPHub's mods are built against $ML_VERSION"
        warn "leaving it alone — replace it yourself if BAPHub mods misbehave"
    elif [ -n "$installed" ]; then
        ok "v$installed"
    else
        ok "found (version unknown — the game hasn't been launched with it yet)"
    fi
else
    warn "not installed"
    printf '    MelonLoader %s is required. This is the build the BAPHub launcher pins,\n' "$ML_VERSION"
    printf '    not the public 0.7.3 release, and it will be verified by sha256.\n'

    if [ "${INSTALL_LOADER:-0}" = "1" ]; then
        install_melonloader
    else
        read -r -p "    Download and install it now? [Y/n] " answer < /dev/tty || answer=n
        case "$answer" in
            [Nn]*) die "Nothing installed. Get MelonLoader yourself and re-run this script." ;;
            *)     install_melonloader ;;
        esac
    fi
fi

MODS_DIR="$GAME_DIR/Mods"
mkdir -p "$MODS_DIR"

# ------------------------------------------------------------------ fetch + verify

step "Fetching manifest"
STAGING=$(mktemp -d)

curl -fsSL "$BASE_URL/dist/manifest.json" -o "$STAGING/manifest.json" \
    || die "Could not download the manifest."

# Small dependency-free read of the flat manifest: one "file sha256" pair per mod.
mapfile -t ENTRIES < <(
    tr -d ' \t' < "$STAGING/manifest.json" \
    | grep -oP '"(file|sha256)":"\K[^"]+' \
    | paste - -
)
[ "${#ENTRIES[@]}" -gt 0 ] || die "Manifest looks empty or malformed."
ok "${#ENTRIES[@]} mods listed"

VERIFIED=()
for entry in "${ENTRIES[@]}"; do
    file=$(printf '%s' "$entry" | cut -f1)
    want=$(printf '%s' "$entry" | cut -f2)

    step "$file"
    curl -fsSL "$BASE_URL/dist/$file" -o "$STAGING/$file" || die "Download failed for $file"

    got=$(sha256sum "$STAGING/$file" | cut -d' ' -f1)
    if [ "$got" != "$want" ]; then
        die "sha256 MISMATCH for $file — refusing to install.
  expected $want
  got      $got"
    fi
    ok "sha256 verified"
    VERIFIED+=("$file|$want")
done

# Only start touching Mods/ once every download has passed verification.
for item in "${VERIFIED[@]}"; do
    file="${item%%|*}"
    want="${item##*|}"
    dest="$MODS_DIR/$file"

    if [ -f "$dest" ] && [ "${FORCE:-0}" != "1" ]; then
        have=$(sha256sum "$dest" | cut -d' ' -f1)
        if [ "$have" = "$want" ]; then
            warn "$file already up to date"
            continue
        fi
        # Read from the terminal, not stdin: this script is usually piped into bash.
        read -r -p "    $file exists and differs. Replace it? [y/N] " answer < /dev/tty || answer=n
        case "$answer" in [Yy]*) ;; *) warn "skipped"; continue ;; esac
    fi

    cp -f "$STAGING/$file" "$dest"
    ok "installed $file"
done

printf '\n\033[32mDone. Launch BAPBAP and press F5, or click MODS in the top nav bar.\033[0m\n'
printf 'Mods are only loaded at startup, so restart the game if it is already running.\n'

if [ "$LOADER_WAS_INSTALLED" = "1" ]; then
    printf '\n\033[33mOne more thing, since MelonLoader was just installed:\033[0m\n'
    printf 'Set this Steam launch option for BAPBAP, or Proton ignores the loader silently and\n'
    printf 'nothing will load with no error to tell you why:\n\n'
    printf '    WINEDLLOVERRIDES="version=n,b" %%command%%\n\n'
    printf 'Right-click BAPBAP in Steam -> Properties -> Launch Options.\n'
fi
