#!/bin/bash
# Generate repositories from templates/astra-integration — with options that hold every character
# that breaks a C# or TOML string or an XML file — and check that what comes out builds, parses and is
# what Astra's launcher reads: the Mono one is fetched (tools/fetch.sh: network), built with warnings
# as errors and packed into astra-gi.zip; both runtimes' manifests parse as TOML; the release workflow
# pins every action by commit. The template is installed into a PRIVATE template cache (dotnet new's
# --debug:custom-hive), never into yours.
#
# Usage: tools/check-template.sh
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
HIVE=(--debug:custom-hive "$W/hive")
fail() { echo "check-template: $*" >&2; exit 1; }

dotnet new install "$REPO/templates/astra-integration" "${HIVE[@]}" >/dev/null
GAME='X "Gate" & <Co>\Back'
new() { # new <dir> <runtime> [more options]
  local dir=$1 runtime=$2; shift 2
  dotnet new astra-integration "${HIVE[@]}" -o "$W/$dir" -n AstraHostile --game "$GAME" --runtime "$runtime" \
    --steamAppId 1234560 --owner 'Some One!"' --id 'astra.x"y z' "$@" >/dev/null
}
new mono mono
new il2cpp il2cpp
new gamedir mono --gameDir '/games/A & B "c"'

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
    assert gi["integration"]["id"] == item["id"] == "astra.xyz", gi["integration"]["id"]
    assert gi["integration"]["authors"] == ["SomeOne"], gi["integration"]["authors"]
    assert gi["integration"]["repo"] == "https://github.com/SomeOne/AstraHostile", gi["integration"]["repo"]
    assert gi["target"]["exe"] == "X 'Gate' & <Co>Back.exe", gi["target"]["exe"]
    assert gi["target"]["steam_appid"] == 1234560
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
echo "check-template: OK"
