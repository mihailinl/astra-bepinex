#!/bin/bash
# Does a runner manifest name the foundation version this checkout builds (Directory.Build.props)?
# The release scripts ask before laying out a zip: a runner zip whose astra-gi.toml says another
# version than the foundation inside it would be recorded by Astra under the wrong version.
# Usage: tools/check-runner-version.sh runner/astra-gi.toml
set -euo pipefail
cd "$(dirname "$0")/.."
want=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
have=$(sed -n 's/^version = "\(.*\)".*/\1/p' "$1" | head -1)
if [ "$want" != "$have" ]; then
  echo "${0##*/}: $1 says version \"$have\", the foundation is $want (Directory.Build.props): change the manifest" >&2
  exit 1
fi
