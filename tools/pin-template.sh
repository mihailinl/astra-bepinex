#!/bin/bash
# Point the integration template at a PUBLISHED foundation release — the one step a release takes for
# the template (CONTRIBUTING.md, "Releasing"). Three places move together:
#   templates/astra-integration/AstraIntegration.csproj   <AstraVersion> (the SDK's range starts there)
#   templates/astra-integration/tools/fetch.sh            the release's SHA-256 digests, per runtime
#   templates/astra-integration/astra-item.toml           the foundation range it requires
# The digests are computed here from the files the release serves (Astra.Unity.Sdk.<v>.nupkg,
# astra-gi.zip, astra-gi-il2cpp.zip), so run it only once v<version> is published on GitHub.
#
# Usage: tools/pin-template.sh <version>          e.g. tools/pin-template.sh 0.7.0
set -euo pipefail
cd "$(dirname "$0")/.."
V=${1:?usage: tools/pin-template.sh <version>}
[[ "$V" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "pin-template.sh: '$V' is not a release version (x.y.z)" >&2; exit 1; }
BASE=https://github.com/mihailinl/astra-bepinex/releases/download/v$V
T=templates/astra-integration

digest() {
  local sum
  sum=$(curl -fsSL "$BASE/$1" | sha256sum | cut -d' ' -f1) || { echo "pin-template.sh: cannot fetch $BASE/$1 (is v$V published?)" >&2; exit 1; }
  echo "$sum"
}
SDK=$(digest "Astra.Unity.Sdk.$V.nupkg")
MONO=$(digest astra-gi.zip)
IL2CPP=$(digest astra-gi-il2cpp.zip)

sed -i -E "s:<AstraVersion>[^<]*</AstraVersion>:<AstraVersion>$V</AstraVersion>:" "$T/AstraIntegration.csproj"
sed -i -E "s/range = \">=[0-9.]+, <1\"/range = \">=$V, <1\"/" "$T/astra-item.toml"
# fetch.sh's table holds one line per runtime: the Mono one first (inside #if (mono)), then IL2CPP's.
awk -v v="$V" -v sdk="$SDK" -v mono="$MONO" -v il2="$IL2CPP" '
  /^  [0-9]+\.[0-9]+\.[0-9]+\) SDK_SHA256=/ {
    n++
    printf "  %s) SDK_SHA256=%s; RUNNER_SHA256=%s ;;\n", v, sdk, (n == 1 ? mono : il2)
    next
  }
  { print }
  END { if (n != 2) { print "pin-template.sh: fetch.sh has " n " table lines, not 2" > "/dev/stderr"; exit 1 } }
' "$T/tools/fetch.sh" > "$T/tools/fetch.sh.new"
mv "$T/tools/fetch.sh.new" "$T/tools/fetch.sh"
chmod +x "$T/tools/fetch.sh"
echo "the template now builds against Astra $V (SDK $SDK, astra-gi.zip $MONO, astra-gi-il2cpp.zip $IL2CPP)"
git diff --stat -- "$T"
