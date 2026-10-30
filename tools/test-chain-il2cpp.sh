#!/bin/bash
# Headless test for the IL2CPP/BepInEx 6 half of the chain loader (play together with the player's
# own mods, docs/GAME-INTEGRATION-MANIFEST.md §7's "IL2CPP flavour"). Sibling of tools/test-chain.sh
# (Mono/BepInEx 5); read that script's own header first — this one differs in two structural ways,
# both explained where they are used below:
#
# 1. BepInEx 6's own IL2CPPChainloader segfaults on THIS sandboxed host's testbed the moment it
#    touches GameAssembly/Il2CppInterop's native runtime-invoke detour — confirmed IDENTICAL for a
#    CHAINED launch (through Astra.Chain.CoreCLR) and an UNCHAINED direct one (straight at the real
#    preloader, no chain at all): same log, same crash point, same signal. That makes it an
#    environment/host issue with this testbed, not a defect in the chain (NOTES-lane.md has the
#    full comparison). So the "handoff" scenarios below (which DO run through the real
#    IL2CPPChainloader) can only prove the chain hands off cleanly and decides fallback/ok/
#    hooks-failed correctly — never that DiscoverPlugins/dedupe/config-redirect actually ran, because
#    Execute() is only ever called from the native detour that crashes first.
# 2. The "merge" scenarios instead drive Astra.Chain.Hooks.IL2CPP directly (Astra.Chain.MergeTest.dll,
#    a small test-only Doorstop target in tests/ChainIl2CppMergeTest) through a minimal
#    BaseChainloader<BasePlugin> subclass with NONE of IL2CPPChainloader's IL2CPP-native overrides —
#    so Execute()/DiscoverPlugins/LoadPlugins run as pure managed code, against the REAL be.788
#    BepInEx.Core/0Harmony/Mono.Cecil/BepInEx.Unity.IL2CPP assemblies, proving the actual
#    dedupe-by-GUID, config-redirect and dependency-fold behaviour the handoff scenarios cannot reach.
#    Both still run through the REAL Doorstop + REAL be.788 Linux runtime via the REAL testbed
#    player, because a BARE `dotnet exec` host (no Doorstop at all) rolls forward to whatever system
#    .NET is installed (there is no .NET 6 runtime on this box) and Harmony/MonoMod's native detour
#    patching segfaults deep inside a newer major CLR version's JIT — a SEPARATE crash class from
#    (1), confirmed while building this script; running under the exact 6.0.7 runtime production
#    actually ships avoids it entirely.
#
# Usage: tools/test-chain-il2cpp.sh
#   ASTRA_CHAIN_TEST_SCRATCH   where profiles are assembled (default: a dir under TMPDIR/$PWD/tmp)
#   ASTRA_CHAIN_TEST_PACKS     folder of <pack>/BepInEx* dirs (default: gi-bugs/packs/x — the Windows
#                              be.788 and V_Rising packs the design downloaded; never the owner's own
#                              game folders or Gale data)
#   ASTRA_CHAIN_TEST_TESTBED   the Linux IL2CPP testbed player's folder, WITH a real_bepinex.sh +
#                              libdoorstop.so beside it (default: unity/AstraTestbed/Build/il2cpp)
#   ASTRA_CHAIN_TEST_LINUX_BIE6  a cached be.788 Linux-x64 BepInEx.Unity.IL2CPP zip (default:
#                              downloaded once into scratch, sha256-pinned — see BEPINEX6_LINUX_*
#                              below; it ships no coreclr.dll/BepInEx.Unity.IL2CPP.dll Windows build
#                              can run natively on this host, only a Windows-native one does, per
#                              the design's own pack set — this is test infrastructure only, never
#                              shipped to a player)
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)

TMPDIR=${TMPDIR:-/tmp}
SCRATCH=${ASTRA_CHAIN_TEST_SCRATCH:-"$TMPDIR/astra-chain-il2cpp-test"}
PACKS_ROOT=${ASTRA_CHAIN_TEST_PACKS:-/mnt/data/claude-scratch/astra-avatar-engine/gi-bugs/packs/x}
TESTBED=${ASTRA_CHAIN_TEST_TESTBED:-"$REPO/unity/AstraTestbed/Build/il2cpp"}
BEPINEX6_LINUX_URL="https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-linux-x64-6.0.0-be.788%2B5b766a3.zip"
BEPINEX6_LINUX_SHA256=b9428e9f4b03f863114fe049ea8f578a45cd92ac68358136cd35b3fd6b3650c2

rm -rf "$SCRATCH"
mkdir -p "$SCRATCH"
export TMPDIR="$SCRATCH/tmp"
mkdir -p "$TMPDIR"

FAIL=0
fail() { echo "FAIL: $*" >&2; FAIL=1; }
pass() { echo "ok: $*"; }

[ -x "$TESTBED/AstraTestbed.x86_64" ] || { echo "test-chain-il2cpp: no testbed player at $TESTBED (build it: see CONTRIBUTING.md 'The testbed', TestbedBuild.BuildIl2Cpp)" >&2; exit 1; }
[ -x "$TESTBED/run_bepinex.sh" ] && [ -f "$TESTBED/libdoorstop.so" ] || { echo "test-chain-il2cpp: no run_bepinex.sh/libdoorstop.so beside $TESTBED (the Linux Doorstop launcher — place a be.788 Linux pack's copies there once)" >&2; exit 1; }
[ -d "$PACKS_ROOT" ] || { echo "test-chain-il2cpp: no packs at $PACKS_ROOT (ASTRA_CHAIN_TEST_PACKS)" >&2; exit 1; }

echo "== building the IL2CPP chain, hooks, the merge-test entry, fixtures =="
dotnet build src/Astra.Chain.CoreCLR -c Release -v q
dotnet build src/Astra.Chain.Hooks.IL2CPP -c Release -v q
dotnet build tests/ChainIl2CppMergeTest -c Release -v q
dotnet build tests/ChainFixtures.IL2CPP -c Release -v q
dotnet build tests/ChainConfigProbe.IL2CPP -c Release -v q

CHAIN_DLL="$REPO/src/Astra.Chain.CoreCLR/bin/Release/Astra.Chain.CoreCLR.dll"
HOOKS_DLL="$REPO/src/Astra.Chain.Hooks.IL2CPP/bin/Release/net6.0/Astra.Chain.Hooks.IL2CPP.dll"
MERGETEST_DIR="$REPO/tests/ChainIl2CppMergeTest/bin/Release"
FIXTURES_DLL="$REPO/tests/ChainFixtures.IL2CPP/bin/Release/net6.0/ChainFixtures.IL2CPP.dll"
PROBE_DLL="$REPO/tests/ChainConfigProbe.IL2CPP/bin/Release/net6.0/ChainConfigProbe.IL2CPP.dll"
BE788_WIN="$PACKS_ROOT/BepInEx-BepInExPack_IL2CPP-6.0.788/BepInExPack"
V_RISING="$PACKS_ROOT/BepInEx-BepInExPack_V_Rising-1.733.2/BepInExPack_V_Rising"
[ -d "$BE788_WIN/BepInEx/core" ] || { echo "test-chain-il2cpp: no be.788 pack at $BE788_WIN" >&2; exit 1; }
[ -d "$V_RISING/BepInEx/core" ] || { echo "test-chain-il2cpp: no V Rising pack at $V_RISING" >&2; exit 1; }

echo "== fetching the be.788 Linux-x64 runtime (test infrastructure only: Doorstop's CoreCLR host needs a native libcoreclr.so, and the design's own downloaded packs are the Windows build Proton actually runs in production) =="
LINUX_ZIP="$SCRATCH/bie6-linux.zip"
curl -sL -o "$LINUX_ZIP" "$BEPINEX6_LINUX_URL"
echo "$BEPINEX6_LINUX_SHA256  $LINUX_ZIP" | sha256sum -c --quiet
LINUXPACK="$SCRATCH/bie6-linux"
mkdir -p "$LINUXPACK"
unzip -q "$LINUX_ZIP" -d "$LINUXPACK"
[ -f "$LINUXPACK/dotnet/libcoreclr.so" ] || { echo "test-chain-il2cpp: Linux pack has no dotnet/libcoreclr.so" >&2; exit 1; }

# The LLVM linker this Unity editor bundles needs host libncurses/libxml2/ICU this box does not
# ship (CONTRIBUTING.md's "Old Unity editors on CachyOS" names the same class of gap for OLDER
# editors; this is the same gap on the editor this testbed was actually built with) — irrelevant
# here since the testbed player is already built; GLIBC_TUNABLES is the OTHER half of that same
# class of gap (execstack), needed at RUN time for Harmony/MonoMod's native detour patching.
RUN_ENV=(env GLIBC_TUNABLES=glibc.rtld.execstack=2 DBUS_SESSION_BUS_ADDRESS=)

# ---- helpers -----------------------------------------------------------------------------------

# make_astra_profile_il2cpp <dir>: Astra's OWN profile for the HANDOFF scenarios — the be.788 core
# (Linux build: this host's own native CoreCLR needs it; §9/docs/GAME-INTEGRATION-MANIFEST.md note
# production ships the Windows build under Proton, never this one, so ours stays test-only), the
# chain, and the config-probe fixture.
make_astra_profile_il2cpp() {
  local dir=$1
  rm -rf "$dir"
  mkdir -p "$dir/BepInEx/core" "$dir/BepInEx/plugins/Astra" "$dir/chain" "$dir/dotnet"
  cp -r "$LINUXPACK/BepInEx/core/." "$dir/BepInEx/core/"
  cp -r "$LINUXPACK/dotnet/." "$dir/dotnet/"
  cp "$PROBE_DLL" "$dir/BepInEx/plugins/Astra/"
  cp "$CHAIN_DLL" "$HOOKS_DLL" "$dir/chain/"
}

# make_their_profile_il2cpp <dir> <pack dir> <fixture value>: a scratch copy of a real downloaded
# pack's own core (managed IL, cross-platform — the handoff/version-gate logic never touches a
# native file of theirs) plus the fixture plugins. Reuses the testbed's OWN already-generated
# BepInEx/interop + unity-libs cache (same game, same Unity line, so the same Cpp2IL output is
# valid for any be.788-based "their" profile against it) — never writes into the testbed's own
# copy, only reads it: without this, every "their" profile's first run regenerates it from scratch
# (several real seconds of Cpp2IL work), which does not reliably finish before -astraQuit fires and
# can kill the process before our own InstallHooks/WriteLastRun ever run — not a chain bug, but this
# reuse makes the handoff scenarios' own pass/fail deterministic instead of a race against that.
make_their_profile_il2cpp() {
  local dir=$1 pack=$2 value=$3
  rm -rf "$dir"
  mkdir -p "$dir/BepInEx/plugins" "$dir/BepInEx/config"
  cp -r "$pack/BepInEx/core" "$dir/BepInEx/core"
  if [ -d "$TESTBED/BepInEx/interop" ]; then
    cp -r "$TESTBED/BepInEx/interop" "$dir/BepInEx/interop"
    cp -r "$TESTBED/BepInEx/unity-libs" "$dir/BepInEx/unity-libs"
  fi
  cp "$FIXTURES_DLL" "$dir/BepInEx/plugins/"
  printf '[Test]\nValue = %s\n' "$value" > "$dir/BepInEx/config/astra.test.fixturemod.cfg"
  printf '[Logging.Console]\nEnabled = false\n[Logging.Disk]\nEnabled = true\n' > "$dir/BepInEx/config/BepInEx.cfg"
}

write_chain_ini() { # <astra dir> <target> <source> <profile>
  cat > "$1/chain/chain.ini" <<EOF
[Chain]
target = $2
source = $3
profile = $4
EOF
}

# run_handoff <astra dir> <label>: Doorstop -> Astra.Chain.CoreCLR.dll, through the real testbed.
run_handoff() {
  local astra_dir=$1 label=$2
  ( cd "$TESTBED" && "${RUN_ENV[@]}" timeout 40 ./run_bepinex.sh ./AstraTestbed.x86_64 \
      --doorstop-enabled true --doorstop-target-assembly "$astra_dir/chain/Astra.Chain.CoreCLR.dll" \
      --doorstop-clr-corlib-dir "$astra_dir/dotnet" --doorstop-clr-runtime-coreclr-path "$astra_dir/dotnet/libcoreclr" \
      -batchmode -nographics -astraQuit 12 -logFile "$SCRATCH/unity-$label.log" \
      > "$SCRATCH/stdout-$label.log" 2>&1 ) || true
}

has() { grep -qF -- "$2" "$1" 2>/dev/null; } # has <file> <literal text>
hasnt() { ! grep -qF -- "$2" "$1" 2>/dev/null; }

echo "== handoff: ok (real be.788 pack) =="
ASTRA_DIR="$SCRATCH/astra-ok"
THEIR_DIR="$SCRATCH/their-ok"
make_astra_profile_il2cpp "$ASTRA_DIR"
# The Linux pack, not the design's own downloaded Windows one: their preloader must actually RUN
# here (unlike the wrong-build/malformed-ini/missing-target/broken-core scenarios, which never
# reach it), and BepInEx 6's native runtime-invoke detour needs a real libdobby.so beside it — the
# Windows pack's dobby.dll is a PE DLL this host's native loader cannot open at all
# (DllNotFoundException: "libdobby: cannot open shared object file"), which is test-infrastructure
# plumbing, not a chain concern: production always runs the Windows build, under Proton, which
# supplies its own native loader.
make_their_profile_il2cpp "$THEIR_DIR" "$LINUXPACK" "from-ok-profile"
TARGET="$THEIR_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll"
write_chain_ini "$ASTRA_DIR" "$TARGET" "manual" "ok-profile"
run_handoff "$ASTRA_DIR" "ok"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "ok " && has "$LAST" "6.0.0-be.788" \
  && pass "ok pack: last-run.txt says ok with their exact build ($(cat "$LAST"))" \
  || fail "ok pack: last-run.txt: $(cat "$LAST" 2>&1)"
has "$THEIR_DIR/BepInEx/LogOutput.log" "Chainloader initialized" \
  && pass "ok pack: their real preloader ran deep enough to initialize the chainloader (handoff + AssemblyLoadContext resolution of their core both worked)" \
  || fail "ok pack: their LogOutput.log never shows Chainloader initialized"

echo "== handoff: wrong build (real V Rising pack, a DIFFERENT be build) =="
ASTRA_DIR="$SCRATCH/astra-wrongbuild"
THEIR_DIR="$SCRATCH/their-wrongbuild"
make_astra_profile_il2cpp "$ASTRA_DIR"
make_their_profile_il2cpp "$THEIR_DIR" "$V_RISING" "from-wrongbuild-profile"
TARGET="$THEIR_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll"
write_chain_ini "$ASTRA_DIR" "$TARGET" "manual" "wrongbuild-profile"
run_handoff "$ASTRA_DIR" "wrongbuild"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "fallback " && has "$LAST" "6.0.0-be.788" \
  && pass "wrong build: the exact-build gate rejected a real, different BepInEx 6 build and fell back ($(cat "$LAST"))" \
  || fail "wrong build: last-run.txt: $(cat "$LAST" 2>&1)"
has "$ASTRA_DIR/BepInEx/LogOutput.log" "Chainloader initialized" \
  && pass "wrong build: Astra's own session still came up" \
  || fail "wrong build: Astra's own session did not come up"

echo "== handoff: malformed chain.ini (no [Chain] section) =="
ASTRA_DIR="$SCRATCH/astra-malformed"
make_astra_profile_il2cpp "$ASTRA_DIR"
printf '[NotChain]\nValue = whatever\n' > "$ASTRA_DIR/chain/chain.ini"
run_handoff "$ASTRA_DIR" "malformed"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "fallback " && pass "malformed chain.ini: falls back ($(cat "$LAST"))" \
  || fail "malformed chain.ini: last-run.txt: $(cat "$LAST" 2>&1)"
has "$ASTRA_DIR/BepInEx/LogOutput.log" "Chainloader initialized" \
  && pass "malformed chain.ini: Astra's own session still came up" \
  || fail "malformed chain.ini: Astra's own session did not come up"

echo "== handoff: missing target =="
ASTRA_DIR="$SCRATCH/astra-missing"
make_astra_profile_il2cpp "$ASTRA_DIR"
write_chain_ini "$ASTRA_DIR" "$SCRATCH/does-not-exist/BepInEx/core/BepInEx.Unity.IL2CPP.dll" "gale" "Missing"
run_handoff "$ASTRA_DIR" "missing"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "fallback " && pass "missing target: falls back ($(cat "$LAST"))" \
  || fail "missing target: last-run.txt: $(cat "$LAST" 2>&1)"

echo "== handoff: broken core (BepInEx.Core.dll missing beside a real-looking preloader) =="
BROKEN_CORE="$SCRATCH/broken-core/BepInEx/core"
rm -rf "$SCRATCH/broken-core"; mkdir -p "$BROKEN_CORE"
cp -r "$BE788_WIN/BepInEx/core/." "$BROKEN_CORE/"
rm -f "$BROKEN_CORE/BepInEx.Core.dll"
ASTRA_DIR="$SCRATCH/astra-brokencore"
make_astra_profile_il2cpp "$ASTRA_DIR"
write_chain_ini "$ASTRA_DIR" "$BROKEN_CORE/BepInEx.Unity.IL2CPP.dll" "manual" "broken-core"
run_handoff "$ASTRA_DIR" "brokencore"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "fallback " && has "$LAST" "BepInEx.Core.dll" \
  && pass "broken core: ValidateTarget caught the missing BepInEx.Core.dll and fell back ($(cat "$LAST"))" \
  || fail "broken core: last-run.txt: $(cat "$LAST" 2>&1)"

echo "== handoff: a profile name with # (comment-stripping regression, mirrors the Mono review's S3) =="
ASTRA_DIR="$SCRATCH/astra-hashname"
THEIR_DIR="$SCRATCH/their-hashname"
make_astra_profile_il2cpp "$ASTRA_DIR"
make_their_profile_il2cpp "$THEIR_DIR" "$LINUXPACK" "from-hashname-profile" # must actually run; see the "ok" scenario's own comment
write_chain_ini "$ASTRA_DIR" "$THEIR_DIR/BepInEx/core/BepInEx.Unity.IL2CPP.dll" "gale" "My #1"
run_handoff "$ASTRA_DIR" "hashname"
LAST="$ASTRA_DIR/chain/last-run.txt"
has "$LAST" "ok " && pass "profile name with #: the chain still succeeds ($(cat "$LAST"))" \
  || fail "profile name with #: last-run.txt: $(cat "$LAST" 2>&1)"

# ---- merge: Astra.Chain.Hooks.IL2CPP's own dedupe/config-redirect/dependency-fold, bypassing
# IL2CPPChainloader's native layer entirely (see this script's own header, point 2). ------------

# make_merge_layout <dir>: <dir>/ours/{BepInEx/{core,plugins,config}, chain, mergetest, fakegame/
# BepInEx/{core,plugins,config}}. ours/BepInEx/core and fakegame/BepInEx/core both get the WHOLE
# be.788 Linux pack's core PLUS its dotnet/ folder (Mono.Cecil's own resolver falls back to
# Paths.BepInExAssemblyDirectory/PluginPath/PatcherPluginPath/ManagedPath — none of which is a real
# "Managed" folder for an IL2CPP game, so the base-framework facades it also needs, e.g.
# System.Runtime/System.Private.CoreLib, have to be reachable from BepInExAssemblyDirectory here;
# a REAL game's own BepInEx/core never needs this because production never reaches this code path
# through this harness in the first place — only this sandboxed substitute does).
make_merge_layout() {
  local dir=$1
  rm -rf "$dir"
  local ours="$dir/ours"
  mkdir -p "$ours/BepInEx/core" "$ours/BepInEx/plugins" "$ours/BepInEx/config" \
           "$ours/chain" "$ours/mergetest" \
           "$ours/fakegame/BepInEx/core" "$ours/fakegame/BepInEx/plugins" "$ours/fakegame/BepInEx/config" \
           "$ours/fakegame/game_Data"
  : > "$ours/fakegame/game.exe"
  cp -r "$LINUXPACK/BepInEx/core/." "$ours/BepInEx/core/"
  cp -r "$LINUXPACK/BepInEx/core/." "$ours/fakegame/BepInEx/core/"
  cp -n "$LINUXPACK"/dotnet/*.dll "$ours/BepInEx/core/" 2>/dev/null || true
  cp -n "$LINUXPACK"/dotnet/*.dll "$ours/fakegame/BepInEx/core/" 2>/dev/null || true
  cp "$PROBE_DLL" "$ours/BepInEx/plugins/"
  cp -r "$MERGETEST_DIR/." "$ours/mergetest/"
  printf '[Logging.Console]\nEnabled = false\n[Logging.Disk]\nEnabled = true\n' > "$ours/fakegame/BepInEx/config/BepInEx.cfg"
}

run_mergetest() { # <dir> <label>
  local dir=$1 label=$2
  ( cd "$TESTBED" && "${RUN_ENV[@]}" timeout 40 ./run_bepinex.sh ./AstraTestbed.x86_64 \
      --doorstop-enabled true --doorstop-target-assembly "$dir/ours/mergetest/Astra.Chain.MergeTest.dll" \
      --doorstop-clr-corlib-dir "$LINUXPACK/dotnet" --doorstop-clr-runtime-coreclr-path "$LINUXPACK/dotnet/libcoreclr" \
      -batchmode -nographics -astraQuit 12 -logFile "$SCRATCH/unity-$label.log" \
      > "$SCRATCH/stdout-$label.log" 2>&1 ) || true
}

echo "== merge: dedupe-by-GUID, config redirect, dependency fold (real be.788 assemblies) =="
DIR="$SCRATCH/merge-full"
make_merge_layout "$DIR"
cp "$FIXTURES_DLL" "$DIR/ours/fakegame/BepInEx/plugins/"
printf '[Test]\nValue = from-their-profile\n' > "$DIR/ours/fakegame/BepInEx/config/astra.test.fixturemod.cfg"
run_mergetest "$DIR" "merge-full"
RESULT="$DIR/ours/mergetest/merge-result.txt"
LOG="$DIR/ours/fakegame/BepInEx/LogOutput.log"
has "$RESULT" "ok " && pass "merge: Execute() completed ok ($(cat "$RESULT"))" \
  || fail "merge: merge-result.txt: $(cat "$RESULT" 2>&1)"
has "$LOG" "Skipping astra.unity-foundation" && pass "merge: dedupe-by-GUID skipped the stale Astra copy" \
  || fail "merge: no 'Skipping astra.unity-foundation' line"
hasnt "$LOG" "StaleAstra Load ran" && pass "merge: the stale Astra copy's Load() never ran" \
  || fail "merge: StaleAstra's Load() ran — dedupe did not actually remove it"
has "$LOG" "FixtureMod value=from-their-profile" && pass "merge: FixtureMod ran with THEIR profile's own config value" \
  || fail "merge: FixtureMod did not read its profile's own value"
has "$LOG" "DependsOnAstra loaded" && pass "merge: a dependency on a plugin folded in by the chain resolved and loaded" \
  || fail "merge: DependsOnAstra (depends on astra.unity-foundation, merged in by the chain) did not load"
has "$LOG" "FoundationStandIn loaded" && pass "merge: the GUID 'ours' owns (astra.unity-foundation) loaded from OUR plugins dir" \
  || fail "merge: FoundationStandIn did not load"
PROBE_CFG="$DIR/ours/BepInEx/config/astra.chain.config-probe.cfg"
[ -f "$PROBE_CFG" ] && has "$PROBE_CFG" "written-by-ours" \
  && pass "merge: Astra's own plugin wrote its config into ASTRA's profile" \
  || fail "merge: astra.chain.config-probe.cfg missing/wrong in Astra's own profile"
[ ! -e "$DIR/ours/fakegame/BepInEx/config/astra.chain.config-probe.cfg" ] \
  && pass "merge: nothing of Astra's config landed in THEIR profile" \
  || fail "merge: astra.chain.config-probe.cfg leaked into their profile"

echo "== merge: hooks-failed (ours/plugins missing) — their mods still load =="
DIR="$SCRATCH/merge-nopluginsdir"
make_merge_layout "$DIR"
rm -rf "$DIR/ours/BepInEx/plugins"
cp "$FIXTURES_DLL" "$DIR/ours/fakegame/BepInEx/plugins/"
printf '[Test]\nValue = from-nopluginsdir-profile\n' > "$DIR/ours/fakegame/BepInEx/config/astra.test.fixturemod.cfg"
run_mergetest "$DIR" "merge-nopluginsdir"
LOG="$DIR/ours/fakegame/BepInEx/LogOutput.log"
has "$LOG" "could not fold Astra's own plugins" && pass "hooks-failed: the postfix caught the missing ours/plugins dir and logged it" \
  || fail "hooks-failed: no 'could not fold' error in their LogOutput.log"
has "$LOG" "FixtureMod value=from-nopluginsdir-profile" && pass "hooks-failed: their own mods still loaded, untouched" \
  || fail "hooks-failed: their mods did not load"
hasnt "$LOG" "Astra loads its own copy" && pass "hooks-failed: no dedupe happened (ours never merged in)" \
  || fail "hooks-failed: a dedupe line appeared despite ours/plugins being empty"

echo
if [ "$FAIL" = 1 ]; then
  echo "test-chain-il2cpp: FAILED (scratch kept at $SCRATCH)" >&2
  exit 1
fi
echo "test-chain-il2cpp: all checks passed (scratch: $SCRATCH)"
