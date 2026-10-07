#!/bin/bash
# Build both foundation plugins, the URP 17 adapter, the SDK package and the templates package, and
# lay out the foundation's install folders under artifacts/ — what a release ships.
# Usage: tools/pack.sh [Il2CppInteropDir]
#   HDRP_REF_DIR=<an HDRP game's *_Data/Managed, or an HDRP project's Library/ScriptAssemblies> builds the
#   HDRP adapter too (without it the foundation ships without one and draws HDRP games without a depth test).
set -euo pipefail
cd "$(dirname "$0")/.."
INTEROP=${1:-}
dotnet build src/Astra.BepInEx5 -c Release -v q
dotnet build src/Astra.Urp17 -c Release -v q
HDRP=""
if [ -n "${HDRP_REF_DIR:-}" ] || [ -d unity/AstraTestbedHdrp/Library/ScriptAssemblies ]; then
  # Two statements, not `build && HDRP=…`: set -e ignores a failure on the left of && and the
  # release would ship without the adapter in silence.
  dotnet build src/Astra.Hdrp -c Release -v q ${HDRP_REF_DIR:+-p:HdrpRefDir="$HDRP_REF_DIR"}
  HDRP=src/Astra.Hdrp/bin/Release/netstandard2.1/Astra.Hdrp.dll
else
  echo "warning: no HDRP references (HDRP_REF_DIR): the foundation ships without its HDRP adapter" >&2
fi
if [ -n "$INTEROP" ]; then
  dotnet build src/Astra.BepInEx6.IL2CPP -c Release -v q -p:Il2CppInteropDir="$INTEROP"
else
  dotnet build src/Astra.BepInEx6.IL2CPP -c Release -v q
fi
dotnet pack src/Astra.Unity.Sdk -c Release -v q
dotnet pack templates/Astra.Unity.Templates.csproj -c Release -v q
# The foundation as it is installed in a game: BepInEx/plugins/Astra/…
for flavour in mono il2cpp; do
  out=artifacts/foundation-$flavour/BepInEx/plugins/Astra
  rm -rf artifacts/foundation-$flavour && mkdir -p "$out"
  if [ $flavour = mono ]; then
    src=src/Astra.BepInEx5/bin/Release/netstandard2.0
    cp src/Astra.Urp17/bin/Release/netstandard2.1/Astra.Urp17.dll "$out/"
    [ -n "$HDRP" ] && cp "$HDRP" "$out/"
  else
    src=src/Astra.BepInEx6.IL2CPP/bin/Release/net6.0
  fi
  cp $src/Astra.Unity.dll $src/Astra.Bridge.Core.dll $src/astra-item.json "$out/"
done
ls -R artifacts | head -40
