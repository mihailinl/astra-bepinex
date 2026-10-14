#!/bin/bash
# docs/PROTOCOL.md is the engine's BRIDGE.md (astra-avatar-engine: the modder contract, whose source
# of truth is the engine that implements it), from its first section on, minus the engine's own file
# paths, under this repository's header. This checks that it still is: it rebuilds PROTOCOL.md's body
# from BRIDGE.md and diffs the two.
#
# Usage: tools/check-protocol-sync.sh [--write]
#   ASTRA_ENGINE_DIR  the engine checkout (default: ../astra-companion/astra-avatar-engine, beside this
#                     repository). Without one this prints SKIP and exits 0: contributors need none.
#   --write           rewrite PROTOCOL.md's body from BRIDGE.md and its "synced … on" date to today.
set -euo pipefail
cd "$(dirname "$0")/.."
ENGINE=${ASTRA_ENGINE_DIR:-../astra-companion/astra-avatar-engine}
BRIDGE=$ENGINE/BRIDGE.md
DOC=docs/PROTOCOL.md
# The last line of PROTOCOL.md's own header; everything after it is generated.
MARK='<!-- Everything below is generated from the engine'"'"'s BRIDGE.md by tools/check-protocol-sync.sh --write: edit BRIDGE.md, not this. -->'

if [ ! -f "$BRIDGE" ]; then
  echo "SKIP: no engine checkout at $ENGINE (set ASTRA_ENGINE_DIR to compare docs/PROTOCOL.md with its BRIDGE.md)"
  exit 0
fi

# BRIDGE.md from its first "## " section on (its title and introduction are the engine's), with the
# engine's file paths taken out. One rule per kind of reference; a new kind fails below.
body() {
  awk 'f || /^## /{ f = 1; print }' "$BRIDGE" |
    sed -E 's/ ?\(`notes\/[^`]*`[^)]*\)//g'
}

left=$(body | grep -nE '`(notes|src|tools)/[^`]*`|[A-Za-z_]+\.rs\b' || true)
if [ -n "$left" ]; then
  echo "check-protocol-sync: BRIDGE.md names engine paths no rule here takes out; add one:" >&2
  echo "$left" >&2
  exit 1
fi

header() { awk -v m="$MARK" '{ print } $0 == m { exit }' "$DOC"; }
grep -qxF "$MARK" "$DOC" || { echo "check-protocol-sync: $DOC has lost its header's last line:" >&2; echo "$MARK" >&2; exit 1; }

if [ "${1:-}" = --write ]; then
  today=$(date -u +%F)
  { header | sed -E "s/(synced from the engine's BRIDGE\.md on )[0-9]{4}-[0-9]{2}-[0-9]{2}/\1$today/"; echo; body; } > "$DOC.new"
  mv "$DOC.new" "$DOC"
  echo "wrote $DOC from $BRIDGE (synced $today)"
  exit 0
fi

if diff -u --label "$DOC" --label "$BRIDGE (as PROTOCOL.md)" \
    <(awk -v m="$MARK" 'f { print } $0 == m { f = 1 }' "$DOC" | sed '1{/^$/d}') <(body); then
  echo "OK: $DOC matches $BRIDGE"
else
  echo "check-protocol-sync: $DOC differs from the engine's BRIDGE.md (above); run tools/check-protocol-sync.sh --write" >&2
  exit 1
fi
