#!/bin/bash
# Lay out the IL2CPP RUNNER's astra-gi.zip — "Astra for any Unity game (IL2CPP, experimental)":
# BepInEx 6 (IL2CPP, with the .NET runtime it hosts plugins in) + this foundation's IL2CPP flavour.
# Same contract as tools/make-runner-gi.sh; written to artifacts/astra-gi-il2cpp.zip.
#
# Usage: tools/make-runner-il2cpp-gi.sh <IL2CPP interop dir>   (or IL2CPP_INTEROP_DIR=<dir>)
#   The interop is a BepInEx/interop folder BepInEx 6 generated for an IL2CPP game that keeps every
#   Unity type the foundation names (Schedule I's: ~/.local/share/astra/games/3164500/BepInEx/interop
#   once Astra's launcher has started it); the testbed's IL2CPP build's is the default. tools/pack.sh
#   builds the foundation first and refuses an interop the game stripped (CONTRIBUTING.md).
#   BEPINEX6_ZIP=<file>  a downloaded BepInEx-Unity.IL2CPP-win-x64 build (default: fetched + verified)
set -euo pipefail
cd "$(dirname "$0")/.."
BEPINEX6_URL="https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip"
BEPINEX6_SHA256=f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a
OUT=$(mktemp -d); trap 'rm -rf "$OUT"' EXIT
# pack.sh's build output goes to a log; on a failure, its errors are shown.
if ! ASTRA_PACK_REQUIRE=il2cpp bash tools/pack.sh "${1:-}" > "$OUT/pack.log"; then
  grep -E ': error ' "$OUT/pack.log" | sed 's/ \[[^]]*\]$//' | sort -u >&2 || tail -20 "$OUT/pack.log" >&2
  echo "${0##*/}: tools/pack.sh failed" >&2; exit 1
fi
if [ -z "${BEPINEX6_ZIP:-}" ]; then
  BEPINEX6_ZIP="$OUT/bepinex6.zip"
  curl -sL -o "$BEPINEX6_ZIP" "$BEPINEX6_URL"
fi
echo "$BEPINEX6_SHA256  $BEPINEX6_ZIP" | sha256sum -c --quiet
mkdir -p "$OUT/bie" && unzip -q "$BEPINEX6_ZIP" -d "$OUT/bie"
Z="$OUT/zip"; mkdir -p "$Z/BepInEx/core" "$Z/BepInEx/plugins" "$Z/dotnet" "$Z/licenses"
cp -r "$OUT/bie/BepInEx/core/." "$Z/BepInEx/core/"
cp -r "$OUT/bie/dotnet/." "$Z/dotnet/"
cp -r artifacts/foundation-il2cpp/BepInEx/plugins/Astra "$Z/BepInEx/plugins/Astra"
cp "$OUT/bie/winhttp.dll" "$Z/winhttp.dll"
cp runner/il2cpp/doorstop_config.ini "$Z/doorstop_config.ini"
cp LICENSE "$Z/licenses/astra-foundation-LICENSE.txt"
# BepInEx's LGPL text (runner/licenses/BepInEx-LICENSE.txt, whose header names the Mono runner's
# BepInEx 5), under a header that names what THIS zip bundles.
{ printf '%s\n' "# BepInEx is licensed under the GNU Lesser General Public License v2.1." \
    "# Source: https://github.com/BepInEx/BepInEx" \
    "# This zip bundles its bleeding-edge build 788 (6.0.0-be.788, IL2CPP) unmodified (except" \
    "# doorstop_config.ini's 'enabled' flag, which this zip ships set to false)." ""
  sed '1,/^$/d' runner/licenses/BepInEx-LICENSE.txt; } > "$Z/licenses/BepInEx-LICENSE.txt"
printf 'BepInEx 6 (IL2CPP) bleeding-edge build 788 (LGPL-2.1): https://github.com/BepInEx/BepInEx\nIts dotnet/ folder is the .NET runtime (MIT): https://github.com/dotnet/runtime\n' > "$Z/licenses/SOURCES.txt"
cp runner/il2cpp/astra-gi.toml "$Z/astra-gi.toml"
mkdir -p artifacts && rm -f artifacts/astra-gi-il2cpp.zip
( cd "$Z" && zip -qr - . ) > artifacts/astra-gi-il2cpp.zip
echo "wrote $(pwd)/artifacts/astra-gi-il2cpp.zip ($(unzip -l artifacts/astra-gi-il2cpp.zip | tail -1 | awk '{print $2}') files, sha256 $(sha256sum artifacts/astra-gi-il2cpp.zip | cut -c1-16)…)"
