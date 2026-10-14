# Contributing to astra-bepinex

This repository is the Unity side of Astra in games: the **foundation** (a BepInEx plugin that puts
Astra into any Unity game) and the **SDK** that game integrations build on. It is MIT, and it is
meant to be extended by players and modders: fixes for games where she misbehaves, shader bundles
for more Unity lines, robustness for IL2CPP builds, and new integrations.

- Want Astra to do better in **one** game? Write an integration:
  [`docs/WRITE-AN-INTEGRATION.md`](docs/WRITE-AN-INTEGRATION.md). It lives in its own repository.
- Want to fix or extend the **foundation** itself? Read on.
- Found a game where she does not work? See [Reporting a game that does not work](#reporting-a-game-that-does-not-work).

## The rules

1. **MIT.** Everything here is MIT ([`LICENSE`](LICENSE)), and what you contribute is MIT too. New
   source files start with `// SPDX-License-Identifier: MIT`.
2. **The foundation stays generic.** A change here must help every Unity game of its kind ("Built-in
   games whose shaders have no ShadowCaster pass", "IL2CPP builds that stripped an event accessor").
   Anything about ONE game — its player type, its layers, where she should stand, its scale — belongs
   in that game's integration. If your fix names a game, a game type or a game's layer, it is an
   integration. The foundation never names a game in its logic.
3. **Never commit game files.** No DLLs from a game's `<Game>_Data/Managed`, no `BepInEx/interop`, no
   decompiled game code, no extracted game assets. The build reads such folders at compile time
   only (`Private="false"`: nothing is copied). Your `*.user` files are git-ignored; keep paths to
   your games there or in environment variables.
4. **Binary shader bundles from contributors are rebuilt by maintainers from source before any
   release.** A bundle (`unity/AstraShaders/Build/astra-*.bundle`) is compiled binary data. It is
   embedded in `Astra.Unity.dll`, which runs inside every player's game. A reviewer cannot read a
   bundle, so nobody can tell from the diff whether it holds only the reviewed shader, an older
   version of it, or something else. The release must come from the reviewed source and a known
   editor version. So a bundle in your pull request is for review and testing; before a release a
   maintainer builds it again from `unity/AstraShaders/Assets` with the editor version you name, and
   ships that build.
5. **No anti-cheat games.** Never test in, or add support for, a game with kernel or server-side
   anti-cheat (EasyAntiCheat, BattlEye, Vanguard…). Mods get accounts banned there. Pull requests
   that help get around anti-cheat are refused.
6. **Raw facts, never decisions.** The foundation sends the engine what IS (`speed = 4.1`,
   `airborne = true`), never what to play (`gait = "run"`). What a fact looks like is decided by
   her animation set, which players can edit.
7. **She must never get stuck.** An optional feature that fails switches itself off and the rest
   goes on (`src/Astra.Bridge.Core/Feature.cs`). Only the essential chain (the shader, the camera
   hook, the picture, the composite, the link and the ring) may stop her, and then she goes back to
   the desktop.

## The repository

| path | what it is |
|---|---|
| `src/Astra.Bridge.Core` | The game-bridge protocol, the WebSocket client and the shared-memory frame ring. .NET Standard 2.0, no Unity. Unit-tested. |
| `src/Astra.Unity` | The foundation's Unity code, compiled into BOTH plugins (`Astra.Unity.props`). `Sdk/` is the public API (`Astra.Sdk` namespace). |
| `src/Astra.BepInEx5` | The Mono plugin (BepInEx 5, `netstandard2.0`). Output: `Astra.Unity.dll`. |
| `src/Astra.BepInEx6.IL2CPP` | The IL2CPP plugin (BepInEx 6, `net6.0`, the `IL2CPP` symbol). Output: `Astra.Unity.dll`. |
| `src/Astra.Urp17`, `src/Astra.Urp14`, `src/Astra.Hdrp` | Render-pipeline adapters for Mono games (URP 17 in Unity 6, URP 13–16 in Unity 2022–2023, HDRP). Each compiles against that pipeline's own assemblies. |
| `src/Astra.Unity.Sdk` | Packs both `Astra.Unity.dll` builds into the `Astra.Unity.Sdk` NuGet package that integrations compile against. |
| `templates/` | The `dotnet new astra-integration` template. |
| `tests/Astra.Bridge.Core.Tests` | xUnit tests for `Astra.Bridge.Core` (plus two end-to-end tests that need an engine). |
| `tests/TestbedIntegration` | An integration made from the template, for the testbed. |
| `tools/` | `pack.sh` (build everything), `build-bundles.sh` (shader bundles), `check-game-members/` (metadata checks against a game), `make-runner-gi.sh` and `make-runner-il2cpp-gi.sh` (the release zips). |
| `unity/AstraShaders` | The composite shader (`Assets/Astra/AstraComposite.shader`), its bundle builder (`Assets/Editor/BuildBundles.cs`) and the committed bundles (`Build/`). |
| `unity/AstraTestbed`, `unity/com.astra.testbed` | The testbed: a tiny URP game built in code (Unity 6000.3.18f1). |
| `runner/` | The manifests of "Astra for any Unity game" (Mono, and `il2cpp/`). |
| `docs/` | [`PROTOCOL.md`](docs/PROTOCOL.md) (the bridge protocol), [`GAME-INTEGRATIONS.md`](docs/GAME-INTEGRATIONS.md) (the design), [`GAME-INTEGRATION-MANIFEST.md`](docs/GAME-INTEGRATION-MANIFEST.md) (`astra-gi.toml`), [`RENDER-CASES.md`](docs/RENDER-CASES.md) (where a game can lose her, and the open follow-ups), [`WRITE-AN-INTEGRATION.md`](docs/WRITE-AN-INTEGRATION.md). |
| `artifacts/` | Build output (git-ignored). |

Mono and IL2CPP share one code base. The runtime differences sit behind `#if IL2CPP`, mostly in the
`Compat*.cs` files. Code both plugins compile is C# 9 (`Directory.Build.props`): no records, no
`init`. An old Mono runtime lacks the types they need.

## Building and testing

You need the **.NET 8 SDK** (the tests and `check-game-members` target `net8.0`; the plugins
target `netstandard2.0` and `net6.0`). The scripts are bash; the bundle script also needs
`python3`, and the release zips need `curl`, `zip`, `unzip` and `sha256sum`. Packages come from
nuget.org and BepInEx's feed (`NuGet.Config`).

### The quick loop: no Unity, no game

```bash
dotnet build src/Astra.BepInEx5 -c Release        # the Mono foundation
dotnet test tests/Astra.Bridge.Core.Tests         # 70 tests; the 2 EngineTests are skipped
```

The two `EngineTests` run against a real engine when `ASTRA_ENGINE` names an
`astra-avatar-engine` binary built without its `daemon` feature. The engine Astra ships is
auth-gated and will not run them, so the maintainers do.

### Everything: `tools/pack.sh`

`tools/pack.sh` builds both foundations, the pipeline adapters, the SDK package and the template
package, and lays out the foundation as it is installed in a game:

```
artifacts/foundation-mono/BepInEx/plugins/Astra/     Astra.Unity.dll, Astra.Bridge.Core.dll, the adapters, astra-item.json
artifacts/foundation-il2cpp/BepInEx/plugins/Astra/   Astra.Unity.dll, Astra.Bridge.Core.dll, astra-item.json
artifacts/nuget/                                     Astra.Unity.Sdk.<version>.nupkg, Astra.Unity.Templates.<version>.nupkg
```

The adapters and the IL2CPP plugin compile against assemblies that have no NuGet package. You point
the build at folders that have them. These folders are only read; never commit them.

| for | set | point it at | without it |
|---|---|---|---|
| the URP 17 adapter | environment `UrpRefDir` | a Unity 6 URP game's `<Game>_Data/Managed`, or a Unity 6 URP project's `Library/ScriptAssemblies`. The default is the testbed's `unity/AstraTestbed/Library/ScriptAssemblies`, which exists once you open or build the testbed. | **the build fails** (a fresh clone has no testbed `Library/`) |
| the URP 14 adapter | `URP14_REF_DIR` | a Unity 2022.3 URP game's `<Game>_Data/Managed` | a warning; the foundation draws such games over the finished frame |
| the HDRP adapter | `HDRP_REF_DIR` | an HDRP game's `<Game>_Data/Managed`, or an HDRP project's `Library/ScriptAssemblies` | a warning; HDRP games get no depth test |
| the IL2CPP plugin | the first argument of `pack.sh` (or environment `Il2CppInteropDir`) | a `BepInEx/interop` folder that BepInEx 6 generated for an IL2CPP game. It must keep every Unity type the foundation names: Schedule I's works; MiSide's lacks `SphericalHarmonicsL2` and does not compile. The default is the testbed's `unity/AstraTestbed/Build/il2cpp/BepInEx/interop`, which exists once BepInEx 6 has started the testbed's IL2CPP build. | the build fails |

For example, with the games the maintainers use (Content Warning is Unity 6 URP, the EXTERMINATION
SHIP demo is Unity 2022.3 URP, Lethal Company is HDRP, and the interop is Schedule I's from Astra's
launcher profile):

```bash
S=~/.steam/steam/steamapps/common   # wherever your Steam library is
UrpRefDir="$S/Content Warning/Content Warning_Data/Managed" \
URP14_REF_DIR="$S/EXTERMINATION SHIP Demo/Extermination ship_Data/Managed" \
HDRP_REF_DIR="$S/Lethal Company/Lethal Company_Data/Managed" \
  tools/pack.sh ~/.local/share/astra/games/3164500/BepInEx/interop
```

Any game of the right kind works. You do not have to own these.

`tools/make-runner-gi.sh` and `tools/make-runner-il2cpp-gi.sh` lay out the release zips of "Astra
for any Unity game" (`artifacts/astra-gi.zip`, `artifacts/astra-gi-il2cpp.zip`). They download
BepInEx and check its digest. They call `tools/pack.sh` with no argument, so set `Il2CppInteropDir`
in the environment if you have no testbed build. The Mono runner refuses to build without
`HDRP_REF_DIR` and `URP14_REF_DIR`: a release must carry every adapter. Releases are the
maintainers' job; you rarely need these.

### The testbed

`unity/AstraTestbed` is a small URP game built in code: walls, a ramp, stairs, a player that walks,
runs and jumps, a camera that whips round. Open it with Unity 6000.3.18f1, or build it from the
command line:

```bash
Unity -batchmode -quit -projectPath unity/AstraTestbed -executeMethod TestbedBuild.Build
```

That writes a Linux Mono player to `Build/mono` and an IL2CPP one to `Build/il2cpp`
(`TestbedBuild.BuildMono` and `TestbedBuild.BuildIl2Cpp` build one each). Opening it also creates the
`Library/ScriptAssemblies` the URP 17 adapter compiles against. `tests/TestbedIntegration` builds
against `Build/mono` (see its `GameDir.props.user`).

## A shader bundle for another Unity line

Her composite shader is a Unity asset bundle, built per Unity line and platform, and embedded in the
plugins. Every file matching `unity/AstraShaders/Build/astra-*.bundle` is embedded at build time
(`src/Astra.Unity/Astra.Unity.props`). The name is `astra-<major>-<platform>.bundle`, where
`<major>` is the first number of the editor's version (`2021`, `2022`, `6000`).

At start, the foundation takes every bundle whose major is not newer than the game's, tries the
newest first, and falls back to the next older one when Unity refuses it
(`Compositor.Create` in `src/Astra.Unity/Compositor.cs`). A bundle loads in its own Unity line and
newer ones, never in older ones. Inside one line, a bundle from a newer minor version can be
refused by an older player (a 6000.3 bundle in a 6000.0 game); the foundation then uses the older
line's bundle.

The committed bundles and the editors that built them (each bundle's header names its editor:
`head -c 64 <bundle> | strings`):

| bundle | editor |
|---|---|
| `astra-2021-{windows,linux}.bundle` | 2021.3.35f1 |
| `astra-2022-{windows,linux}.bundle` | 2022.3.62f2 |
| `astra-6000-{windows,linux}.bundle` | 6000.3.18f1 |

**Wanted:** bundles for **2019** and **2020**, and a `6000` bundle from an older 6000.x editor (so
6000.0–6000.2 games get the newest line's bundle).

### Build one

You need the editor of that line with **Windows Build Support** and **Linux Build Support**
installed (the script builds a bundle for each platform). Use the oldest editor of the line you can
get: its bundle loads in more games.

On Linux:

```bash
tools/build-bundles.sh ~/Unity/Hub/Editor/<version>/Editor/Unity
```

The script copies the shader and the builder into a temporary project, keeps only Unity's built-in
modules in its package manifest (another line cannot compile this project's packages), and runs
`BuildBundles.Build` in batch mode. The new bundles land in `unity/AstraShaders/Build/`. If the
build fails, it prints the error lines; the temporary project and its `build.log` stay in `$TMPDIR` (or `/tmp`).
Old editors on new Linux distributions need help, and the script's header says how:
`UNITY_EXTRA_LIBS` for a missing library (2021.3 wants `libxml2.so.2`), `GLIBC_TUNABLES` for the
licensing client, and a `bee_backend` wrapper for 2021/2022 editors that hang after compiling.

By hand, on any OS: create an empty project in that editor, copy `unity/AstraShaders/Assets/Astra`
and `unity/AstraShaders/Assets/Editor` into its `Assets/`, choose **Astra → Build shader bundles** in
the menu, and copy `Build/astra-*.bundle` into `unity/AstraShaders/Build/`.

### Test it

1. Rebuild the foundation (`dotnet build src/Astra.BepInEx5 -c Release`, or `tools/pack.sh`). It
   embeds the new bundles.
2. Install it in a game of that Unity line (see [`README.md`](README.md#install)) and start the game
   with Astra running.
3. Read `BepInEx/LogOutput.log`. `Astra cannot be drawn in this game: no shader bundle …` means no
   bundle loaded; the line lists why each one was refused. Otherwise look for the `her:` line: it
   should say she is drawn.

### 2019 and 2020

A bundle is the first step there, not the whole job. The foundation compiles against Unity 2021.3's
API and is only tested on 2021.3 and newer. On older lines some calls it makes may not exist, and SRP
games before 2021.1 have no "end of context rendering" event to draw her at (an open follow-up in
`RENDER-CASES.md`). Build the bundle, try a game, and report what the log says.

### What to put in the pull request

- the bundles, and the exact editor version that built them (with its changeset if you have it);
- the games you tested, with their Unity versions and the log lines;
- no changes to the shader in the same pull request. A shader change is its own pull request, and
  it means rebuilding every line's bundles.

## IL2CPP games: the stripping audit

An IL2CPP game's build strips every engine type and member the game never uses. A plugin method
that names a stripped one fails the first time it runs (`TypeLoadException`,
`MissingMethodException`). If that method is on the start path, the plugin stops. Il2CppInterop
sometimes puts a member back without its body ("Method unstripping failed"); calling it throws too.

`check-game-members il2cpp` lists every reference of the IL2CPP plugin that a game's interop
lacks, and which plugin methods use it. It reads metadata only; nothing is loaded or run:

```bash
dotnet run --project tools/check-game-members -- il2cpp \
  src/Astra.BepInEx6.IL2CPP/bin/Release/net6.0/Astra.Unity.dll <profile>/BepInEx/interop
```

The interop folder is generated by BepInEx 6 the first time the game starts with it. With Astra's
launcher it is in the game's profile: `~/.local/share/astra/games/<steam appid>/BepInEx/interop` on
Linux, `%APPDATA%\astra\astra\data\games\<steam appid>\BepInEx\interop` on Windows. With a hand
install it is in the game's folder.

The tool exits with 0 only when nothing is missing. A list is normal. What matters is that every
`MISSING` row is either unreachable in that game (the SRP hooks in a Built-in game) or used only in
a method that is allowed to fail:

- the method is `[MethodImpl(MethodImplOptions.NoInlining)]`, so the failure happens when it is
  called, not when its caller is compiled;
- its caller catches the failure (`Compat.Stripped` recognises it), or the method runs inside a
  `Feature` (`src/Astra.Bridge.Core/Feature.cs`), which switches that feature off and lets the rest
  go on.

`src/Astra.Unity/Sh.cs` is the pattern: light probes, which MiSide's build removed. When you call a
Unity API the foundation did not use before, run the audit against at least one IL2CPP game and
fence what it reports. `check-game-members` has two more modes, `dump` and `check`, for integration
authors ([`docs/WRITE-AN-INTEGRATION.md`](docs/WRITE-AN-INTEGRATION.md#4-find-your-way-around-the-game)).

## Reporting a game that does not work

First: if the game has anti-cheat, stop. Do not install mods there.

Open an issue at <https://github.com/mihailinl/astra-bepinex/issues> with:

1. **The game**: its name and store page (or Steam app id).
2. **Its Unity version, pipeline and graphics API.** The foundation's first lines in the log say
   them, for example:
   `PEAK (Unity 6000.3.15f1, Direct3D12, UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset)`.
   `Built-in pipeline` there means no SRP.
3. **Mono or IL2CPP.** A game folder with `GameAssembly.dll` is IL2CPP; one with
   `<Game>_Data/Managed/Assembly-CSharp.dll` is Mono.
4. **Your system**: Windows, or Linux with Proton (and the Proton version).
5. **How you installed it** (Astra's Games tab, an integration, by hand, a mod manager) and the
   versions: the foundation's is in `BepInEx/plugins/Astra/astra-item.json`; also Astra's version and
   any other mods in the game.
6. **The whole `BepInEx/LogOutput.log`** (and `BepInEx/ErrorLog.log` if there is one). With Astra's
   launcher it is in the game's profile: `~/.local/share/astra/games/<steam appid>/BepInEx/` on
   Linux, `%APPDATA%\astra\astra\data\games\<steam appid>\BepInEx\` on Windows. With a hand install
   it is in the game's folder. If the game crashes before BepInEx starts, add Unity's `Player.log`
   (`%USERPROFILE%\AppData\LocalLow\<company>\<game>\Player.log` on Windows; under Proton, the same
   path inside `steamapps/compatdata/<appid>/pfx/drive_c/users/steamuser/`).
7. **What you see**: no Astra, Astra through walls, over particles, in the wrong place, too dark, a
   crash. A screenshot helps.

The **`her:` line** is the foundation's own diagnosis, written when something changes (checked
every 10 s):

```
her: on screen (0.36, 0.55), 7.9 m from 'Main Camera'; in 10 s drawn 2327 times from 2188 new pictures, her picture 6.2% covered
```

- `not placed yet`, `behind the camera` or `off screen (…)`: where she stands is the problem, not the
  drawing.
- `(no picture from Astra)`: the engine sends nothing. Is Astra running, and did the log say
  `connected to Astra on 127.0.0.1:<port>`?
- `(pictures arrive, none is drawn)`: the drawing is the problem (the pipeline, the camera).
- `(Astra's picture is empty)`: pictures arrive and are drawn, but none of her is in them.
- `(hidden: toggled off, or Astra not connected)`: the toggle key (`F8` by default) or the link.

Logs hold folder paths that may include your user name. Check before you post. Never attach game
files.

## Good first issues

The open follow-ups at the end of [`docs/RENDER-CASES.md`](docs/RENDER-CASES.md) are the list of
known work. Ones that fit a first contribution:

- **A shader bundle for Unity 2019 or 2020** ([above](#a-shader-bundle-for-another-unity-line)).
  Needs that editor.
- **`timeScale` 0** (DISCOVERY item 5): when the game is paused, send `speed` 0, and send a raw
  `time_scale` parameter so her animation set can decide what a pause means.
- **Temporal AA** (TEMPORAL AA item): use the camera's `nonJitteredProjectionMatrix` after temporal
  passes, so she does not shimmer in games with TAA or an upscaler.
- **Games without physics**: place her on the camera's ground plane when there are no colliders,
  and log it once.
- **A Rigidbody player** (DISCOVERY item 2): decide "grounded" by a ray plus the vertical speed,
  and make the vertical teleport relative to the player's height, applied only after a landing.
- **Shader items 3 and 4**: rays for orthographic cameras; a UV scale for pass 0 under dynamic
  resolution. Needs a Unity editor to rebuild the bundles.

Bigger items (the depth-only pass that puts particles in front of her, the runtime self-check, the
IL2CPP pipeline adapters through `ClassInjector`, the URP 17 depth attachment): open an issue first,
so we can agree on the approach before you spend days on it. The LAUNCHER items live in
[astra-game-launcher](https://github.com/mihailinl/astra-game-launcher), not here.

## Pull requests

Fork, branch, and open a pull request against `main`. Keep one topic per pull request.

Checklist:

- [ ] The change is generic: no game is named in the logic (rule 2).
- [ ] `dotnet build src/Astra.BepInEx5 -c Release` passes, and so does the IL2CPP plugin
      (`dotnet build src/Astra.BepInEx6.IL2CPP -c Release -p:Il2CppInteropDir=<interop>`) when you
      touched `src/Astra.Unity` or `src/Astra.Bridge.Core`.
- [ ] `dotnet test tests/Astra.Bridge.Core.Tests` passes. New behaviour in `Astra.Bridge.Core` comes
      with a test.
- [ ] New Unity API calls: the IL2CPP audit ran against at least one IL2CPP game, and every new
      `MISSING` row is fenced ([above](#il2cpp-games-the-stripping-audit)).
- [ ] Tested in at least one real game. The description names it, its Unity version, pipeline,
      Mono or IL2CPP, your OS, and quotes the log lines that show the change working.
- [ ] Public SDK changes are additive (no removal or rename within a major version), and the Mono
      and IL2CPP builds expose the same API ([`docs/GAME-INTEGRATIONS.md`](docs/GAME-INTEGRATIONS.md) §2).
- [ ] A new setting is in the README's settings table; a closed follow-up is marked in
      `docs/RENDER-CASES.md`.
- [ ] No game files, no interop, no built DLLs. Bundles only with the editor version (rule 4).
- [ ] No version bumps. The maintainers set versions at release (`Directory.Build.props`,
      `runner/*/astra-gi.toml`).

Code style: four spaces, lines up to about 120 columns, comments that say WHY. Code both plugins
compile stays C# 9. Do not reformat files you did not change.
