# Write a game integration

This is a step-by-step guide to a **game integration** for Astra: a small BepInEx plugin, in its own
repository, that makes Astra better in ONE Unity game. It is built on this repository's foundation
and its SDK (`Astra.Unity.Sdk`). At the end you have a GitHub repository whose releases carry an
`astra-gi.zip`, and players install it by pasting your repository's address into Astra's **Games**
tab.

Four integrations are published and are good examples:
[astra-peak](https://github.com/mihailinl/astra-peak) (PEAK: climbing, its own brain),
[astra-lethal](https://github.com/mihailinl/astra-lethal) (Lethal Company: the game's camera, a
darker light, many raw facts),
[astra-content-warning](https://github.com/mihailinl/astra-content-warning) (Content Warning: her in
the in-game video camera and in mirrors) and
[astra-repo](https://github.com/mihailinl/astra-repo) (R.E.P.O.).

**You need:** the .NET 8 SDK, the game, the Astra app, a GitHub account, and git. A .NET decompiler
(ILSpy, dnSpyEx) helps you read the game's code on your machine.

The design behind all this is [`GAME-INTEGRATIONS.md`](GAME-INTEGRATIONS.md); the install contract
is [`GAME-INTEGRATION-MANIFEST.md`](GAME-INTEGRATION-MANIFEST.md); the wire protocol is
[`PROTOCOL.md`](PROTOCOL.md). You do not need to read them to follow this guide.

## 1. Do you need one?

Often not. The foundation alone makes Astra work in any Unity game. She follows the nearest object
tagged `Player` (else the camera) through the game's own colliders. She is lit by its sun and
hidden by its walls. Players can tune her per game in `BepInEx/config/astra.unity-foundation.cfg`
(`Scale`, `PlayerObject`, `GroundLayers`, the follow distances; the table is in the
[README](../README.md#settings)). Try that first.

Write an integration when the game needs knowledge only code can have:

| problem | the SDK call |
|---|---|
| In multiplayer she follows someone else, not you. | `UsePlayer`: return YOUR player. |
| The camera she is drawn into is wrong (an untagged camera, a spectator or vehicle camera). | `UseCamera` |
| She is too small or too big for this world. | `Defaults.Scale` or `Defaults.MatchPlayerHeight` |
| Where she stands needs the game's rules: you climb, swim, drive. | `UseBrain` |
| The game has states her animations could react to (crouching, underwater, afraid). | `OnFrame` with `f.Params` |
| A game event deserves a gesture. | `Cues` |
| She should show up in mirrors or in-game cameras. | `AlsoDrawInto` (URP games only, for now) |
| The game's light is unusual (its own day cycle, darker than most). | `UseSun`, `UseLook` |

An integration is the wrong place for rendering problems that a whole kind of game shares (walls,
particles, a pipeline). Those are foundation fixes: see [`CONTRIBUTING.md`](../CONTRIBUTING.md).

## 2. Know your game

- **Mono or IL2CPP?** A game folder with `<Game>_Data/Managed/Assembly-CSharp.dll` is Mono:
  BepInEx 5, the path every published integration takes. A folder with `GameAssembly.dll` is
  IL2CPP: BepInEx 6, experimental (see [IL2CPP integrations](#11-il2cpp-integrations)).
- **Single-player or co-op without anti-cheat only.** Never mod a game with kernel or server-side
  anti-cheat: players get banned.
- **Run it with the foundation alone first.** In Astra's Games tab, add the game with "Astra for any
  Unity game" and press Play. Watch what is wrong; that is your to-do list. The log's first
  foundation lines name the Unity version, the graphics API and the render pipeline (where the log
  is: [step 7](#7-test-it-in-the-game)).

## 3. Create the project

The template comes from this repository:

```bash
git clone https://github.com/mihailinl/astra-bepinex
dotnet new install ./astra-bepinex/templates/astra-integration
dotnet new astra-integration -n AstraMyGame --game "My Game" --id astra.mygame --runtime mono --steamAppId 1234560
cd AstraMyGame          # your new repository: git init here
```

| option | meaning |
|---|---|
| `-n` | the project, assembly and folder name |
| `--game` | the game's name as players know it (required) |
| `--id` | your integration's id: its BepInEx GUID, and the id in `astra-gi.toml` (`astra.<game>`) |
| `--runtime` | `mono` (BepInEx 5) or `il2cpp` (BepInEx 6) |
| `--steamAppId` | the game's Steam app id (its store page address has it) |
| `--gameDir` | the game's folder; leave it out (see below) |

You get `AstraMyGame.csproj`, `Plugin.cs`, `astra-item.toml` (a proposed marketplace manifest that
nothing reads yet), `README.md` and `.gitignore`. The template does not create `astra-gi.toml` or a
release workflow yet; steps 8 and 9 add them.

### The SDK package

`Astra.Unity.Sdk` is not on nuget.org yet. Every foundation release on GitHub carries it. Put it in
a `nuget-local` folder inside your project folder:

```bash
mkdir nuget-local      # in AstraMyGame/
gh release download v0.6.1 -R mihailinl/astra-bepinex -p 'Astra.Unity.Sdk.*.nupkg' -D nuget-local
# without gh: curl -L -o nuget-local/Astra.Unity.Sdk.0.6.1.nupkg \
#   https://github.com/mihailinl/astra-bepinex/releases/download/v0.6.1/Astra.Unity.Sdk.0.6.1.nupkg
```

Add a `nuget.config` next to your `.csproj` (a relative path in it is relative to the file):

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="BepInEx" value="https://nuget.bepinex.dev/v3/index.json" />
    <add key="astra-local" value="nuget-local" />
  </packageSources>
</configuration>
```

Add `nuget-local/` to your `.gitignore` (and, for step 8, `bepinex-dist/`,
`BepInEx_win_x64_5.4.23.5.zip`, `foundation/`, `foundation-mono/` and `astra-gi.zip`). Then change two things in the `.csproj`:

1. **Pin the SDK** to the version you downloaded. The template says `Version="[0.3,1)"`, and NuGet
   takes the LOWEST version in a range that it can find: an old SDK in your NuGet cache wins, and it
   may lack API this guide uses (`GameType` came after 0.3). Write the version you build against:

   ```xml
   <PackageReference Include="Astra.Unity.Sdk" Version="[0.6.1,1)" ExcludeAssets="runtime" PrivateAssets="all" />
   ```

   The SDK version also sets the oldest foundation your plugin loads with: `AstraSdk.Dependency` in
   your `[BepInDependency]` starts at that version. Keep `ExcludeAssets="runtime"`: your plugin never ships
   the foundation's DLLs.
2. **Lock the packages**, so your CI restores exactly what you tested: add
   `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` to the first `PropertyGroup` and
   commit the `packages.lock.json` that the next restore writes.

Build:

```bash
dotnet build -c Release      # → bin/Release/netstandard2.1/AstraMyGame.dll and astra-item.json
```

**Leave `GameDir` empty.** Then the project compiles against Unity's public API
(`UnityEngine.Modules`) and nothing of the game, and you reach the game's own types by name through
`GameType` (step 5). That is what lets a CI machine, which has no copy of the game, build your
releases. With `GameDir` set, the template also references every DLL in the game's
`<Game>_Data/Managed`; that is handy for a quick experiment, but such code cannot be built in CI.
The target is `netstandard2.1` (Unity 2021.2 and newer); for an older game use `netstandard2.0`.

## 4. Find your way around the game

Read the game's code on your machine with a decompiler: open
`<Game>_Data/Managed/Assembly-CSharp.dll`. Look for the local player (often a static: PEAK has
`Character.localCharacter`), the camera the player looks through, flags such as grounded,
crouching or swimming, and the layer mask the game walks on. What you read is for your eyes only:
never commit or post decompiled code.

This repository's `check-game-members` tool reads a game's metadata without running anything.
`dump` lists a type's fields and properties with their types:

```bash
dotnet run --project ../astra-bepinex/tools/check-game-members -- dump "<game>/<Game>_Data/Managed" CharacterData
```

`check` verifies every lookup your integration makes. Write them in a `members.tsv`, one per line
(`Type Member Kind ExpectedType`; kinds are `type`, `static`, `instance` and `method0`; `object`
accepts any reference type):

```
# Type          Member          Kind      ExpectedType
Character       -               type      -
Character       localCharacter  static    object
Character       data            instance  object
CharacterData   isGrounded      instance  Boolean
CharacterData   groundPos       instance  Vector3
```

```bash
dotnet run --project ../astra-bepinex/tools/check-game-members -- check "<game>/<Game>_Data/Managed" members.tsv
```

It prints a `PASS` or `FAIL` line per row and exits with 1 if any row fails. Run it again after every
game update: a renamed field does not crash your plugin (it reads as its default), it just quietly
stops working.

## 5. The SDK

Everything is in the `Astra.Sdk` namespace, and every call happens on Unity's main thread. Every
extension point has a default that already works: replace only what you know better.

### Register

```csharp
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]   // after the foundation; refuses an older one
public sealed class Plugin : BaseUnityPlugin
{
    void Awake()
    {
        var astra = AstraSdk.Register(MyPluginInfo.PLUGIN_GUID, "My Game");
        // … the calls below
    }
}
```

- `AstraSdk.Register(id, game)` returns your `GameIntegration`. One per game: a second registration
  replaces the first. Use the same id everywhere (BepInEx GUID, `astra-gi.toml`): the engine knows
  your integration by it.
- Name `AstraSdk.Dependency` in the attribute, never `AstraSdk.Version`: BepInEx 6 reads a plain
  version as an exact one, and every foundation update would unload your plugin.
- `AstraSdk.Connected` says whether Astra's engine is connected.

### Reach the game: `GameType`

```csharp
static readonly GameType PlayerType = GameType.Find("PlayerController");     // by short name
static readonly Member<bool> InWater = PlayerType.Member<bool>("inWater");    // a cached accessor

object local = PlayerType.Static<object>("Local");   // a static field or property, read now
bool swimming = InWater.Get(local);                  // false if anything is missing
```

- `GameType.Find(name, assembly = "Assembly-CSharp")` looks in the game's code first, then every
  other loaded assembly. `Exists` and `ClrType` tell you what it found.
- `Static<T>(name)` reads a static member now. `Member<T>(name)` returns an accessor; keep it in a
  `static readonly` field and call `Get(instance, fallback)` every frame.
- It never throws. A missing type or member reads as the default (or your `fallback`) and is listed
  once in `GameType.Missing`: log that list once, after your lookups have run.
- Fields and properties read the same way, so the same code reads a Mono game and an IL2CPP game
  (where Il2CppInterop turns fields into properties).
- Lookups are cached, misses too.
- It only reads. An integration observes the game; it does not change it.

### Who she follows: `UsePlayer`

`UsePlayer(Func<Camera, PlayerInfo?>)` is called every frame. Return `null` when there is no player
(a menu, a loading screen, the player is dead): she stays where she is.

| `PlayerInfo` field | meaning |
|---|---|
| `Feet` | the ground point under the player: the point she keeps near |
| `Forward` | where the player faces or moves, horizontal (zero: the camera's) |
| `Root` | the player's root object: its colliders never count as ground or walls for her |
| `Grounded` | false while the player jumps, climbs, flies |
| `Height` | the player's STANDING height in game units; 0 when not standing (crouching, climbing) or unknown |

### The camera: `UseCamera`

`UseCamera(Func<Camera>)` returns the camera she is composited into, every frame (`null`: none
now, she is not drawn). The default is `Camera.main`, which many games do not tag, or switch away
from.

### Her size and how she follows: `Defaults`

`astra.Defaults` (`IntegrationDefaults`) sets this game's defaults. A value the player changed in
`astra.unity-foundation.cfg` always wins. You can change a default at any time (Lethal Company sets
`GroundMask` once the round exists).

| field | meaning |
|---|---|
| `Scale` | her size in this world (1 = her own height in metres) |
| `MatchPlayerHeight` | her height as a fraction of `PlayerInfo.Height`; wins over `Scale` when the height is known |
| `KeepDistance`, `FollowStart`, `TeleportDistance`, `MaxSpeed` | how she follows |
| `GroundLayers` | comma-separated layer names she walks on and bumps into |
| `GroundMask` | the same as a layer mask; wins over `GroundLayers` |
| `DepthBias`, `DepthSoftness` | the depth test, in metres |

She takes up a new player height only after it has held for a second, but report 0 whenever the
player is not standing anyway.

### Where she stands: `UseBrain`

A brain is an `IBrain` with one method, `Step(FrameContext f)`, called every frame. It writes
`f.Her`. For the frames it has nothing better to do, it calls the stock follower: `f.Default.Step(f)`.

| `FrameContext` | |
|---|---|
| `DeltaTime`, `Camera`, `Player` | this frame (`Player` is null when there is none) |
| `Her` | where she is; the brain writes it |
| `Params`, `Cues` | raw facts and cues (below) |
| `Default` | the stock follower |
| `Follow` | the follow settings in effect (`KeepDistance`, `FollowStart`, `TeleportDistance`, `MaxSpeed`) |
| `GroundMask` | the layers she walks on |
| `Raycast`, `CapsuleCast`, `GroundBelow` | physics queries that skip the player's own colliders and triggers |
| `TrySpotNear(feet, forward, distance, out spot)` | a place to stand about `distance` from the feet: behind first, then the sides, then in front; false when there is none |
| `Hold(lookAt)` | stay where she is under gravity, turned toward `lookAt` |

| `Her` | |
|---|---|
| `Position` | her feet (or the point `Anchor` names), in the game's world |
| `Facing` | horizontal for an upright stance |
| `Up` | optional tilt (a seat in a vehicle); null keeps her upright |
| `Anchor` | which of her points `Position` names: a VRM humanoid bone such as `"hips"`, or `"feet"` (null) |
| `Airborne` | off the ground: a raw fact for her animations |
| `Placed` | has she been placed yet? Nothing is drawn until she is |

A seat in a vehicle is `Her.Anchor = "hips"`, `Her.Up` set to the seat's up, and
`Cues.Play("seat")`. PEAK's `MountainBrain` is a full example: she waits under a wall while you
climb and leaps up after you.

### Animation by name: `Cues`

`astra.Cues` and `f.Cues` send a cue with the next frame:

- `Play(state)`: enter a state of her animation set and hold it until the set leaves it;
- `Trigger(name)`: raise a token into her animation graph;
- `Resume()`: hand her back to her set's default state.

Use cues for EVENTS ("the player died", "a song started"); use raw facts (below) for anything that
lasts. A name her set does not have is ignored. `Play` and `Trigger` take an optional
`expectedSet`: the engine declines the cue if she wears another set. The built-in set has, for
example, `seat` and `dance_slow` / `dance_mid` / `dance_fast` ([`PROTOCOL.md`](PROTOCOL.md) lists
them).

### More cameras: `AlsoDrawInto`

`AlsoDrawInto(Func<Camera, bool>)` is asked about every camera that renders (keep it cheap) and says
whether she belongs in it: an in-game video camera, a mirror, a security monitor. She is the main
camera's picture reprojected into that view, so a camera looking roughly where the player looks shows
her right. URP games only, for now. Content Warning's integration uses it for the video camera and
mirrors.

### Light: `UseSun` and `UseLook`

- `UseSun(Func<Light>)`: the light that is this game's sun (null: no sun now). The default is the
  scene's sun, else its brightest directional light.
- `UseLook(floor, ceiling = 1.40f)`: the band her light is held in. Lower the floor for a game whose
  dark is darker than most (Lethal Company uses `UseLook(floor: 0.10f)`). The engine bounds the floor
  to 0.05–0.40 and the ceiling to 0.8–2.0. Set it only after looking at her in the game.

### When your code throws

If one of your callbacks throws, or returns a player or a position that is not a finite number, the
foundation logs `the integration '<id>' failed (…) and is switched off; Astra goes on with her
defaults` and uses its defaults until a new `Register`. She keeps working; your integration does not.
`GameType` never throws, so most of your code cannot.

### A whole example

This compiles against SDK 0.6.1 with `GameDir` empty. The game's names (`PlayerController`,
`Local`, `inWater`…) are made up; use the ones step 4 found.

```csharp
using Astra.Sdk;
using BepInEx;
using UnityEngine;

namespace AstraMyGame
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        void Awake()
        {
            var astra = AstraSdk.Register(MyPluginInfo.PLUGIN_GUID, "My Game");

            // Defaults: the player's own values in astra.unity-foundation.cfg still win.
            astra.Defaults.MatchPlayerHeight = 0.95f;   // 95% of the player's standing height
            astra.Defaults.TeleportDistance = 25f;

            astra.UsePlayer(_ => MyPlayer.Locate());
            astra.UseCamera(() => MyPlayer.Camera);
            astra.UseBrain(new ShoreBrain());

            bool wasDead = false;
            astra.OnFrame(f =>
            {
                f.Params.Set("swimming", MyPlayer.Swimming)
                        .Set("health", MyPlayer.Health);
                bool dead = MyPlayer.Dead;
                if (dead && !wasDead) f.Cues.Trigger("player_died");
                wasDead = dead;
            });

            Logger.LogInfo("Astra for My Game is ready");
        }
    }

    /// <summary>The game's local player, reached by NAME: no reference to the game's assemblies.</summary>
    static class MyPlayer
    {
        static readonly GameType PlayerType = GameType.Find("PlayerController");
        static readonly Member<bool> IsGrounded = PlayerType.Member<bool>("isGrounded");
        static readonly Member<bool> InWater = PlayerType.Member<bool>("inWater");
        static readonly Member<float> Hp = PlayerType.Member<float>("health");
        static readonly Member<Camera> Cam = PlayerType.Member<Camera>("playerCamera");

        static object Local => PlayerType.Static<object>("Local");

        public static PlayerInfo? Locate()
        {
            var p = Local as Component;
            if (p == null) return null;                // a menu, a loading screen: she waits
            return new PlayerInfo
            {
                Feet = p.transform.position,           // this game's pivot is at the feet
                Forward = p.transform.forward,
                Root = p.gameObject,
                Grounded = IsGrounded.Get(p),
                Height = 1.8f,                         // or measure it; 0 = "not standing now"
            };
        }

        public static Camera Camera => Cam.Get(Local);
        public static bool Swimming => InWater.Get(Local);
        public static float Health => Hp.Get(Local);
        public static bool Dead => Local != null && Health <= 0;
    }

    /// <summary>She waits on the shore while you swim; every other frame is the stock follower.</summary>
    sealed class ShoreBrain : IBrain
    {
        public void Step(FrameContext f)
        {
            if (f.Player is PlayerInfo p && MyPlayer.Swimming && f.Her.Placed)
            {
                f.Hold(p.Feet);       // stay put under gravity, turned toward you
                return;
            }
            f.Default.Step(f);        // walk beside you, through the game's colliders
        }
    }
}
```

## 6. Raw facts for her animations

`astra.OnFrame(Action<FrameContext>)` runs every frame, before the brain. Set what is TRUE now in
`f.Params`: `Set(name, bool)`, `Set(name, double)`, or `Unset(name)` to hand a parameter back to her
set's default. `Params` is cleared every frame, so set every fact every frame.

- **Facts, not decisions.** Send `climbing = true`, never `animation = "climb"`. Her animation set
  decides what a fact looks like, and players and pack authors can change that without your code. A
  pack author can build swimming or flying from your facts.
- **Names** are identifiers of at most 32 characters. The published integrations use snake_case:
  `climbing` (PEAK); `fear`, `inside`, `in_ship`, `crouching`, `ladder`, `underwater` (Lethal
  Company); `crouching`, `sprinting`, `in_bell`, `upside_down` (Content Warning).
- **`speed` and `airborne` are measured by the foundation** from her own motion and `Her.Airborne`.
  Do not send them.
- **Send facts no pack uses yet.** The engine remembers a parameter her set does not declare; when a
  pack that declares it becomes active, it starts from your last value. When the game lets her go,
  every parameter returns to its default.
- **List your parameters in your README**: name, type, meaning, units. That is what pack authors
  read.

**Declaring your parameters (coming; engine side done, foundation pending).** The engine already
accepts the list of parameters a game sends, in its `hello` message (`params`, each
`{"name": …, "type": "float" | "bool"}`), and keeps it per integration id, so Astra's Studio can
show a player what your game sends before it ever ran and offer to make her react to it. The
foundation does not send that list yet, and the SDK has no call for it. Until it does, your README
is the declaration.

## 7. Test it in the game

Two ways to get your build into the game. Both need the Astra app running: she switches from the
desktop into the game when it connects.

**With Astra's launcher.** Add the game once in Astra's Games tab with "Astra for any Unity game".
That creates the game's profile: `~/.local/share/astra/games/<steam appid>/` on Linux,
`%APPDATA%\astra\astra\data\games\<steam appid>\` on Windows. Copy your `AstraMyGame.dll` and
`astra-item.json` into `<profile>/BepInEx/plugins/AstraMyGame/`, then press Play in Astra. The
launcher checks that its own files are there and leaves yours alone; remove your folder yourself
when you are done.

**By hand.** Install BepInEx 5 and the foundation into the game folder as the
[README](../README.md#install) says (the foundation's `BepInEx/plugins/Astra/` is inside the
`astra-gi.zip` of every [foundation release](https://github.com/mihailinl/astra-bepinex/releases)),
and copy your files into `<game>/BepInEx/plugins/AstraMyGame/`. On Linux, set the Proton launch
option from the README.

Then read `BepInEx/LogOutput.log` (in the profile, or in the game folder for a hand install):

- your own `Logger.LogInfo` line: your plugin loaded;
- `integration 'astra.mygame' for My Game is in charge`: the foundation took your registration;
- `connected to Astra on 127.0.0.1:25600`: the engine is there;
- `her: on screen (…), 3.1 m from '<camera>'; in 10 s drawn … times`: she is placed and drawn
  ([`CONTRIBUTING.md`](../CONTRIBUTING.md#reporting-a-game-that-does-not-work) explains the line);
- `the integration 'astra.mygame' failed (…) and is switched off`: your code threw; the exception
  follows;
- a BepInEx error about a missing dependency: the foundation in the game is older than the SDK you
  compiled against.

To iterate: change the code, `dotnet build -c Release`, copy the DLL, restart the game (BepInEx loads
plugins once, at start). Try values in `astra.unity-foundation.cfg` first, then make the good ones
your `Defaults`; while you judge your defaults, keep those keys at the foundation's defaults in the
cfg, because a changed value there wins. `F8` hides and shows her. In a multiplayer game, test with a
second player: following YOUR player is the point.

## 8. Package it: `astra-gi.toml` and the zip

Astra installs one file from your release: `astra-gi.zip`, with an `astra-gi.toml` manifest at its
root. The manifest is data only: which game, which files go where, how to launch.
[`GAME-INTEGRATION-MANIFEST.md`](GAME-INTEGRATION-MANIFEST.md) is the full contract.

The easiest start is astra-peak's files (MIT): copy `astra-gi.toml`, `doorstop_config.ini`,
`licenses/` and `tools/make-astra-gi.sh` into your repository.

In `astra-gi.toml`, change:

- `[integration]`: `id` (your `--id`), `name`, `version`, `authors`, `license`, `repo`;
- `[target]`: `steam_appid`, `exe` (relative to the game folder), `platforms`. Keep
  `anti_cheat = "none"`: Astra refuses anything else.

Keep `[[files]]`, `[[game_files]]`, `[launch]` and `[[config_writes]]` as they are. The
`winhttp.dll` digest is the one of BepInEx 5.4.23.5's proxy; change it only if you bundle another
BepInEx.

In `tools/make-astra-gi.sh`, change `ASSEMBLY_NAME` and `PLUGIN_FOLDER` (your assembly name), `TFM`
if yours is not `netstandard2.1`, and the name of your licence file (`astra-peak-LICENSE.txt`). The
script needs three inputs:

```bash
dotnet build -c Release                                       # your plugin
curl -LO https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip
unzip BepInEx_win_x64_5.4.23.5.zip -d bepinex-dist             # BepInEx 5 (BEPINEX_DIST)
gh release download v0.6.1 -R mihailinl/astra-bepinex -p astra-gi.zip -D foundation
unzip foundation/astra-gi.zip -d foundation-mono                 # holds BepInEx/plugins/Astra/
FOUNDATION_DIR=foundation-mono bash tools/make-astra-gi.sh
```

The zip it writes holds `astra-gi.toml`, BepInEx 5's `core`, the foundation in
`BepInEx/plugins/Astra/` exactly as released, your plugin in `BepInEx/plugins/AstraMyGame/`,
`winhttp.dll` and `doorstop_config.ini` (shipped with Doorstop off, so Steam's own Play stays
vanilla), and the licences of everything bundled.

The foundation travels in your zip unchanged. Never copy its code into your plugin and never
modify its DLLs. Bundle a foundation at least as new as the SDK you compiled against.

Check the zip with the launcher Astra uses (it needs Rust). `inspect` reads and validates the
manifest and the zip, and installs nothing:

```bash
git clone https://github.com/mihailinl/astra-game-launcher ../astra-game-launcher
cargo run --release --manifest-path ../astra-game-launcher/Cargo.toml --bin astra-game-launcher -- \
  inspect astra-gi.zip --sha256 $(sha256sum astra-gi.zip | cut -d' ' -f1)
```

## 9. Release it on GitHub

The published integrations build their zip in GitHub Actions from public inputs only: the SDK
package, BepInEx's NuGet packages, `UnityEngine.Modules`, BepInEx's release and the foundation's
release, each pinned by its SHA-256. Copy astra-peak's `.github/workflows/release.yml`. On a pushed
tag `v*` it:

1. downloads the SDK package from the foundation's release and checks its digest;
2. restores in locked mode (your `packages.lock.json`) and builds with warnings as errors;
3. downloads BepInEx 5.4.23.5 and the foundation's `astra-gi.zip`, checking both digests;
4. runs `tools/make-astra-gi.sh` and attaches `astra-gi.zip` to the tag's release.

In the workflow, change `ASSEMBLY_NAME`, `PLUGIN_FOLDER`, `TFM`, the two SDK lines
(`ASTRA_SDK_URL`, `ASTRA_SDK_SHA256`, and the file name the download step writes) and the two
foundation lines (`FOUNDATION_ASSET_URL`, `FOUNDATION_ASSET_SHA256`). Take each digest with
`sha256sum` of the file you downloaded. The SDK file must be the one your `packages.lock.json`
pinned. The workflow downloads the SDK into `nuget-local/` at the repository root and then adds that
folder as a package source; your `nuget.config` already names it, so **delete the step "Add the
local SDK feed"** (`dotnet nuget add source` fails on a name that is already there). This assumes
your `.csproj` and `nuget.config` sit at the repository root, as astra-peak's do.

To release:

1. Bump the version in three places: `<Version>` in the `.csproj`, `[integration].version` in
   `astra-gi.toml`, and `version` in `astra-item.toml`.
2. Commit, tag `vX.Y.Z`, push the tag. The workflow attaches `astra-gi.zip` to the release.
3. Keep it a normal release (the workflow makes one). Astra takes the newest release that is not a
   pre-release (a player can pin one with `…/releases/tag/<tag>`).

**A tag is final.** Astra records the zip's digest per tag and refuses a tag whose zip changed. To
fix anything, publish a new tag.

**A newer foundation for your players** is a new release of yours: bump the foundation pins (and
the SDK pins if you want its new API) and tag. Your code rarely needs a change: the SDK only grows
within a major version.

Share your repository's address. There is no catalogue yet: Astra shows every integration as
**Experimental**, and does not review its code.

## 10. How Astra installs it

1. A player pastes `https://github.com/<you>/<repo>` into Astra's Games tab.
2. The launcher fetches your newest release and its `astra-gi.zip`. It records your repository's and
   owner's GitHub ids (a re-created or transferred repository is not the one the player trusted) and
   the zip's digest for that tag.
3. The player sees a consent sheet: among others, the files that go beside the game's exe and your
   licence.
4. The launcher extracts only the files your manifest names. BepInEx and the plugins go into the
   game's profile (Astra's own folder for that game); only `winhttp.dll` and `doorstop_config.ini`
   go beside the exe. One integration per game: yours replaces "Astra for any Unity game" there, after the player
   agrees.
5. At Play, Astra writes the port and token into `astra.unity-foundation.cfg`, makes sure her engine
   runs, and starts the game (through Steam, with Doorstop switched on by launch arguments).
6. An update installs without a new question unless `[target]`, `[[game_files]]`, `[launch]` or
   `[[config_writes]]` changed.

The launcher is open source: [astra-game-launcher](https://github.com/mihailinl/astra-game-launcher).

## 11. IL2CPP integrations

- `--runtime il2cpp` makes a BepInEx 6 plugin: `net6.0`, a `BasePlugin` with `Load()` instead of
  `Awake()`.
- It compiles against the interop assemblies BepInEx 6 generates from the game on its first start
  (`BepInEx/interop`). Set `GameDir` to the folder that holds `BepInEx/interop`: with Astra's
  launcher, the game's profile. Without it the build fails (no Unity types).
- Those assemblies are generated from the game's files, so the public-inputs CI of step 9 does not
  work as it is. No IL2CPP integration is published yet: open an issue on astra-bepinex before you
  plan a release.
- Your own Unity API calls can be stripped from the game too. Run the audit on YOUR plugin:
  `check-game-members il2cpp <your plugin.dll> <profile>/BepInEx/interop`
  ([`CONTRIBUTING.md`](../CONTRIBUTING.md#il2cpp-games-the-stripping-audit)).
- `GameType` works the same way there.

## 12. Coming: your own animations and the game's

Both are built on the engine side and not yet usable from an integration. Do not ship either today.

**An animation pack for your game (engine side done; launcher pending).** An integration will be able
to ship an animation pack (an `.astrapack`: a zip of a pack, marked as yours, with an id, a version
and your game in its `games` list). Your `astra-gi.toml` will place it with a new destination,
`${astra_pack_inbox}`, which the launcher will resolve to the player's pack inbox for your
integration.
The engine imports it, uses it only while your game holds her, and keeps the player's own edits
apart from your pack, so your updates never overwrite them. Today the launcher does not know
`${astra_pack_inbox}`, and it refuses a manifest that names a placeholder it does not know.

**Capturing the game's own animations (engine side done; foundation and Astra's switch pending).**
Opt-in, per integration, by the player, and local only. When the player switches capture on for your
integration, the engine's reply to the game's `hello` names an inbox folder. The integration samples
the game's own animator into a `.vrma` file there and tells the engine (`clip_ready`). The engine
copies it into the player's own pack and asks the player whether to use it. A captured clip never
leaves the player's machine: Astra refuses to export a pack that holds one. The foundation has no
capture API yet. Never put the game's animations in your repository or your zip: they are the game's
assets.

## 13. Before you publish

- [ ] The game has no anti-cheat; `anti_cheat = "none"`.
- [ ] In multiplayer she follows YOUR player.
- [ ] Only raw facts as parameters, listed in your README.
- [ ] No game files and no decompiled code in your repository or your zip.
- [ ] `GameDir` empty: the release builds in CI from public inputs.
- [ ] The SDK version pinned; the foundation in your zip at least as new.
- [ ] The licences of everything bundled are in the zip.
- [ ] `LogOutput.log` shows `is in charge`, no `switched off`, and `GameType.Missing` is empty.
- [ ] `inspect` accepts the zip.
- [ ] The version bumped in three places, a new tag.
- [ ] Your README says how to install (paste the address into Astra's Games tab), what the
      integration changes, and its parameters.
