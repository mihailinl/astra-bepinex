#!/bin/bash
# Lay out astra-gi.zip, the one file Astra installs from this repository's GitHub release: the
# foundation's own zip as tools/fetch.sh unpacked it (BepInEx, Doorstop's winhttp.dll and
# doorstop_config.ini, the foundation in BepInEx/plugins/Astra/, their licences), plus this
# plugin in BepInEx/plugins/AstraIntegration/, this repository's astra-gi.toml and its LICENSE.
#
# Usage: bash tools/fetch.sh && dotnet build -c Release && bash tools/make-astra-gi.sh
set -euo pipefail
cd "$(dirname "$0")/.."

NAME=AstraIntegration
TFM=$(sed -n 's:.*<TargetFramework>\(.*\)</TargetFramework>.*:\1:p' "$NAME.csproj" | head -1)
BUILD=bin/Release/$TFM
FOUNDATION=foundation/unpacked

for need in "$FOUNDATION/BepInEx/plugins/Astra/Astra.Unity.dll" "$BUILD/$NAME.dll" "$BUILD/astra-item.json" astra-gi.toml LICENSE; do
  [ -f "$need" ] || { echo "make-astra-gi.sh: missing $need: run tools/fetch.sh and 'dotnet build -c Release' first" >&2; exit 1; }
done

OUT=$(mktemp -d)
trap 'rm -rf "$OUT"' EXIT
cp -r "$FOUNDATION/." "$OUT/"
rm -f "$OUT/astra-gi.toml"          # the foundation's own manifest: this repository's replaces it
mkdir -p "$OUT/BepInEx/plugins/$NAME" "$OUT/licenses"
cp "$BUILD/$NAME.dll" "$BUILD/astra-item.json" "$OUT/BepInEx/plugins/$NAME/"
cp astra-gi.toml "$OUT/astra-gi.toml"
cp LICENSE "$OUT/licenses/$NAME-LICENSE.txt"

rm -f astra-gi.zip
( cd "$OUT" && zip -qr - . ) > astra-gi.zip
echo "wrote $(pwd)/astra-gi.zip ($(unzip -l astra-gi.zip | tail -1 | awk '{print $2}') files, sha256 $(sha256sum astra-gi.zip | cut -c1-16)…)"
