#!/bin/bash
# Build the testbed game (unity/AstraTestbed's TestbedBuild.cs and the com.astra.testbed package) with
# ANY Unity editor, in a project made for it outside the repository: in the Built-in pipeline, or with
# that editor's URP. unity/AstraTestbed itself is the Unity 6 / URP 17 project; this is how the same
# game is made for another line (an older Unity cannot open a newer project) or another pipeline.
# Usage: tools/build-testbed.sh <Unity editor> <project dir> builtin|urp:<version> [BuildMono|BuildIl2Cpp|Build]
#   tools/build-testbed.sh ~/Unity/Hub/Editor/2019.4.41f2/Editor/Unity /mnt/data/tb-2019 builtin
#   tools/build-testbed.sh ~/Unity/Hub/Editor/2022.3.62f2/Editor/Unity /mnt/data/tb-2022 urp:14.0.12
# The player lands in <project dir>/Build/mono (or il2cpp); the project is kept, so a rebuild reuses
# its Library. UNITY_EXTRA_LIBS and GLIBC_TUNABLES as in tools/build-bundles.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
EDITOR=${1:?path to a Unity editor binary}
PROJ=${2:?project directory}
PIPE=${3:?builtin or urp:<version>}
METHOD=${4:-BuildMono}
VERSION=$("$EDITOR" -version 2>/dev/null | tail -1 | grep -oE '[0-9]+\.[0-9]+\.[0-9]+[a-z][0-9]+' || true)
[ -n "$VERSION" ] || VERSION=$(basename "$(dirname "$(dirname "$EDITOR")")")
case "$PIPE" in
  builtin) URP="" ;;
  urp:*) URP=${PIPE#urp:} ;;
  *) echo "the pipeline is builtin or urp:<version>, not '$PIPE'" >&2; exit 2 ;;
esac
mkdir -p "$PROJ/Assets/Editor" "$PROJ/Packages" "$PROJ/ProjectSettings"
cp unity/AstraTestbed/Assets/Editor/TestbedBuild.cs "$PROJ/Assets/Editor/"
# The Built-in build is the same file with its URP half compiled out.
if [ -z "$URP" ]; then echo "-define:ASTRA_TESTBED_BUILTIN" > "$PROJ/Assets/csc.rsp"; else rm -f "$PROJ/Assets/csc.rsp"; fi
# The testbed package is COPIED in (an embedded package): an editor rewrites the .meta files of a
# package it is pointed at, and another line's editor would rewrite them its own way.
rm -rf "$PROJ/Packages/com.astra.testbed"
cp -r unity/com.astra.testbed "$PROJ/Packages/"
# Unity's built-in modules, plus the URP asked for; not the Unity 6 toolchain packages, which another
# line cannot resolve.
python3 - "unity/AstraTestbed/Packages/manifest.json" "$PROJ/Packages/manifest.json" "$URP" <<'EOF'
import json, sys
src, dst, urp = sys.argv[1:4]
deps = {k: v for k, v in json.load(open(src))["dependencies"].items() if k.startswith("com.unity.modules.")}
if urp:
    deps["com.unity.render-pipelines.universal"] = urp
json.dump({"dependencies": deps}, open(dst, "w"), indent=2)
EOF
[ -f "$PROJ/ProjectSettings/ProjectVersion.txt" ] || echo "m_EditorVersion: $VERSION" > "$PROJ/ProjectSettings/ProjectVersion.txt"
echo "building the testbed (${URP:+URP }${URP:-Built-in}, $METHOD) with Unity $VERSION in $PROJ"
LD_LIBRARY_PATH=${UNITY_EXTRA_LIBS:-}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH} GLIBC_TUNABLES=${GLIBC_TUNABLES:-glibc.rtld.execstack=2} \
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=${DOTNET_SYSTEM_GLOBALIZATION_INVARIANT:-1} \
  "$EDITOR" -batchmode -quit -projectPath "$PROJ" -executeMethod "TestbedBuild.$METHOD" -logFile "$PROJ/build.log" >/dev/null || {
  grep -E 'error|Exception' "$PROJ/build.log" | head -20; exit 1; }
grep -F '[testbed]' "$PROJ/build.log" || true
