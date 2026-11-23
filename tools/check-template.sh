#!/bin/bash
# Generate repositories from templates/astra-integration — with a game name, an exe and a game folder
# that hold every character that breaks a C# or TOML string or an XML file — and check that what
# comes out builds, parses and is what Astra's launcher reads: the Mono one is fetched (tools/fetch.sh:
# network), built with warnings as errors and packed into astra-gi.zip; both runtimes' manifests parse
# as TOML; the release workflow pins every action by commit. An --id or --owner that is not one is
# REFUSED: nothing is written, and dotnet new prints why. The template is installed into a PRIVATE template cache (dotnet new's
# --debug:custom-hive), never into yours.
#
# And what comes out with NO --id and NO --owner — the template as an author gets it — is a release
# Astra's Workshop takes: `gi-probe check --provenance community` (the catalogue's own check, from
# astra-game-launcher's release, pinned below by version and SHA-256) must pass with no finding on
# the Mono zip built here, and on the IL2CPP zip packed here. An IL2CPP plugin compiles against a
# game's own interop assemblies, so no machine without the game builds it: its flavour releases a
# build the author COMMITS in prebuilt/. Here a placeholder stands in for the plugin's DLL, the
# project's own targets write prebuilt/ around it, and the flavour's own packer packs the zip —
# every step of that flavour's release except the compiler.
#
# And each flavour's release workflow ATTESTS the zip the way AstraPlugins' templates/gi-release.yml
# does (the catalogue's reference for an author's release workflow; pinned below by commit and
# SHA-256). The catalogue takes a community release only with a build provenance attestation signed
# by the author's own workflow, so the two may not drift. What is compared, per generated
# release.yml, against the reference:
#   - the commit actions/attest-build-provenance is pinned to: the same 40 hex digits;
#   - the workflow's default permissions and the release job's permissions: the same key/value sets
#     (today: contents read; and contents, id-token, attestations write);
#   - the attested subject: astra-gi.zip in both (the reference's subject-path, through its ASSET);
# and, of ours alone: exactly one attestation step, placed BEFORE the step that publishes the
# release, and a step that checks the tag against the version.
# .github/workflows/template.yml runs this script on every change to the template.
#
# Usage: tools/check-template.sh
#   GI_PROBE                  a gi-probe binary to use instead of downloading the pinned one (it
#                             must still say the pinned version). Without it, on anything but
#                             Linux x64 — the one build gi-probe is released for — the Workshop
#                             check prints SKIP.
#   ASTRA_GAME_LAUNCHER_REPO  astra-game-launcher's checkout, built fresh into a scratch target dir
#                             and never written to (default: ../astra-game-launcher). Its `inspect`
#                             is run against the packed zip below — a real launcher check, not just
#                             "does this TOML parse": a [[files]] rule matching nothing in the zip
#                             (e.g. a chain/** rule the pinned runner release does not ship yet) is
#                             "not in the zip" to the launcher and would get every integration built
#                             from this template refused at install. Missing the repo prints SKIP.
set -euo pipefail
cd "$(dirname "$0")/.."
REPO=$(pwd)
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
HIVE=(--debug:custom-hive "$W/hive")
fail() { echo "check-template: $*" >&2; exit 1; }
# zip_has <zip> <path>: the zip holds that file. Never `unzip -l | grep -q`: grep -q leaves at the
# first match, unzip dies of SIGPIPE writing the rest, and pipefail calls a zip that has the file a
# failure (0.9.0's IL2CPP runner lists 4 more entries after the plugin, and the check went red).
zip_has() { unzip -Z1 "$1" | grep -Fxc -- "$2" > /dev/null; }

# The Workshop's check: bump the three together (the digest is in the release's gi-probe-linux-x64.sha256).
GI_PROBE_VERSION=0.5.1
GI_PROBE_URL=https://github.com/mihailinl/astra-game-launcher/releases/download/v$GI_PROBE_VERSION/gi-probe-linux-x64
GI_PROBE_SHA256=dd5e1a2708042222e52bbcaa477396480853c615aa209d78fd329451381bc4a9

# The catalogue's reference release workflow: bump the commit and the digest together
# (sha256sum of the file at that commit), then make the template's release.yml agree with it.
GI_RELEASE_COMMIT=53b832785bcc20953f12d471ad1181092a75d774
GI_RELEASE_URL=https://raw.githubusercontent.com/mihailinl/AstraPlugins/$GI_RELEASE_COMMIT/templates/gi-release.yml
GI_RELEASE_SHA256=7eb5d57c28825d7f9f4a3b4e56cfb0b089f28d9f64b3f38b82d846f31b1dc7cd

dotnet new install "$REPO/templates/astra-integration" "${HIVE[@]}" >/dev/null
GAME='X "Gate" & <Co>\Back'
new() { # new <dir> <runtime> [more options]: the options a later one names win
  local dir=$1 runtime=$2; shift 2
  dotnet new astra-integration "${HIVE[@]}" -o "$W/$dir" -n AstraHostile --game "$GAME" --runtime "$runtime" \
    --steamAppId 1234560 --owner Some-One --id some-one.x_y-z.1 "$@" > "$W/$dir.out"
}
new mono mono
new il2cpp il2cpp
new gamedir mono --gameDir '/games/A & B "c"'
new exe mono --exe 'bin\Game "x".exe'
# Two more, clean-named and with the template's OWN defaults (no --id, no --owner), for the Workshop
# and launcher checks below: what an author gets must pass as it is. They are separate from the
# hostile ones because the hostile GAME name's `<`/`>` (deliberately picked to stress TOML/C#/XML
# escaping) makes `target.exe` a string no Windows filename could ever hold, which the real launcher
# correctly refuses (astra-game-launcher's check_component: `<>:"|?*\` are illegal there) — a fact
# about Windows paths, not about this template.
for rt in mono il2cpp; do
  dotnet new astra-integration "${HIVE[@]}" -o "$W/community-$rt" -n AstraMyGame --game 'My Game' \
    --runtime "$rt" --steamAppId 1234560 > "$W/community-$rt.out"
done

# Refused: an id or owner that is not one is never rewritten into another; nothing is written.
refused() { # refused <dir> <what the output must name> <options…>
  local dir=$1 says=$2; shift 2
  dotnet new astra-integration "${HIVE[@]}" -o "$W/$dir" -n AstraHostile --game G --steamAppId 1 "$@" > "$W/$dir.out"
  [ -z "$(ls -A "$W/$dir" 2>/dev/null)" ] || fail "$dir: files were written for a refused option: $(ls -A "$W/$dir" | tr '\n' ' ')"
  grep -q 'REFUSED' "$W/$dir.out" && grep -qF -- "$says" "$W/$dir.out" || fail "$dir: dotnet new did not say why: $(cat "$W/$dir.out")"
}
refused bad-id '--id is not an integration id' --id 'some-one.x"y z'
# astra. and minice. are Astra's own prefixes: the Workshop refuses them in anyone else's release
# (reserved-id, in any letter case), so the template does first.
refused reserved-id '--id is not an integration id' --id astra.mygame
refused reserved-id-2 '--id is not an integration id' --id Minice.my-game
refused bad-owner '--owner is not a GitHub' --owner 'Some One!'
refused bad-owner-2 '--owner is not a GitHub' --owner '-dash'
echo "refusals: OK"

# The manifests: TOML that says what the options meant.
python3 - "$W" <<'EOF'
import sys, tomllib
w = sys.argv[1]
for rt in ("mono", "il2cpp"):
    gi = tomllib.load(open(f"{w}/{rt}/astra-gi.toml", "rb"))
    item = tomllib.load(open(f"{w}/{rt}/astra-item.toml", "rb"))
    want = "Astra for X 'Gate' & <Co>Back"
    assert gi["integration"]["name"] == want, gi["integration"]["name"]
    assert item["name"] == want, item["name"]
    assert gi["integration"]["id"] == item["id"] == "some-one.x_y-z.1", gi["integration"]["id"]
    assert gi["integration"]["authors"] == ["Some-One"], gi["integration"]["authors"]
    assert gi["integration"]["repo"] == "https://github.com/Some-One/AstraHostile", gi["integration"]["repo"]
    assert gi["target"]["exe"] == "X 'Gate' & <Co>Back.exe", gi["target"]["exe"]
    assert gi["target"]["steam_appid"] == 1234560
exe = tomllib.load(open(f"{w}/exe/astra-gi.toml", "rb"))["target"]["exe"]
assert exe == "bin/Game 'x'.exe", exe   # a \ is a path separator: it becomes /, never dropped
print("manifests: OK")
EOF

# The projects: XML MSBuild reads, with the values the options meant.
for rt in mono il2cpp; do
  name=$(dotnet msbuild "$W/$rt/AstraHostile.csproj" -getProperty:BepInExPluginName)
  [ "$name" = "Astra for X 'Gate' & <Co>Back" ] || fail "$rt: BepInExPluginName is '$name'"
done
dir=$(dotnet msbuild "$W/gamedir/AstraHostile.csproj" -getProperty:GameDir)
[ "$dir" = '/games/A & B "c"' ] || fail "GameDir is '$dir'"
echo "projects: OK"

# The workflows, both flavours: every action pinned by a full commit SHA, and the attestation the
# catalogue's reference workflow makes (the header says what is compared).
curl -fsSL -o "$W/gi-release.yml" "$GI_RELEASE_URL" || fail "the reference workflow did not download ($GI_RELEASE_URL)"
echo "$GI_RELEASE_SHA256  $W/gi-release.yml" | sha256sum -c --quiet >/dev/null 2>&1 \
  || fail "$GI_RELEASE_URL is not the file this script pins (sha256 $GI_RELEASE_SHA256)"
for inst in mono il2cpp community-mono community-il2cpp; do
  wf="$W/$inst/.github/workflows/release.yml"
  [ -f "$wf" ] || fail "$inst: no .github/workflows/release.yml (both flavours release through one)"
  unpinned=$(grep -E '^\s*(- )?uses:' "$wf" | grep -vE '@[0-9a-f]{40}( |$)' || true)
  [ -z "$unpinned" ] || fail "$inst: actions not pinned by commit: $unpinned"
  python3 - "$W/gi-release.yml" "$wf" <<'EOF' || fail "$inst: release.yml does not attest the way the reference workflow does"
import re, sys

def read(path):
    return open(path, encoding="utf-8").read().splitlines()

def permissions(lines):
    """The 'permissions:' blocks: the workflow's (no indent) and the jobs' (indented), as sets."""
    top, jobs = None, []
    for i, line in enumerate(lines):
        m = re.match(r"^(\s*)permissions:\s*(#.*)?$", line)
        if not m:
            continue
        block = set()
        for inner in lines[i + 1:]:
            if not inner.strip() or inner.lstrip().startswith("#"):
                continue
            if len(inner) - len(inner.lstrip()) <= len(m.group(1)):
                break
            key, _, value = inner.split("#")[0].strip().partition(":")
            block.add((key.strip(), value.strip()))
        if m.group(1):
            jobs.append(block)
        else:
            top = block
    return top, jobs

def attestation(lines):
    """[(line index, pinned commit, subject)] of every actions/attest-build-provenance step."""
    env = {}
    for line in lines:
        m = re.match(r"^\s+([A-Z_]+):\s*([^\s#]+)\s*(#.*)?$", line)
        if m:
            env.setdefault(m.group(1), m.group(2))
    found = []
    for i, line in enumerate(lines):
        m = re.match(r"^\s*(?:- )?uses:\s*actions/attest-build-provenance@(\S+)", line)
        if not m:
            continue
        subject = None
        for inner in lines[i + 1:i + 6]:
            s = re.match(r"^\s+subject-path:\s*(.+?)\s*(#.*)?$", inner)
            if s:
                subject = s.group(1).strip("'\"")
                e = re.fullmatch(r"\$\{\{\s*env\.([A-Z_]+)\s*\}\}", subject)
                if e:
                    subject = env.get(e.group(1))
                break
        found.append((i, m.group(1), subject))
    return found

ref, ours = read(sys.argv[1]), read(sys.argv[2])
wrong = []
ref_att, our_att = attestation(ref), attestation(ours)
if len(ref_att) != 1:
    sys.exit(f"the reference workflow has {len(ref_att)} attestation steps: this check no longer reads it")
if len(our_att) != 1:
    sys.exit(f"{len(our_att)} actions/attest-build-provenance steps, expected exactly one")
(_, ref_pin, ref_subject), (at, pin, subject) = ref_att[0], our_att[0]
if not re.fullmatch(r"[0-9a-f]{40}", pin):
    wrong.append(f"actions/attest-build-provenance is pinned to '{pin}', not a 40-hex commit")
if pin != ref_pin:
    wrong.append(f"actions/attest-build-provenance@{pin}; the reference pins {ref_pin}")
if ref_subject != "astra-gi.zip":
    sys.exit(f"the reference attests '{ref_subject}', not astra-gi.zip: this check no longer reads it")
if subject != ref_subject:
    wrong.append(f"the attested subject is '{subject}'; the reference attests {ref_subject}")
(ref_top, ref_jobs), (top, jobs) = permissions(ref), permissions(ours)
if len(ref_jobs) != 1 or ref_top is None:
    sys.exit("the reference workflow's permissions are not one default block and one job block: this check no longer reads it")
show = lambda block: ", ".join(f"{k}: {v}" for k, v in sorted(block)) if block else "none"
if top != ref_top:
    wrong.append(f"the workflow's default permissions are [{show(top)}]; the reference's are [{show(ref_top)}]")
if jobs != ref_jobs:
    wrong.append(f"the job's permissions are {[show(j) for j in jobs]}; the reference's are [{show(ref_jobs[0])}]")
publish = [i for i, line in enumerate(ours) if re.match(r"^\s*(?:- )?uses:\s*softprops/action-gh-release@", line)]
if len(publish) != 1 or publish[0] < at:
    wrong.append("the attestation step is not before the one step that publishes the release")
if not any("GITHUB_REF_NAME#v" in line for line in ours):
    wrong.append("no step checks the tag against the version")
for w in wrong:
    print("  " + w, file=sys.stderr)
sys.exit(1 if wrong else 0)
EOF
done
echo "workflow: OK"

# The Mono one end to end: fetch, build as CI does, pack the zip.
(
  cd "$W/mono"
  bash tools/fetch.sh >/dev/null
  dotnet build -c Release -p:TreatWarningsAsErrors=true -v q > build.log 2>&1 || { tail -20 build.log >&2; exit 1; }
  bash tools/make-astra-gi.sh >/dev/null
  zip_has astra-gi.zip BepInEx/plugins/AstraHostile/AstraHostile.dll
) || fail "the Mono project did not build and pack"
echo "build and zip: OK"

# The template as an author gets it, built and packed as each flavour's own release workflow does.
(
  cd "$W/community-mono"
  bash tools/fetch.sh >/dev/null
  dotnet build -c Release -p:TreatWarningsAsErrors=true -v q > build.log 2>&1 || { tail -20 build.log >&2; exit 1; }
  bash tools/make-astra-gi.sh >/dev/null
) || fail "the default Mono project did not build and pack"
# IL2CPP releases a COMMITTED build (see the header). A placeholder stands in for the DLL the
# compiler would write; the project's own targets (the marker, then the copy into prebuilt/) run as
# they do after a real Release build; then the flavour's packer packs prebuilt/, as its workflow does.
# (errexit is off inside a `( … ) || fail` block, so every step here says `|| exit 1` itself.)
(
  cd "$W/community-il2cpp" || exit 1
  [ ! -e prebuilt ] || { echo "the template ships a prebuilt/ of its own" >&2; exit 1; }
  # (MSBuild prints a property as written, "bin\Release/net6.0/"; the build itself reads it with /.)
  out=$(dotnet msbuild AstraMyGame.csproj -getProperty:OutDir -p:Configuration=Release | tr '\\' '/')
  [ -n "$out" ] && mkdir -p "$out" && printf 'a placeholder for the plugin tools/check-template.sh cannot compile\n' > "$out/AstraMyGame.dll" || exit 1
  dotnet msbuild AstraMyGame.csproj -t:AstraItemMarker -p:Configuration=Release -v:q -nologo > prebuilt.log 2>&1 || { tail -20 prebuilt.log >&2; exit 1; }
  [ "$(ls -A prebuilt | LC_ALL=C sort | tr '\n' ' ')" = "AstraMyGame.dll astra-item.json " ] || { echo "prebuilt/ holds: $(ls -A prebuilt | tr '\n' ' ')" >&2; exit 1; }
  # The generated .gitignore: the committed build is committable, the game's interop is not.
  git init -q . || exit 1
  for ours in prebuilt/AstraMyGame.dll prebuilt/astra-item.json; do   # check-ignore: 0 ignored, 1 not, 128 an error
    git check-ignore -q "$ours"; [ $? = 1 ] || { echo "the template's .gitignore hides $ours from a commit" >&2; exit 1; }
  done
  for theirs in BepInEx/plugins/Other.dll interop/GameCode.dll Il2Cppmscorlib.dll Assembly-CSharp.dll UnityEngine.CoreModule.dll Unity.TextMeshPro.dll; do
    git check-ignore -q "$theirs" || { echo "the template's .gitignore lets $theirs (the game's, or a copied profile's) be committed" >&2; exit 1; }
  done
  bash tools/fetch.sh >/dev/null || exit 1
  bash tools/make-astra-gi.sh >/dev/null || exit 1
  zip_has astra-gi.zip BepInEx/plugins/AstraMyGame/AstraMyGame.dll || exit 1
  zip_has astra-gi.zip BepInEx/plugins/AstraMyGame/astra-item.json || exit 1
) || fail "the default IL2CPP project did not pack its committed build"

# The Workshop's own check on both. A finding of any kind fails: the template is what a community
# author copies, so it may not carry a line the catalogue refuses (2026-11: it shipped without
# [integration.loader] and [integration.capabilities], and with an astra.* default id).
PROBE=${GI_PROBE:-}
if [ -z "$PROBE" ] && [ "$(uname -sm)" = "Linux x86_64" ]; then
  PROBE="$W/gi-probe"
  curl -fsSL -o "$PROBE" "$GI_PROBE_URL" || fail "gi-probe $GI_PROBE_VERSION did not download ($GI_PROBE_URL)"
  echo "$GI_PROBE_SHA256  $PROBE" | sha256sum -c --quiet >/dev/null 2>&1 \
    || fail "$GI_PROBE_URL is not the gi-probe this script pins (sha256 $GI_PROBE_SHA256)"
  chmod +x "$PROBE"
fi
if [ -n "$PROBE" ]; then
  said=$("$PROBE" --version) || fail "gi-probe ($PROBE) did not run"
  case "$said" in "gi-probe $GI_PROBE_VERSION"|"gi-probe $GI_PROBE_VERSION "*) ;; *) fail "gi-probe is '$said', this script pins $GI_PROBE_VERSION";; esac
  for rt in mono il2cpp; do
    "$PROBE" check "$W/community-$rt/astra-gi.zip" --provenance community > "$W/probe-$rt.json" || true
    python3 - "$W/probe-$rt.json" "$rt" <<'EOF' || fail "gi-probe $GI_PROBE_VERSION refuses the $rt template as a community release"
import json, sys
try:
    r = json.load(open(sys.argv[1]))
except ValueError:
    sys.exit("gi-probe printed no report: " + open(sys.argv[1]).read()[:400])
for f in r["findings"]:
    print(f"  {f['code']}: {f['detail']}", file=sys.stderr)
ok = r["verdict"] == "pass" and not r["findings"] and r["input"]["provenance"] == "community" and r["input"]["kind"] == "zip"
sys.exit(0 if ok else f"verdict {r['verdict']}, {len(r['findings'])} finding(s)")
EOF
  done
  echo "gi-probe $GI_PROBE_VERSION check (community): OK"
else
  echo "SKIP: gi-probe check (no gi-probe build for $(uname -sm); set GI_PROBE)"
fi

# The launcher itself, not just a TOML parse: a [[files]] rule matching nothing in the zip is
# refused outright (astra-game-launcher's package.rs, "not in the zip") — the exact mistake that
# shipped [integration.mods] + chain/** pinned to a runner release with no chain/ in its zip.
LAUNCHER_REPO=${ASTRA_GAME_LAUNCHER_REPO:-"$REPO/../astra-game-launcher"}
if [ -d "$LAUNCHER_REPO" ]; then
  ( cd "$LAUNCHER_REPO" && CARGO_TARGET_DIR="$W/launcher-build" cargo build --release -q ) \
    || fail "astra-game-launcher ($LAUNCHER_REPO) did not build"
  LAUNCHER_BIN="$W/launcher-build/release/astra-game-launcher"
  ZIP="$W/community-mono/astra-gi.zip"
  SHA=$(sha256sum "$ZIP" | cut -d' ' -f1)
  OUT=$("$LAUNCHER_BIN" inspect "$ZIP" --sha256 "$SHA" --json 2>&1) \
    || fail "astra-game-launcher refused the packed template zip: $OUT"
  echo "launcher inspect: OK"
else
  echo "SKIP: launcher inspect ($LAUNCHER_REPO not found; set ASTRA_GAME_LAUNCHER_REPO)"
fi
echo "check-template: OK"
