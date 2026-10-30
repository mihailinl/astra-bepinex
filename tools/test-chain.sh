#!/bin/bash
# Headless test for the chain loader (play together with the player's own mods,
# docs/GAME-INTEGRATION-MANIFEST.md §7). Builds Astra.Chain + Astra.Chain.Hooks + the foundation +
# the fixture plugins, then runs the Linux testbed player (-batchmode -nographics: no window, ever)
# under Doorstop against real, downloaded BepInEx packs copied into scratch, and asserts what each
# run's LogOutput.log and last-run.txt say. Also checks that a manifest carrying [integration.mods]
# is still accepted end to end by astra-game-launcher 9aa7874 (built fresh into its own target dir;
# that repository is never written to).
#
# Usage: tools/test-chain.sh
#   ASTRA_CHAIN_TEST_SCRATCH   where profiles are assembled (default: a dir under TMPDIR/$PWD/tmp)
#   ASTRA_CHAIN_TEST_PACKS     a folder of <pack>/BepInEx* dirs (default: the design's downloaded
#                              packs, gi-bugs/packs/x — never the owner's real game folders or Gale
#                              data; those are read-only reference material, never touched)
#   ASTRA_CHAIN_TEST_LAUNCHER  astra-game-launcher's checkout (default: ../astra-game-launcher);
#                              built into its OWN target dir under scratch, never edited
#   ASTRA_CHAIN_TEST_TESTBED   the Linux Mono testbed player's folder
#                              (default: unity/AstraTestbed/Build/mono)
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)

TMPDIR=${TMPDIR:-/tmp}
SCRATCH=${ASTRA_CHAIN_TEST_SCRATCH:-"$TMPDIR/astra-chain-test"}
PACKS_ROOT=${ASTRA_CHAIN_TEST_PACKS:-/mnt/data/claude-scratch/astra-avatar-engine/gi-bugs/packs/x}
LAUNCHER_REPO=${ASTRA_CHAIN_TEST_LAUNCHER:-"$REPO/../astra-game-launcher"}
TESTBED=${ASTRA_CHAIN_TEST_TESTBED:-"$REPO/unity/AstraTestbed/Build/mono"}

rm -rf "$SCRATCH"
mkdir -p "$SCRATCH"
export TMPDIR="$SCRATCH/tmp"
mkdir -p "$TMPDIR"

FAIL=0
fail() { echo "FAIL: $*" >&2; FAIL=1; }
pass() { echo "ok: $*"; }

[ -x "$TESTBED/AstraTestbed.x86_64" ] || { echo "test-chain: no testbed player at $TESTBED (unity/AstraTestbed needs building — CONTRIBUTING.md)" >&2; exit 1; }
[ -d "$PACKS_ROOT" ] || { echo "test-chain: no packs at $PACKS_ROOT (ASTRA_CHAIN_TEST_PACKS)" >&2; exit 1; }

echo "== building chain, foundation, fixtures =="
dotnet build src/Astra.Chain -c Release -v q
dotnet build src/Astra.Chain.Hooks -c Release -v q
dotnet build src/Astra.BepInEx5 -c Release -v q
dotnet build tests/ChainFixtures -c Release -v q
dotnet build tests/ChainConfigProbe -c Release -v q

CHAIN_DLL="$REPO/src/Astra.Chain/bin/Release/Astra.Chain.dll"
HOOKS_DLL="$REPO/src/Astra.Chain.Hooks/bin/Release/netstandard2.0/Astra.Chain.Hooks.dll"
FOUNDATION_DIR="$REPO/src/Astra.BepInEx5/bin/Release/netstandard2.0"
FIXTURES_DLL="$REPO/tests/ChainFixtures/bin/Release/netstandard2.0/ChainFixtures.dll"
PROBE_DLL="$REPO/tests/ChainConfigProbe/bin/Release/netstandard2.0/ChainConfigProbe.dll"
ASTRA_CORE_SRC="$PACKS_ROOT/BepInEx-BepInExPack-5.4.2305/BepInExPack/BepInEx/core"   # stock 5.4.23.5, = the runner's own proxy
[ -d "$ASTRA_CORE_SRC" ] || { echo "test-chain: no stock core at $ASTRA_CORE_SRC" >&2; exit 1; }

# ---- helpers -----------------------------------------------------------------------------------

# make_astra_profile <dir>: Astra's OWN profile — stock core + the real foundation + the
# config-probe fixture (astra.chain.config-probe), exactly as a with-mods Play lays one out.
make_astra_profile() {
  local dir=$1
  rm -rf "$dir"
  mkdir -p "$dir/BepInEx/core" "$dir/BepInEx/plugins/Astra" "$dir/chain"
  cp -r "$ASTRA_CORE_SRC/." "$dir/BepInEx/core/"
  cp "$FOUNDATION_DIR/Astra.Unity.dll" "$FOUNDATION_DIR/Astra.Bridge.Core.dll" "$FOUNDATION_DIR/astra-item.json" "$dir/BepInEx/plugins/Astra/"
  cp "$PROBE_DLL" "$dir/BepInEx/plugins/Astra/"
  cp "$CHAIN_DLL" "$HOOKS_DLL" "$dir/chain/"
}

# force_disk_log <BepInEx/config dir>: every pack's own BepInEx.cfg, however it ships, logs to
# LogOutput.log in THIS scratch copy — never the player's real one, and never a write this script
# doesn't make itself. Drops any existing [Logging.Disk] section first so one `Enabled = true`
# always wins.
force_disk_log() {
  local cfg="$1/BepInEx.cfg"
  if [ -f "$cfg" ]; then
    awk '/^\[Logging\.Disk\]/{skip=1;next} /^\[/{skip=0} !skip{print}' "$cfg" > "$cfg.new"
    mv "$cfg.new" "$cfg"
  fi
  printf '\n[Logging.Disk]\nEnabled = true\n' >> "$cfg"
}

# make_their_profile <dir> <pack's BepInExPack* dir> <fixture value>: a scratch copy of a real,
# downloaded pack (never the owner's own game folders or Gale data) plus the fixture plugins —
# "their profile" exactly as a mod manager would leave it, never written to by anything but this
# script.
make_their_profile() {
  local dir=$1 pack=$2 value=$3
  rm -rf "$dir"
  mkdir -p "$dir"
  cp -r "$pack/BepInEx" "$dir/BepInEx"
  mkdir -p "$dir/BepInEx/plugins" "$dir/BepInEx/config"
  cp "$FIXTURES_DLL" "$dir/BepInEx/plugins/"
  printf '[Test]\nValue = %s\n' "$value" > "$dir/BepInEx/config/astra.test.fixturemod.cfg"
  force_disk_log "$dir/BepInEx/config"
}

write_chain_ini() { # <astra dir> <target> <source> <profile>
  cat > "$1/chain/chain.ini" <<EOF
[Chain]
target = $2
source = $3
profile = $4
EOF
}

# digest_tree <dir>: relpath + size + md5, one per line, sorted — everything except the files
# BepInEx's OWN runtime always touches on any launch, chained or not (LogOutput.log, BepInEx/cache/,
# and BepInEx.cfg itself, which BepInEx always rewrites with full descriptive comments the first
# time it runs against a cfg that does not already have them — none of this is Astra's doing; a
# separate, content-level check below asks whether BepInEx.cfg picked up anything of Astra's).
digest_tree() {
  local dir=$1
  ( cd "$dir" && find . -type f \
      ! -path './BepInEx/LogOutput.log' ! -path './BepInEx/cache/*' \
      ! -path './BepInEx/config/BepInEx.cfg' ! -path './BepInEx/config/astra.test.fixturemod.cfg' \
      -exec sh -c 'printf "%s %s %s\n" "$1" "$(stat -c%s "$1")" "$(md5sum < "$1" | cut -d" " -f1)"' _ {} \; \
    | sort )
}

run_testbed() { # <astra dir> <label>
  local astra_dir=$1 label=$2
  ( cd "$TESTBED" && timeout 90 ./run_bepinex.sh ./AstraTestbed.x86_64 \
      --doorstop-enabled true --doorstop-target-assembly "$astra_dir/chain/Astra.Chain.dll" \
      -batchmode -nographics -astraQuit 6 -logFile "$SCRATCH/unity-$label.log" \
      > "$SCRATCH/stdout-$label.log" 2>&1 )
}

has() { grep -qF -- "$2" "$1" 2>/dev/null; } # has <file> <literal text>
hasnt() { ! grep -qF -- "$2" "$1" 2>/dev/null; }

# ---- the pack matrix ----------------------------------------------------------------------------
# name | pack dir (relative to PACKS_ROOT) | full downstream assertions? | semver loads?
# "full" packs are the ones whose chainloader trigger actually fires against the generic testbed
# (its patched entrypoint is a type/method every Unity game touches: UnityEngine.Application's own
# .cctor, or — the Valheim fork — UnityEngine.CoreModule's GameObject..cctor). RoR2's 5.4.21 core
# and Subnautica's 5.4.19 core are NOT "full": the chain itself still succeeds (their preloader is
# invoked, Astra's hooks install, last-run.txt says ok) — proven below — but their own chainloader
# never actually starts against this testbed, for two different, pack-specific reasons that have
# nothing to do with the chain: Subnautica's preloader patches `SystemsSpawner.Awake`, a type that
# exists only in the real game, never in this testbed; RoR2's 5.4.21 core's patcher never finds its
# target method against a Unity version (6000.3) that much newer than the one it shipped for (its
# own log stops right after "Preloader finished" — the "Patching [UnityEngine.CoreModule] with
# [BepInEx.Chainloader]" line every other pack logs is simply absent). Documented, not worked
# around: proving a 2021-era core's chainloader is not this design's job.
PACKS_FULL="
generic|BepInEx-BepInExPack-5.4.2305/BepInExPack|no
valheim|denikson-BepInExPack_Valheim-5.4.2360/BepInExPack_Valheim|yes
peak|BepInEx-BepInExPack_PEAK-5.4.75301/BepInExPack_PEAK|no
"
PACKS_OK_ONLY="
ror2|bbepis-BepInExPack-5.4.2122/BepInExPack
subnautica|Subnautica_Modding-BepInExPack_Subnautica-5.4.1901/BepInExPack_Subnautica
"

echo "== full packs: chain + hooks + dedupe + config redirect + per-pack semver =="
while IFS='|' read -r name reldir semver; do
  [ -n "$name" ] || continue
  PACK="$PACKS_ROOT/$reldir"
  [ -d "$PACK/BepInEx" ] || { fail "$name: no pack at $PACK"; continue; }
  ASTRA_DIR="$SCRATCH/astra-$name"
  THEIR_DIR="$SCRATCH/their-$name"
  make_astra_profile "$ASTRA_DIR"
  make_their_profile "$THEIR_DIR" "$PACK" "from-$name-profile"
  TARGET="$THEIR_DIR/BepInEx/core/BepInEx.Preloader.dll"
  [ -f "$TARGET" ] || { fail "$name: no BepInEx.Preloader.dll in $PACK"; continue; }
  write_chain_ini "$ASTRA_DIR" "$TARGET" "manual" "$name-profile"
  BEFORE=$(digest_tree "$THEIR_DIR")
  run_testbed "$ASTRA_DIR" "$name" || true
  AFTER=$(digest_tree "$THEIR_DIR")

  LOG="$THEIR_DIR/BepInEx/LogOutput.log"
  LAST="$ASTRA_DIR/chain/last-run.txt"
  [ -f "$LOG" ] || { fail "$name: no LogOutput.log at all"; continue; }

  has "$LAST" "ok " && pass "$name: last-run.txt says ok" || fail "$name: last-run.txt: $(cat "$LAST" 2>&1)"
  has "$LOG" "Loading [FixtureMod" && has "$LOG" "FixtureMod value=from-$name-profile" \
    && pass "$name: FixtureMod ran with THEIR profile's config value" \
    || fail "$name: FixtureMod did not read its profile's own value"
  has "$LOG" "Skipping astra.unity-foundation" && pass "$name: dedupe-by-GUID skipped the stale Astra copy" \
    || fail "$name: no 'Skipping astra.unity-foundation' line"
  hasnt "$LOG" "StaleAstra Awake ran" && pass "$name: the stale Astra copy's Awake never ran" \
    || fail "$name: StaleAstra's Awake ran — dedupe did not actually remove it"
  has "$LOG" "with your mods: manual" && has "$LOG" "$name-profile" \
    && pass "$name: the foundation's own 'with your mods' line printed, with chain.ini's labels" \
    || fail "$name: no (or wrong) 'Astra ... with your mods' line"
  has "$LOG" "DependsOnAstra loaded" \
    && pass "$name: a per-game integration fixture with [BepInDependency] on the foundation loaded" \
    || fail "$name: DependsOnAstra (depends on astra.unity-foundation, merged in by the chain) did not load"
  if [ "$semver" = yes ]; then
    has "$LOG" "SemverMod loaded" && pass "$name: SemverMod loaded (this pack's fork accepts semver)" \
      || fail "$name: SemverMod did not load under the Valheim fork"
  else
    hasnt "$LOG" "SemverMod loaded" && pass "$name: SemverMod correctly rejected by stock BepInEx" \
      || fail "$name: SemverMod loaded under a stock core that should reject it"
  fi

  PROBE_CFG="$ASTRA_DIR/BepInEx/config/astra.chain.config-probe.cfg"
  [ -f "$PROBE_CFG" ] && has "$PROBE_CFG" "written-by-ours" \
    && pass "$name: Astra's own plugin wrote its config into ASTRA's profile" \
    || fail "$name: astra.chain.config-probe.cfg missing/wrong in Astra's own profile"
  [ ! -e "$THEIR_DIR/BepInEx/config/astra.chain.config-probe.cfg" ] \
    && pass "$name: nothing of Astra's config landed in THEIR profile" \
    || fail "$name: astra.chain.config-probe.cfg leaked into their profile"

  if [ "$BEFORE" = "$AFTER" ]; then
    pass "$name: their profile's tree is byte-for-byte unchanged (minus LogOutput.log/cache/BepInEx.cfg)"
  else
    fail "$name: their profile's tree changed: $(diff <(echo "$BEFORE") <(echo "$AFTER") | head -10)"
  fi
  if grep -qi astra "$THEIR_DIR/BepInEx/config/BepInEx.cfg" 2>/dev/null; then
    fail "$name: the word 'astra' (any case) turned up in their own BepInEx.cfg"
  else
    pass "$name: their rewritten BepInEx.cfg carries nothing of Astra's"
  fi
done < <(echo "$PACKS_FULL")

echo "== ok-only packs: the chain still succeeds; their own chainloader has no trigger in this testbed =="
while IFS='|' read -r name reldir; do
  [ -n "$name" ] || continue
  PACK="$PACKS_ROOT/$reldir"
  [ -d "$PACK/BepInEx" ] || { fail "$name: no pack at $PACK"; continue; }
  ASTRA_DIR="$SCRATCH/astra-$name"
  THEIR_DIR="$SCRATCH/their-$name"
  make_astra_profile "$ASTRA_DIR"
  make_their_profile "$THEIR_DIR" "$PACK" "from-$name-profile"
  TARGET="$THEIR_DIR/BepInEx/core/BepInEx.Preloader.dll"
  [ -f "$TARGET" ] || { fail "$name: no BepInEx.Preloader.dll in $PACK"; continue; }
  write_chain_ini "$ASTRA_DIR" "$TARGET" "manual" "$name-profile"
  run_testbed "$ASTRA_DIR" "$name" || true
  LAST="$ASTRA_DIR/chain/last-run.txt"
  has "$LAST" "ok " && pass "$name: chain still hands off and installs hooks ok ($(cat "$LAST"))" \
    || fail "$name: last-run.txt: $(cat "$LAST" 2>&1)"
done < <(echo "$PACKS_OK_ONLY")

echo "== fallback: a chain.ini naming a missing target =="
ASTRA_DIR="$SCRATCH/astra-fallback"
make_astra_profile "$ASTRA_DIR"
write_chain_ini "$ASTRA_DIR" "$SCRATCH/does-not-exist/BepInEx/core/BepInEx.Preloader.dll" "gale" "Missing"
run_testbed "$ASTRA_DIR" "fallback" || true
LAST="$ASTRA_DIR/chain/last-run.txt"
LOG="$ASTRA_DIR/BepInEx/LogOutput.log"
has "$LAST" "fallback " && pass "fallback: last-run.txt says fallback ($(cat "$LAST"))" \
  || fail "fallback: last-run.txt: $(cat "$LAST" 2>&1)"
[ -f "$LOG" ] && has "$LOG" "Loading [Astra " && pass "fallback: Astra's own foundation still loaded" \
  || fail "fallback: Astra's own session did not come up"
hasnt "$LOG" "with your mods" && pass "fallback: no 'with your mods' line (this IS Astra's own root)" \
  || fail "fallback: 'with your mods' printed during a fallback session"

GENERIC_PACK="$PACKS_ROOT/BepInEx-BepInExPack-5.4.2305/BepInExPack"

echo "== malformed chain.ini: no [Chain] section at all → real fallback =="
ASTRA_DIR="$SCRATCH/astra-malformed"
make_astra_profile "$ASTRA_DIR"
printf '[NotChain]\nValue = whatever\n' > "$ASTRA_DIR/chain/chain.ini"
run_testbed "$ASTRA_DIR" "malformed" || true
LAST="$ASTRA_DIR/chain/last-run.txt"
LOG="$ASTRA_DIR/BepInEx/LogOutput.log"
has "$LAST" "fallback " && pass "malformed chain.ini: falls back ($(cat "$LAST"))" \
  || fail "malformed chain.ini: last-run.txt: $(cat "$LAST" 2>&1)"
[ -f "$LOG" ] && has "$LOG" "Loading [Astra " && pass "malformed chain.ini: Astra's own session still came up" \
  || fail "malformed chain.ini: Astra's own session did not come up"

echo "== broken core: BepInEx.dll missing beside a real-looking preloader → real fallback =="
BROKEN_CORE="$SCRATCH/broken-core/BepInEx/core"
rm -rf "$SCRATCH/broken-core"; mkdir -p "$BROKEN_CORE"
cp -r "$GENERIC_PACK/BepInEx/core/." "$BROKEN_CORE/"
rm -f "$BROKEN_CORE/BepInEx.dll"
ASTRA_DIR="$SCRATCH/astra-brokencore"
make_astra_profile "$ASTRA_DIR"
write_chain_ini "$ASTRA_DIR" "$BROKEN_CORE/BepInEx.Preloader.dll" "manual" "broken-core"
run_testbed "$ASTRA_DIR" "brokencore" || true
LAST="$ASTRA_DIR/chain/last-run.txt"
LOG="$ASTRA_DIR/BepInEx/LogOutput.log"
has "$LAST" "fallback " && has "$LAST" "BepInEx.dll" \
  && pass "broken core: ValidateTarget caught the missing BepInEx.dll and fell back ($(cat "$LAST"))" \
  || fail "broken core: last-run.txt: $(cat "$LAST" 2>&1)"
[ -f "$LOG" ] && has "$LOG" "Loading [Astra " && pass "broken core: Astra's own session still came up" \
  || fail "broken core: Astra's own session did not come up"

echo "== postfix failure: ours/plugins missing → their mods still load, hooks-failed (not ok) =="
ASTRA_DIR="$SCRATCH/astra-nopluginsdir"
THEIR_DIR="$SCRATCH/their-nopluginsdir"
make_astra_profile "$ASTRA_DIR"
rm -rf "$ASTRA_DIR/BepInEx/plugins"
make_their_profile "$THEIR_DIR" "$GENERIC_PACK" "from-nopluginsdir-profile"
write_chain_ini "$ASTRA_DIR" "$THEIR_DIR/BepInEx/core/BepInEx.Preloader.dll" "manual" "nopluginsdir"
run_testbed "$ASTRA_DIR" "nopluginsdir" || true
LAST="$ASTRA_DIR/chain/last-run.txt"
LOG="$THEIR_DIR/BepInEx/LogOutput.log"
has "$LAST" "hooks-failed " && pass "postfix failure: last-run.txt says hooks-failed, not ok ($(cat "$LAST"))" \
  || fail "postfix failure: last-run.txt: $(cat "$LAST" 2>&1)"
[ -f "$LOG" ] && has "$LOG" "Loading [FixtureMod" && has "$LOG" "FixtureMod value=from-nopluginsdir-profile" \
  && pass "postfix failure: their own mods still loaded, untouched" \
  || fail "postfix failure: their mods did not load"

echo "== a profile name with # (comment-stripping regression, review S3) =="
ASTRA_DIR="$SCRATCH/astra-hashname"
THEIR_DIR="$SCRATCH/their-hashname"
make_astra_profile "$ASTRA_DIR"
make_their_profile "$THEIR_DIR" "$GENERIC_PACK" "from-hashname-profile"
write_chain_ini "$ASTRA_DIR" "$THEIR_DIR/BepInEx/core/BepInEx.Preloader.dll" "gale" "My #1"
run_testbed "$ASTRA_DIR" "hashname" || true
LAST="$ASTRA_DIR/chain/last-run.txt"
LOG="$THEIR_DIR/BepInEx/LogOutput.log"
has "$LAST" "ok " && pass "profile name with #: the chain still succeeds ($(cat "$LAST"))" \
  || fail "profile name with #: last-run.txt: $(cat "$LAST" 2>&1)"
[ -f "$LOG" ] && has "$LOG" "My #1" \
  && pass "profile name with #: the whole name (including the #) reached the foundation's log line" \
  || fail "profile name with #: 'My #1' did not appear whole in the log (a stale comment-stripping bug would truncate it to 'My ')"

echo "== launcher compatibility: astra-game-launcher 9aa7874 still accepts [integration.mods] =="
if [ -d "$LAUNCHER_REPO" ]; then
  LAUNCHER_BUILD="$SCRATCH/launcher-build"
  ( cd "$LAUNCHER_REPO" && CARGO_TARGET_DIR="$LAUNCHER_BUILD" cargo build --release -q ) \
    || fail "astra-game-launcher ($LAUNCHER_REPO) did not build"
  LAUNCHER_BIN="$LAUNCHER_BUILD/release/astra-game-launcher"
  LCHECK="$SCRATCH/launcher-check"
  rm -rf "$LCHECK"; mkdir -p "$LCHECK/game/game_Data/Managed" "$LCHECK/profile"
  touch "$LCHECK/game/UnityPlayer.dll" "$LCHECK/game/game_Data/Managed/Assembly-CSharp.dll" "$LCHECK/game/game.exe"
  mkdir -p "$LCHECK/zip/BepInEx/core" "$LCHECK/zip/BepInEx/plugins" "$LCHECK/zip/chain"
  echo x > "$LCHECK/zip/BepInEx/core/BepInEx.Preloader.dll"
  echo x > "$LCHECK/zip/BepInEx/plugins/dummy.dll"
  echo x > "$LCHECK/zip/chain/Astra.Chain.dll"
  echo x > "$LCHECK/zip/winhttp.dll"
  printf '[UnityPlayer]\n' > "$LCHECK/zip/doorstop_config.ini"
  WINHTTP_SHA=$(sha256sum "$LCHECK/zip/winhttp.dll" | cut -d' ' -f1)
  sed "s/8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c/$WINHTTP_SHA/" \
    "$REPO/runner/astra-gi.toml" > "$LCHECK/zip/astra-gi.toml"
  ( cd "$LCHECK/zip" && zip -qr - . ) > "$LCHECK/runner.zip"
  SHA=$(sha256sum "$LCHECK/runner.zip" | cut -d' ' -f1)
  INSPECT_JSON=$("$LAUNCHER_BIN" inspect "$LCHECK/runner.zip" --sha256 "$SHA" --json 2>&1) \
    || fail "inspect refused the manifest: $INSPECT_JSON"
  if echo "$INSPECT_JSON" | grep -q '"mods"'; then
    fail "the launcher's inspect output surfaced integration.mods (it must stay invisible to it)"
  else
    pass "inspect: integration.mods is silently absent from the parsed manifest"
  fi
  if PLAN_OUT=$("$LAUNCHER_BIN" plan "$LCHECK/runner.zip" --sha256 "$SHA" --game "$LCHECK/game" --exe game.exe \
      --platform windows --profile "$LCHECK/profile" --json 2>&1); then
    pass "plan: a manifest with [integration.mods] is accepted end to end by the 9aa7874 launcher"
  else
    fail "plan refused the manifest: $PLAN_OUT"
  fi
else
  echo "SKIP: launcher compatibility check ($LAUNCHER_REPO not found; set ASTRA_CHAIN_TEST_LAUNCHER)"
fi

echo
if [ "$FAIL" = 1 ]; then
  echo "test-chain: FAILED (scratch kept at $SCRATCH)" >&2
  exit 1
fi
echo "test-chain: all checks passed (scratch: $SCRATCH)"
