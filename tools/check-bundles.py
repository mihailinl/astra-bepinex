#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Check built composite-shader bundles: every pass of the shader source is in each bundle, with
compiled programs for every graphics API the bundle is for.

Unity leaves a pass that fails to compile for one API OUT of a bundle with no build error, and the
whole shader is then "not supported" in every game (2026-10-14: an FXC internal error did exactly
that). An editor older than 2020.2 cannot compile one pass for one API from a script, so this reads
what was BUILT, for every line alike. Run by tools/build-bundles.sh before a bundle is kept.

Usage: tools/check-bundles.py <shader source> <bundle>...
Needs UnityPy: python3 -m venv <dir> && <dir>/bin/pip install UnityPy (ASTRA_PYTHON=<dir>/bin/python
for build-bundles.sh).
"""
import re
import sys

try:
    import UnityPy
except ImportError:
    sys.exit("check-bundles: needs UnityPy — python3 -m venv <dir> && <dir>/bin/pip install UnityPy, "
             "then ASTRA_PYTHON=<dir>/bin/python")

# The graphics APIs each bundle is built for (BuildBundles.cs), as ShaderCompilerPlatform numbers:
# 4 D3D (D3D11 and D3D12), 15 OpenGLCore, 18 Vulkan.
PLATFORMS = {"windows": {4, 15, 18}, "linux": {15, 18}}
D3D = 4


def programs(prog):
    """A stage's compiled programs: the editor's list (older lines) and the player's lists (2021+)."""
    found = list(prog.get("m_SubPrograms") or [])
    for group in prog.get("m_PlayerSubPrograms") or []:
        found.extend(group)
    return found


def check(want, path):
    kind = "windows" if path.endswith("-windows.bundle") else "linux"
    shaders = [o.read_typetree() for o in UnityPy.load(path).objects if o.type.name == "Shader"]
    if len(shaders) != 1:
        return [f"{len(shaders)} shaders, not 1"]
    tree = shaders[0]
    problems = []
    platforms = set(tree.get("platforms") or [])
    if platforms != PLATFORMS[kind]:
        problems.append(f"graphics APIs {sorted(platforms)}, not {sorted(PLATFORMS[kind])}")
    passes = [p for sub in tree["m_ParsedForm"]["m_SubShaders"] for p in sub["m_Passes"]]
    names = [p.get("m_Name") or p["m_State"]["m_Name"] for p in passes]
    if names != want:
        problems.append(f"passes {names}, not {want}")
    for name, p in zip(names, passes):
        vertex, fragment = programs(p["progVertex"]), programs(p["progFragment"])
        # One vertex program per API (OpenGL and Vulkan carry the fragment stage in the same one);
        # D3D has its fragment program apart.
        if len(vertex) < len(platforms):
            problems.append(f"pass {name}: vertex programs for {len(vertex)} of {len(platforms)} APIs")
        if D3D in platforms and not fragment:
            problems.append(f"pass {name}: no D3D fragment program")
    return problems


def main():
    if len(sys.argv) < 3:
        sys.exit(__doc__)
    want = re.findall(r'Name\s+"([^"]+)"', open(sys.argv[1], encoding="utf-8").read())
    failed = False
    for path in sys.argv[2:]:
        problems = check(want, path)
        print(f"{path}: {'; '.join(problems) if problems else 'every pass, every API'}")
        failed |= bool(problems)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
