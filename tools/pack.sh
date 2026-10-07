#!/bin/bash
# Build both foundation plugins, the URP 17 adapter, the SDK package and the templates package, and
# lay out the foundation's install folders under artifacts/ — what a release ships.
# Usage: tools/pack.sh [Il2CppInteropDir]
set -euo pipefail
cd "$(dirname "$0")/.."
INTEROP=${1:-}
dotnet build src/Astra.BepInEx5 -c Release -v q
dotnet build src/Astra.Urp17 -c Release -v q
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
  else
    src=src/Astra.BepInEx6.IL2CPP/bin/Release/net6.0
  fi
  cp $src/Astra.Unity.dll $src/Astra.Bridge.Core.dll $src/astra-item.json "$out/"
done
ls -R artifacts | head -40
