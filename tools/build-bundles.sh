#!/bin/bash
# Build the composite shader's bundles with ONE Unity editor, from the one shader source
# (unity/AstraShaders/Assets). A bundle loads in its own Unity line and newer ones, so each line a game
# may run gets its own: astra-<major>-<platform>.bundle, beside the others in unity/AstraShaders/Build.
# Usage: tools/build-bundles.sh /path/to/Unity  (e.g. ~/Unity/Hub/Editor/2022.3.62f2/Editor/Unity)
#   UNITY_EXTRA_LIBS=<dir>  prepended to LD_LIBRARY_PATH — an older editor on a newer distro may need
#                           a library the distro no longer ships (2021.3 wants libxml2.so.2).
# An older editor's licensing client loads a library with an executable stack, which glibc 2.41+
# refuses unless allowed: the editor runs with glibc.rtld.execstack=2 (set GLIBC_TUNABLES to override).
set -euo pipefail
cd "$(dirname "$0")/.."
EDITOR=${1:?path to a Unity editor binary}
VERSION=$("$EDITOR" -version 2>/dev/null | tail -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+[a-z][0-9]+' || true)
[ -n "$VERSION" ] || VERSION=$(basename "$(dirname "$(dirname "$EDITOR")")")
SRC=unity/AstraShaders
PROJ=$(mktemp -d "${TMPDIR:-/tmp}/astra-shaders-$VERSION.XXXX")
mkdir -p "$PROJ/Assets" "$PROJ/Packages" "$PROJ/ProjectSettings"
cp -r "$SRC/Assets/Astra" "$SRC/Assets/Editor" "$PROJ/Assets/"
# Only Unity's built-in modules: an editor adds its own toolchain packages to the manifest, and another
# Unity line cannot compile them.
python3 -c "import json,sys; m=json.load(open(sys.argv[1])); m['dependencies']={k:v for k,v in m['dependencies'].items() if k.startswith('com.unity.modules.')}; json.dump(m,open(sys.argv[2],'w'),indent=2)" \
  "$SRC/Packages/manifest.json" "$PROJ/Packages/manifest.json"
echo "m_EditorVersion: $VERSION" > "$PROJ/ProjectSettings/ProjectVersion.txt"
echo "building the bundles with Unity $VERSION in $PROJ"
# A 2021/2022 editor on Linux can hang forever after compiling the scripts: bee_backend's
# --stdin-canary keeps it from closing. Wrap that editor's Data/bee_backend once (Unity forum, "Linux
# editor stuck on loading because of bee_backend w/ workaround"): rename it bee_backend_real and put
# in its place a script that drops --stdin-canary and execs "${0}_real" with the other arguments.
LD_LIBRARY_PATH=${UNITY_EXTRA_LIBS:-}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH} GLIBC_TUNABLES=${GLIBC_TUNABLES:-glibc.rtld.execstack=2} "$EDITOR" -batchmode -quit -nographics -projectPath "$PROJ" -executeMethod BuildBundles.Build -logFile "$PROJ/build.log" || {
  grep -E 'error|Exception' "$PROJ/build.log" | head -20; exit 1; }
cp "$PROJ"/Build/astra-*.bundle "$SRC/Build/"
ls -la "$SRC"/Build/astra-*.bundle
rm -rf "$PROJ"
