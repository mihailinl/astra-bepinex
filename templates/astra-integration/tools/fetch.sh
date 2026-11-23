#!/bin/bash
# Fetch what this integration builds against and bundles, from the Astra foundation's GitHub release,
# each file checked against the SHA-256 pinned below:
#   nuget-local/   the Astra SDK package (Astra.Unity.Sdk is not on nuget.org; nuget.config names this)
#   foundation/    the foundation's "Astra for any Unity game" zip, unpacked: BepInEx, Doorstop's two
#                  files, the foundation and their licences — what tools/make-astra-gi.sh bundles
# Run it once before the first build, and again after a bump; the release workflow runs it too.
#
# A newer foundation: set <AstraVersion> in AstraIntegration.csproj, add its line to the table
# below (the digests are on the release's page, or sha256sum the downloaded files), run this, then
# `dotnet restore --force-evaluate` (it rewrites packages.lock.json: commit it) and rebuild.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<AstraVersion>\(.*\)</AstraVersion>.*:\1:p' AstraIntegration.csproj | head -1)
#if (mono)
RUNNER=astra-gi.zip
#else
RUNNER=astra-gi-il2cpp.zip
#endif
# release   Astra.Unity.Sdk.<release>.nupkg                                    the runner zip
case "$VERSION" in
#if (mono)
  0.9.0) SDK_SHA256=9f91b61d0cb767f328f36b091320a550175e58eb4873eb65d66f04bae1c94eb5; RUNNER_SHA256=b5ab5e2dad288e7490617b48eab0ca19488bbd4db4510044a660564e97a752c8 ;;
#else
  0.9.0) SDK_SHA256=9f91b61d0cb767f328f36b091320a550175e58eb4873eb65d66f04bae1c94eb5; RUNNER_SHA256=fa1b0c10e99b3961f4037e85e320895adab2bc80e82f2eee600d12d014b03432 ;;
#endif
  *) echo "fetch.sh: no digests for Astra '$VERSION' (AstraVersion in AstraIntegration.csproj): add its line to this script's table" >&2; exit 1 ;;
esac
BASE=https://github.com/mihailinl/astra-bepinex/releases/download/v$VERSION

# fetch <url> <sha256> <file>: download unless the file is already there with that digest.
fetch() {
  if [ -f "$3" ] && echo "$2  $3" | sha256sum -c --quiet >/dev/null 2>&1; then return; fi
  curl -fsSL -o "$3.part" "$1"
  if ! echo "$2  $3.part" | sha256sum -c --quiet >/dev/null 2>&1; then
    rm -f "$3.part"
    echo "fetch.sh: $1 is not the file this repository pins (sha256 $2)" >&2
    exit 1
  fi
  mv "$3.part" "$3"
}

mkdir -p nuget-local foundation
fetch "$BASE/Astra.Unity.Sdk.$VERSION.nupkg" "$SDK_SHA256" "nuget-local/Astra.Unity.Sdk.$VERSION.nupkg"
fetch "$BASE/$RUNNER" "$RUNNER_SHA256" "foundation/$RUNNER"
rm -rf foundation/unpacked
mkdir foundation/unpacked
unzip -q "foundation/$RUNNER" -d foundation/unpacked
echo "fetched Astra $VERSION: the SDK into nuget-local/, the foundation's $RUNNER into foundation/"
