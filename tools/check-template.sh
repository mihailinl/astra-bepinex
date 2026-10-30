#!/bin/bash
# Generate repositories from templates/astra-integration — with a game name, an exe and a game folder
# that hold every character that breaks a C# or TOML string or an XML file — and check that what
# comes out builds, parses and is what Astra's launcher reads: the Mono one is fetched (tools/fetch.sh:
# network), built with warnings as errors and packed into astra-gi.zip; both runtimes' manifests parse
# as TOML; the release workflow pins every action by commit. An --id or --owner that is not one is
# REFUSED: nothing is written, and dotnet new prints why. The template is installed into a PRIVATE template cache (dotnet new's
# --debug:custom-hive), never into yours.
#
# Usage: tools/check-template.sh
#   ASTRA_GAME_LAUNCHER_REPO  astra-game-launcher's checkout, built fresh into a scratch target dir
#                             and never written to (default: ../astra-game-launcher). Its `inspect`
#                             is run against the packed zip below — a real launcher check, not just
#                             "does this TOML parse": a [[files]] rule matching nothing in the zip
#                             (e.g. a chain/** rule the pinned runner release does not ship yet) is
#                             "not in the zip" to the launcher and would get every integration built
#                             from this template refused at install. Missing the repo prints SKIP.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
HIVE=(--debug:custom-hive "$W/hive")
fail() { echo "check-template: $*" >&2; exit 1; }

dotnet new install "$REPO/templates/astra-integration" "${HIVE[@]}" >/dev/null
GAME='X "Gate" & <Co>\Back'
new() { # new <dir> <runtime> [more options]: the options a later one names win
  local dir=$1 runtime=$2; shift 2
  dotnet new astra-integration "${HIVE[@]}" -o "$W/$dir" -n AstraHostile --game "$GAME" --runtime "$runtime" \
    --steamAppId 1234560 --owner Some-One --id astra.x_y-z.1 "$@" > "$W/$dir.out"
}
new mono mono
new il2cpp il2cpp
new gamedir mono --gameDir '/games/A & B "c"'
new exe mono --exe 'bin\Game "x".exe'
# A SECOND, clean-named Mono instance for the real launcher check below: the hostile GAME name's
# `<`/`>` (deliberately picked to stress TOML/C#/XML escaping) makes `target.exe` a string no
# Windows filename could ever hold, which the real launcher correctly refuses
# (astra-game-launcher's check_component: `<>:"|?*\` are illegal there) — a fact about Windows
# paths, not about this template, so it gets its own instance instead of weakening the hostile one.
dotnet new astra-integration "${HIVE[@]}" -o "$W/launchercheck" -n LauncherCheck --game 'LauncherCheckGame' \
  --runtime mono --steamAppId 1234560 --owner Some-One --id astra.launchercheck > "$W/launchercheck.out"

# Refused: an id or owner that is not one is never rewritten into another; nothing is written.
refused() { # refused <dir> <what the output must name> <options…>
  local dir=$1 says=$2; shift 2
  dotnet new astra-integration "${HIVE[@]}" -o "$W/$dir" -n AstraHostile --game G --steamAppId 1 "$@" > "$W/$dir.out"
  [ -z "$(ls -A "$W/$dir" 2>/dev/null)" ] || fail "$dir: files were written for a refused option: $(ls -A "$W/$dir" | tr '\n' ' ')"
  grep -q 'REFUSED' "$W/$dir.out" && grep -qF -- "$says" "$W/$dir.out" || fail "$dir: dotnet new did not say why: $(cat "$W/$dir.out")"
}
refused bad-id '--id is not an integration id' --id 'astra.x"y z'
refused bad-owner '--owner is not a GitHub' --owner 'Some One!'
refused bad-owner-2 '--owner is not a GitHub' --owner '-dash'
echo "refusals: OK"

# The manifests: TOML that says what the options meant.
python3 - "$W" <<'EOF'
import sys, tomllib
w = sys.argv[1]
for rt in ("mono", "il2cpp"):
    gi = tomllib.load(open(f"{w}/{rt}/astra-gi.toml", "rb"))
    item = tomllib.load(open(f"{w}/{rt}/astra-item.toml", "rb"))
    want = "Astra for X 'Gate' & <Co>Back"
    assert gi["integration"]["name"] == want, gi["integration"]["name"]
    assert item["name"] == want, item["name"]
    assert gi["integration"]["id"] == item["id"] == "astra.x_y-z.1", gi["integration"]["id"]
    assert gi["integration"]["authors"] == ["Some-One"], gi["integration"]["authors"]
    assert gi["integration"]["repo"] == "https://github.com/Some-One/AstraHostile", gi["integration"]["repo"]
    assert gi["target"]["exe"] == "X 'Gate' & <Co>Back.exe", gi["target"]["exe"]
    assert gi["target"]["steam_appid"] == 1234560
exe = tomllib.load(open(f"{w}/exe/astra-gi.toml", "rb"))["target"]["exe"]
assert exe == "bin/Game 'x'.exe", exe   # a \ is a path separator: it becomes /, never dropped
print("manifests: OK")
EOF

# The projects: XML MSBuild reads, with the values the options meant.
for rt in mono il2cpp; do
  name=$(dotnet msbuild "$W/$rt/AstraHostile.csproj" -getProperty:BepInExPluginName)
  [ "$name" = "Astra for X 'Gate' & <Co>Back" ] || fail "$rt: BepInExPluginName is '$name'"
done
dir=$(dotnet msbuild "$W/gamedir/AstraHostile.csproj" -getProperty:GameDir)
[ "$dir" = '/games/A & B "c"' ] || fail "GameDir is '$dir'"
echo "projects: OK"

# The workflow: every action pinned by a full commit SHA.
wf="$W/mono/.github/workflows/release.yml"
unpinned=$(grep -E '^\s*(- )?uses:' "$wf" | grep -vE '@[0-9a-f]{40}( |$)' || true)
[ -z "$unpinned" ] || fail "actions not pinned by commit: $unpinned"
[ ! -e "$W/il2cpp/.github" ] || fail "the IL2CPP flavour got a workflow (no CI can build it)"
echo "workflow: OK"

# The Mono one end to end: fetch, build as CI does, pack the zip.
(
  cd "$W/mono"
  bash tools/fetch.sh >/dev/null
  dotnet build -c Release -p:TreatWarningsAsErrors=true -v q > build.log 2>&1 || { tail -20 build.log >&2; exit 1; }
  bash tools/make-astra-gi.sh >/dev/null
  unzip -l astra-gi.zip | grep -q 'BepInEx/plugins/AstraHostile/AstraHostile.dll'
) || fail "the Mono project did not build and pack"
echo "build and zip: OK"

# The launcher itself, not just a TOML parse: a [[files]] rule matching nothing in the zip is
# refused outright (astra-game-launcher's package.rs, "not in the zip") — the exact mistake that
# shipped [integration.mods] + chain/** pinned to a runner release with no chain/ in its zip.
LAUNCHER_REPO=${ASTRA_GAME_LAUNCHER_REPO:-"$REPO/../astra-game-launcher"}
if [ -d "$LAUNCHER_REPO" ]; then
  ( cd "$LAUNCHER_REPO" && CARGO_TARGET_DIR="$W/launcher-build" cargo build --release -q ) \
    || fail "astra-game-launcher ($LAUNCHER_REPO) did not build"
  LAUNCHER_BIN="$W/launcher-build/release/astra-game-launcher"
  (
    cd "$W/launchercheck"
    bash tools/fetch.sh >/dev/null
    dotnet build -c Release -p:TreatWarningsAsErrors=true -v q > build.log 2>&1 || { tail -20 build.log >&2; exit 1; }
    bash tools/make-astra-gi.sh >/dev/null
  ) || fail "the launcher-check project did not build and pack"
  ZIP="$W/launchercheck/astra-gi.zip"
  SHA=$(sha256sum "$ZIP" | cut -d' ' -f1)
  OUT=$("$LAUNCHER_BIN" inspect "$ZIP" --sha256 "$SHA" --json 2>&1) \
    || fail "astra-game-launcher refused the packed template zip: $OUT"
  echo "launcher inspect: OK"
else
  echo "SKIP: launcher inspect ($LAUNCHER_REPO not found; set ASTRA_GAME_LAUNCHER_REPO)"
fi
echo "check-template: OK"
