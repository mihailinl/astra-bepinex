# Astra for Unity games (BepInEx)

Puts **Astra**, the desktop companion of the Astra assistant, *into* Unity games: she
walks beside you in the game's world, lit by its sun, hidden by its walls, while the Astra app
gives her voice, face and moods. This repository is the open-source game side: a
[BepInEx](https://github.com/BepInEx/BepInEx) plugin for **Mono** games (BepInEx 5) and **IL2CPP**
games (BepInEx 6), built from one code base.

> **Single-player and co-op games without anti-cheat only.** Never install BepInEx into a game
> with kernel or server-side anti-cheat (EasyAntiCheat, BattlEye, Vanguard, HoYoverse's…): mods
> get accounts banned. Other players never see her — she is drawn only on your screen.

## Supported

- **Unity 2021.3 and newer** (2021.3 LTS, 2022, 6000) — the **Built-in**, **URP** and **HDRP**
  render pipelines.
- **Windows**, and **Linux through Proton** (see below).
- **BepInEx 5, Mono** — the tested, supported path.
- **BepInEx 6, IL2CPP, is experimental**: it builds and runs on the testbed, but has not yet been
  run against a real IL2CPP game — use it and tell us what you find.
- She works in **any** Unity game with no integration at all: the default follower keeps her near
  the nearest object tagged `Player` (else the camera) through the game's own colliders, lit by
  its sun. A **game integration** only REFINES who the player really is, the camera, her size and
  raw facts for her animation set — see below.
- macOS is not supported.

## How it works

Astra's engine draws her; the game does not need to know how. The plugin:

1. **sends the game's camera** each frame, and **where she stands** — she follows you through the
   game's own colliders (Unity's physics is the same in every Unity game), plus raw facts about her
   motion (`speed`, `airborne`) that her animation set turns into walking, running and jumping;
2. **sends the game's light** — its sun, how much of it reaches her, the sky colour;
3. **composites her picture** over the game's finished frame, with a depth test against the game's
   own depth buffer both ways, reprojected to the camera of the moment so a fast turn does not
   ghost.

Everything goes over the documented **game-bridge protocol** ([`docs/PROTOCOL.md`](docs/PROTOCOL.md)):
a WebSocket on `127.0.0.1` and a shared-memory frame ring. Nothing here is Unity-specific in the
protocol — `src/Astra.Bridge.Core` is a plain .NET Standard 2.0 library any .NET mod can use.

## Install

You need the **Astra app** running (she switches from your desktop into the game by herself when
the game connects, and back when it closes).

### Mono games (most Unity games: `<Game>_Data/Managed/Assembly-CSharp.dll` exists)

1. Install **BepInEx 5** (x64): with a mod manager (r2modman, Gale, Thunderstore — install
   "BepInExPack"), or by hand from the [releases](https://github.com/BepInEx/BepInEx/releases)
   (`BepInEx_win_x64_5.4.23.x.zip` unzipped into the game's folder).
2. Copy the foundation (`artifacts/foundation-mono/BepInEx/…` from `tools/pack.sh`) into the game:
   `BepInEx/plugins/Astra/` holds `Astra.Unity.dll`, `Astra.Bridge.Core.dll`, `Astra.Urp17.dll` and
   `astra-item.json`.

### IL2CPP games (`GameAssembly.dll` and `<Game>_Data/il2cpp_data` exist)

1. Install **BepInEx 6** for IL2CPP (`BepInEx-Unity.IL2CPP-win-x64-6.0.0-…`). Start the game once
   and let BepInEx generate its `interop` folder.
2. Copy the IL2CPP foundation (`artifacts/foundation-il2cpp/BepInEx/…`) into the game:
   `BepInEx/plugins/Astra/` holds `Astra.Unity.dll`, `Astra.Bridge.Core.dll` and `astra-item.json`.

### Linux: a Windows game under Proton

BepInEx loads through `winhttp.dll`, which Proton replaces with its own unless told otherwise: in
Steam → the game → Properties → Launch options, put

```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```

The plugin maps Astra's frames through Wine's `Z:` drive automatically.

## Settings

`BepInEx/config/astra.unity-foundation.cfg`, created on the first run. A game's integration may
change the DEFAULTS; a value you change here always wins:

| section | key | default | |
|---|---|---|---|
| General | `Enabled` | `true` | show her |
| General | `Toggle` | `F8` | show/hide key (games with the legacy input manager) |
| Astra | `Scale` | `1` | her size in this game's world |
| Follow | `PlayerObject` | *(auto)* | the GameObject she follows; auto = the game's integration, else the nearest object tagged `Player`, else the camera |
| Follow | `KeepDistance` / `FollowStart` / `TeleportDistance` / `MaxSpeed` | 1.6 / 3.5 / 30 / 7 | how she follows |
| Follow | `GroundLayers` | *(all)* | layers she walks on and bumps into |
| Picture | `DepthBias` / `DepthSoftness` | 0.03 / 0.02 | the depth test, in metres |
| Picture | `ColourSpace` | `auto` | `linear`/`gamma` if she looks too dark or washed out |
| Picture | `MaxPictureHeight` | 720 | her picture's resolution (lower until the engine crops frames to her rectangle) |
| Picture | `SendLight` | `true` | light her with the game's sun |
| Connection | `Port` / `Token` / `RingPath` | 25600 / — / auto | only if you changed Astra's |

## A game where she should do better → an integration

The foundation alone makes Astra work in any Unity game. A **game integration** makes her better in
ONE game: who the player really is, where she stands and how she gets there, her size in that world,
raw facts for her animations. It is a small separate BepInEx plugin, usually in its own repository
(the first one is `astra-peak`), and it builds on the foundation's SDK:

```bash
tools/pack.sh                                   # until the packages are on nuget.org: artifacts/nuget
dotnet new install artifacts/nuget/Astra.Unity.Templates.0.2.0.nupkg
dotnet new astra-integration -n AstraMyGame --game "My Game" --id astra.mygame --runtime mono
```

(The template's project restores `Astra.Unity.Sdk` from nuget.org; until it is published there, add
`artifacts/nuget` as a package source, e.g. in a `GameDir.props.user`:
`<RestoreAdditionalProjectSources>…/astra-bepinex/artifacts/nuget</RestoreAdditionalProjectSources>`.)

```csharp
var astra = AstraSdk.Register("astra.mygame", "My Game");
astra.Defaults.MatchPlayerHeight = 0.95f;           // as tall as 95% of the player
astra.UsePlayer(cam => new PlayerInfo { Feet = …, Forward = …, Root = …, Height = … });
astra.UseBrain(new MyBrain());                       // where she stands; call f.Default for ordinary frames
astra.OnFrame(f => f.Params.Set("swimming", …));     // RAW FACTS for her animation set
astra.Cues.Play("wave");                             // animation by name, on a game event
```

The integration references `Astra.Unity.Sdk` **compile-only** and never ships the foundation: the
foundation is installed once per game and updates by itself, so every integration gets its updates
without a rebuild. The full design is [`docs/GAME-INTEGRATIONS.md`](docs/GAME-INTEGRATIONS.md);
`tests/TestbedIntegration` is a working example on the testbed.

## Build

```bash
tools/pack.sh [<an IL2CPP game>/BepInEx/interop]   # both foundations, the SDK and template packages → artifacts/
dotnet test tests/Astra.Bridge.Core.Tests          # + ASTRA_ENGINE=<engine binary> for end-to-end
```

The composite shader is a Unity asset bundle, built per Unity line and platform and embedded in the
plugins. To rebuild it (Unity 6000.3 for Unity 6 games):

```bash
Unity -batchmode -quit -projectPath unity/AstraShaders -executeMethod BuildBundles.Build
```

A bundle loads in its own Unity line and newer ones, never older: bundles for older lines
(2019–2022) are welcome as contributions — build the same project with that editor.

`unity/AstraTestbed` is a tiny URP game built in code (walls, a ramp, stairs, a player that walks,
runs and jumps, a camera that whips round) to test both plugins without a real game — as a Mono and
an IL2CPP Linux player (`TestbedBuild.Build`).

## License

MIT — see [LICENSE](LICENSE). The Astra app and engine are separate products.
