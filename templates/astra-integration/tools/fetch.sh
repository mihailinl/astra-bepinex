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
  0.7.0) SDK_SHA256=6636a2c5a0cceb789e21333f5e6de9d07c617f70108fdf48eff6736e32cf0428; RUNNER_SHA256=47a03240bfb9856b98a24ce8d1111646f76c264ca3eda64648343c141a2afc9a ;;
#else
  0.7.0) SDK_SHA256=6636a2c5a0cceb789e21333f5e6de9d07c617f70108fdf48eff6736e32cf0428; RUNNER_SHA256=5316ca8b1e9ec8364249db8c22fc3c0cb69c22627ab5afc19e71a70d0d60b904 ;;
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
