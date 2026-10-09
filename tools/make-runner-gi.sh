#!/bin/bash
# Lay out the RUNNER's astra-gi.zip — "Astra for any Unity game": BepInEx 5 + this foundation, no
# per-game integration (docs/GAME-INTEGRATION-MANIFEST.md §3, the engine-target form). Built LOCALLY:
# the foundation's HDRP adapter compiles against HDRP's own assemblies, which never go into CI.
#
# Usage: HDRP_REF_DIR=<an HDRP game's *_Data/Managed> URP14_REF_DIR=<a Unity 2022.3 URP game's *_Data/Managed> tools/make-runner-gi.sh
#   BEPINEX_DIST=<dir>  an unpacked BepInEx_win_x64_5.4.23.5.zip (default: fetched and verified here)
set -euo pipefail
cd "$(dirname "$0")/.."
BEPINEX_URL=https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip
BEPINEX_SHA256=82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4
[ -n "${HDRP_REF_DIR:-}" ] || { echo "make-runner-gi.sh: set HDRP_REF_DIR (the runner must carry its HDRP adapter)" >&2; exit 1; }
[ -n "${URP14_REF_DIR:-}" ] || { echo "make-runner-gi.sh: set URP14_REF_DIR (the runner must carry its URP 2022 adapter)" >&2; exit 1; }
bash tools/pack.sh >/dev/null
OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT
if [ -z "${BEPINEX_DIST:-}" ]; then
  BEPINEX_DIST="$OUT/bepinex-dist"; mkdir -p "$BEPINEX_DIST"
  curl -sL -o "$OUT/bie.zip" "$BEPINEX_URL"
  echo "$BEPINEX_SHA256  $OUT/bie.zip" | sha256sum -c --quiet
  unzip -q "$OUT/bie.zip" -d "$BEPINEX_DIST"
fi
Z="$OUT/zip"; mkdir -p "$Z/BepInEx/core" "$Z/BepInEx/plugins" "$Z/licenses"
cp -r "$BEPINEX_DIST/BepInEx/core/." "$Z/BepInEx/core/"
cp -r artifacts/foundation-mono/BepInEx/plugins/Astra "$Z/BepInEx/plugins/Astra"
cp "$BEPINEX_DIST/winhttp.dll" "$Z/winhttp.dll"
cp runner/doorstop_config.ini "$Z/doorstop_config.ini"
cp LICENSE "$Z/licenses/astra-foundation-LICENSE.txt"
cp runner/licenses/BepInEx-LICENSE.txt "$Z/licenses/BepInEx-LICENSE.txt"
cp runner/astra-gi.toml "$Z/astra-gi.toml"
mkdir -p artifacts && rm -f artifacts/astra-gi.zip
( cd "$Z" && zip -qr - . ) > artifacts/astra-gi.zip
echo "wrote $(pwd)/artifacts/astra-gi.zip ($(unzip -l artifacts/astra-gi.zip | tail -1 | awk '{print $2}') files, sha256 $(sha256sum artifacts/astra-gi.zip | cut -c1-16)…)"
