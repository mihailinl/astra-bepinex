# Game integrations: plan of record

Astra inside games as a SYSTEM: one foundation that works in any Unity game, small per-game
integrations that the community builds on it, and the Astra marketplace that installs and updates
both. Written 2026-10-06 after the first live run in PEAK. It takes in the answers of the plugins
server agent, the client agent and minice-44, who own the marketplace, the client and identity.

## 1. Four layers, four owners

| layer | what it is | where | who builds it |
|---|---|---|---|
| **Engine** | draws her: pose, animation, face, voice, lighting. Publishes colour + depth frames | `astra-avatar-engine` (`BRIDGE.md`) | Astra |
| **Foundation** | the BepInEx runtime + SDK. Link to the engine, frame ring, compositing for every render pipeline, camera and light feeds, a DEFAULT companion (follows the player), and the public SDK that integrations build on. Mono (BepInEx 5) and IL2CPP (BepInEx 6) | this repository (`astra-bepinex`), MIT | Astra, PRs welcome |
| **Integration** | one game: who the player is, where she stands and how she moves there (PEAK: up the mountain, not a teleport), her size in that world, extra animator facts (`climbing`), cues on game events | one repository per game (`astra-peak`, …) | the community, mostly |
| **Marketplace** | catalogue, review, signing, install, update, uninstall, the anti-cheat denylist | Astra (server, registry, client) | Astra |

**Rule:** the foundation alone must make Astra work in ANY Unity game: placed sensibly, lit, hidden by
walls. An integration only makes her better in one game. It never touches rendering, the socket or
the ring.

## 2. The foundation's SDK: what an integration can change

Everything an integration does goes through one small public API (`Astra.Sdk`, in the foundation
assembly). The engine's own rule applies here too: **raw facts in, the data decides.** An
integration says `climbing = true`. Her animation set decides that this means the climb clip. A pack
author can then build swimming or flying without any code.

```csharp
[BepInPlugin("astra.peak", "Astra for PEAK", "0.1.0")]
[BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)] // loaded after the foundation; refuses an older one
public sealed class Plugin : BaseUnityPlugin
{
    void Awake()
    {
        var astra = AstraSdk.Register("astra.peak", "PEAK");
        astra.Defaults.MatchPlayerHeight = 0.95f;    // a default; the player's own config still wins
        astra.UsePlayer(_ => PeakPlayer.Locate());   // the LOCAL scout, never another player
        astra.UseBrain(new MountainBrain());         // waits under a wall, leaps up after you
        astra.OnFrame(f => f.Params.Set("climbing", PeakPlayer.Climbing));
    }
}
```

The SDK's type names (`PlayerInfo`, `FrameContext`, `GameIntegration`, `IntegrationDefaults`, `Her`,
`IBrain`) are deliberately not `Player` or `Frame`: games declare those in the GLOBAL namespace, and a
global type shadows a `using` (PEAK's `Player` did).

The extension points, one interface each, every one with a default that already works:

| point | default | an integration replaces it with |
|---|---|---|
| `UsePlayer` → `PlayerInfo`: who she follows (feet, facing, root, grounded, height) | the object the config names, else the nearest tagged `Player`, else the ground under the camera | the game's local player (in multiplayer, YOU) |
| `UseBrain` → `IBrain`: where she stands and how she gets there, each frame | the follower: keeps beside you, walls, steps, falls, teleports when far (`FrameContext.Default`) | climbing, swimming, a seat in a vehicle (`Her.Anchor = "hips"` + `Cues.Play("seat")`) |
| `OnFrame` → `FrameContext.Params`: animator facts | `speed` (measured from her motion), `airborne` (`Her.Airborne`) | anything her animation set declares (`climbing`, `altitude`, `underwater`) |
| `Cues`: animation by name | none | `Play("wave")` on a game event, `Trigger("music_fast")` |
| `UseSun`: her sun | the scene's sun, else its brightest directional light | a game with its own day cycle or lights |
| `UseLook`: the band her light is held in | the engine's (floor 0.20, ceiling 1.40) | a game darker or brighter than most (Lethal Company: floor 0.10) |
| `Defaults` → `IntegrationDefaults` | the foundation's | scale or `MatchPlayerHeight`, distances, ground layers, depth bias |
| `Events` (planned, needs the engine and daemon, §5) | none | tell Astra's brain what happened: "the player died", "reached the summit", so she can comment |
| `GameType.Find`/`.Static`/`.Member` (`Astra.Sdk.GameType`) | nothing — an integration must still know its own game's type and field names | reach a game's own types (`StartOfRound`, `PlayerControllerB`, …) by NAME, at run time, with no compile-time reference to the game's assemblies: cached `FieldInfo`/`PropertyInfo` accessors, null-safe and exception-free, logged once per miss in `GameType.Missing`. This is how every integration in `github.com/mihailinl/astra-*` builds without the game's files (`GAME-INTEGRATION-MANIFEST.md` §6) |

**API rules:** additive only within a major version. `[Obsolete]` for one minor before anything is
removed. Mono and IL2CPP expose the SAME API: the runtime differences stay inside the foundation's
`Compat`. An integration builds against Mono or IL2CPP, because a game is one or the other, never
both.

## 3. Packaging: no copied code, updates without republishing

- **Compile time:** the NuGet package `Astra.Unity.Sdk` holds the foundation's reference assembly for
  both runtimes. `lib/netstandard2.0` is Mono and `lib/net6.0` is IL2CPP, and NuGet picks by the
  integration's target. An integration references it **compile-only** (`ExcludeAssets="runtime"`),
  so it never ships the foundation's DLL.
- **Run time:** the foundation is installed once per game as its own item. An integration declares a
  dependency on it in two places (and names `AstraSdk.Dependency`, never `AstraSdk.Version`, in the
  attribute: BepInEx 5 reads a plain version as a minimum, BepInEx 6 as an EXACT version — the SDK's
  IL2CPP build carries the range `>=x.y.z <1.0.0` there):
  - its BepInEx attribute (`[BepInDependency(guid, "1.0.0")]`), which sets the load order and the
    minimum version;
  - its marketplace manifest (`requires`, §4), which makes the installer bring the foundation along.

  A foundation update replaces one folder and every integration picks it up at the next game start,
  without being rebuilt or republished. That is the "update the foundation, every game benefits"
  property.
- **Template:** `dotnet new astra-integration --game "PEAK" --runtime mono` scaffolds the csproj, the
  plugin class, a profile, the item manifest and a CI workflow that builds and packages a release.
  A modder writes behaviour only.
- **Installed-version marker:** the foundation writes `BepInEx/plugins/Astra/astra-item.json`
  (`id`, `version`, `runtime`, `loader`, `protocol`), and so does each integration in its own folder.
  The client reads these to see what is installed.
- **The protocol version** between the foundation and the engine is negotiated in `hello` (§5), not
  assumed. An old foundation keeps working with a new engine.

## 4. Marketplace items

The marketplace becomes ONE store for every kind: commands, plugins, game integrations, later
characters (VRM) and animation packs. What game integrations need from it, agreed in principle with
the owners of each piece:

```toml
# astra-item.toml (proposal: the manifest crate in astra-rs carries it)
id = "astra.peak"
kind = "game-integration"              # plugin | command | library | game-integration | animset | character
version = "1.0.0"
requires = [{ id = "astra.unity-foundation", range = ">=1.2, <2" },
            { id = "astra.peak-climbing-anims", range = ">=1" }]   # an animset item: data, not code
[target]
game = { steam_appid = 3527290, product = "PEAK" }
runtime = "unity-mono"                 # unity-mono | unity-il2cpp
loader = "bepinex5"                    # bepinex5 | bepinex6
platforms = ["windows"]                # the game's build; "windows" covers Proton
```

- **Index shape:** new kinds go in a NEW signed `items[]` array or a separate signed document, NEVER in
  index v1's `plugins[]`. Old clients would read any entry there as a plugin and offer to install it
  (client agent, 2026-10-06).
- **Trust:** the same path as plugins: the registry signer signs every catalogue entry and artefact
  digest, moderation reviews, and advisories can block or disable. A game integration is native code
  inside a game, so it is **never auto-published**: every release is reviewed.
- **Anti-cheat denylist:** a signed list in the index (beside the revocations), with a small floor
  compiled into the client. The server refuses an integration for a listed game at intake. The
  client refuses to install one.
- **The client installer for game kinds** (`astra-daemon/src/games/`, the client agent's plan):
  - Steam library detection, through ONE chokepoint (two copies exist today).
  - It installs BepInEx (a pinned version, the right runtime), then the foundation, then the
    integration, into the GAME folder.
  - One file ledger per game. Uninstall restores the folder exactly, and the client never deletes a
    file it did not write.
  - It coexists with r2modman/Thunderstore installs instead of overwriting them.
  - On Linux the Proton override (`WINEDLLOVERRIDES="winhttp=n,b" %command%`) is a one-click copy;
    it is applied for the user only while Steam is closed, with a backup.
- **Characters and animsets** install into the companion's user root
  (`pack/characters/<id>/`, `pack/animsets/<id>/`) only through the daemon's `CompanionLocation`
  chokepoint. The animation-pack format already has a draft (`astra-plugins-ops/dev/animpack-format.md`).
- **Timeline** (server agent): the kinds land after the current wave: R3–R6, then minice-id, then the
  RU/INTL split. The server side is about 11–15 engineer-days of complexity, 4–6 days of wall-clock
  once the contract exists.
- **Landed — contract 3.14.0** (astra-plugins-ops-49, 2026-10-06; ops `1a78bdf`, registry `be12d72`; FLOW-13 `E_KIND_UNSUPPORTED`):
  - `kind` and `requires` are reserved FAIL-CLOSED. Until a later version defines them, `kind` is
    absent or `"plugin"` and `requires` is absent or empty.
  - The registry bot refuses any other value (`E_KIND_UNSUPPORTED`), and so do the manifest crate
    (Astra 0.2.7) and the CLI (0.5.0): "this item is a <kind>; this version installs plugins only".
    So no client shipped from now on can mistake a game integration for a plugin.
  - Index v1 `plugins[]` carries kind `plugin` only.

  Everything else in this section (the kind list, `requires` semantics, `[target]`, per-kind review,
  the denylist, the ledger and the marker) is settled after R6.
- **The client's design** is `astra-rs/MARKETPLACE_PLAN.md` (Astra main, c239bd24 / 0ba3949d). Its
  phase 0 is DONE (0ba3949d): the manifest crate refuses `kind` ≠ `"plugin"` and a non-empty
  `requires`, read from the raw document, so Astra 0.2.7 tells a user to update instead of installing
  a game integration as a plugin.

## 5. Engine work this system needs

| | what | why |
|---|---|---|
| E1 | `hello` carries `caps` both ways (protocol negotiation) | the foundation and the engine update independently — **DONE both ways** (engine: `CAPS` in `HelloReply`; foundation 0.5.0: `BridgeLink.Supports`/`EffectiveShadow` gate `light`, `light.fog`, `views` and `caster` behind it, and read `caps` ABSENT as "an older engine: keep today's behaviour exactly") |
| E2 | cropped frames (opt-in via `caps`): only her rectangle | today a frame is copied on the game's main thread at up to `MaxPictureHeight` (default lowered to 720 as the interim mitigation, foundation 0.5.0 — still the whole picture, not just her rectangle); she covers 5–15% of it |
| E3 | **game animation LAYERS** (to explore, the owner's question 2026-10-06): an integration's animations as DATA, never code. (1) A layer is a small animset — clips, states, conditional transitions on ITS parameters (`climbing`, `climb_speed`) — the engine lays OVER her character's own set while that game holds her: it may only ADD states/edges on its parameters (no overriding her own states without an explicit opt-in), and goes when the game lets her go. (2) It ships as a marketplace `animset` item the integration `requires`, installed into the user root through the daemon's CompanionLocation chokepoint — a game never sends files (the bridge's security). (3) The protocol only NAMES it (`{"t":"layer","use":"astra.peak-climbing"}`, refused when not installed); clips retarget to any VRM, so one climbing layer serves every character. (4) A layer declares its parameters (validator, Studio preview with fake values). (5) Later: IK targets as raw facts (`"ik":{"leftHand":[x,y,z]}`) so hands grip the rock — the engine's hand IK exists | game-specific movement becomes DATA a pack author ships, never engine code |
| E4 | events up: `{"t":"event",…}` → engine → daemon (a new `CompanionService` variant, proto lockstep) | Astra's brain hears what happened in the game |
| E5 | "Astra is in <game>" in the companion status | the marketplace tab and the UI show it |
| E7 | **her inside the game's post-processing** (the owner's ask, 2026-10-07): today she is composited AFTER it (URP 17 after post, HDRP's custom pass after post, Built-in after image effects), so only the effects a game applies at the very end (REPO's pixelation, Lethal Company's visor) reach her — not its bloom, colour grading, depth of field or fog. Moving her before post-processing needs (1) her depth WRITTEN into the camera's depth (as HDRP's pass already does), so fog and DOF see her at her distance; (2) her colour in the game's HDR units: HDRP's physical exposure would crush an LDR picture to black, so scale by the inverse current exposure; (3) the engine's own tonemap turned off for the bridge (the game's grading replaces it) | she looks like part of the game, not a sticker on it |
| E6 | **who may take her**: today any local program that says `hello` on port 25600 takes her off the desktop (browsers are refused by `Origin`, and a game can only place and frame her, never read or load files). Planned: the first claim by a client name Astra has not seen asks the user in the Astra UI, through the daemon: "PEAK wants to bring Astra into the game: allow / always / no". Clients installed from the marketplace are pre-approved. Without the daemon (development) everything is allowed. | the owner's ask, 2026-10-06: needed for Astra 0.2.7 |

## 6. Phases

| phase | what | state |
|---|---|---|
| **G0** | foundation works in any Unity game: Mono + IL2CPP builds, URP ≤16 / URP 17 (inside the render graph) / Built-in compositing, default follower, game light, testbed (its per-game "profiles" became integrations in G1) | Mono live in PEAK and the testbed (2026-10-06). IL2CPP compiles, but BepInEx 6 itself crashes on a Unity 6.3 Linux IL2CPP player: to verify on a 2022.3 IL2CPP build or a real IL2CPP game. Known IL2CPP gaps: no depth texture is requested for an integration's extra cameras (`Compat.RequestDepthTexture` is Mono-only), and HDRP's exposure adapter cannot load, so an IL2CPP HDRP game gets no world light (she keeps her own) |
| **G1** | the SDK API (§2), `Astra.Unity.Sdk` NuGet, the template, the version marker. PEAK moves OUT of the foundation into `astra-peak`, the first integration: her scale, PEAK's local character, walking up the mountain instead of teleporting | built 2026-10-06. The SDK is proven on the testbed by `tests/TestbedIntegration` (made with the template): its locator, sizing, a raw fact and a cue all reached the engine. `astra-peak` is installed in PEAK; the live check is owed |
| **G2** | marketplace contract proposal (kinds, requires, target, ledger, denylist, trust), sent to astra-plugins-ops. Engine E1 + E5. Client installer once the kind layer exists | contract proposal now; the rest after R6 |
| **G3** | data items: animsets and characters through the marketplace. Engine E3 + E4 | after G2 |
| **G4** | HDRP, her shadow, E2 crop, zero-copy on native Windows, E7 | HDRP adapter 2026-10-06. **Her shadow 2026-10-07**: the engine publishes her SHADOW CASTER (flag 64: a ~7 000-vertex copy of her body in the picture's pose, ~0.1 ms a frame) and the foundation puts it in the scene as a shadows-only renderer, so the GAME casts her shadow — every light, its filtering, its post-processing, in every camera and mirror (seen in the testbed). **Her light 2026-10-07**: the ambient by direction from the game's light probes, up to 8 lamps (points, spots, each pipeline's falloff), the sun's occlusion by three rays through every layer, HDRP's units through its exposure; she is lit as MToon in that world (no glow in the dark, no black paint in her own shadow). E2, zero-copy and E7 as needed |

## 7. The owner's decisions (2026-10-06)

- **Who may publish:** a `publisher` role for community authors who do not own Astra. Roles and
  permissions must be CONFIGURABLE (data, editable from /admin), with the gate checking a capability
  like `publish:<kind>`, never code per role ("make a good system… not a mountain of code").
  minice-id has done this as contract C1.1 (`docs/plans/2026-10-05-minice-id/01-contracts.md`,
  cc161840). Roles and permissions are data, edited in the admin with step-up and an audit log.
  Tokens carry `minice:perms` (`publish:plugin`, `publish:game-integration`, `moderate:plugin`), and a
  change reaches consumers within seconds. Still the owner's call: how someone gets `publisher`
  (they apply, a moderator grants it, or it is automatic).
- **Trust root:** the registry signer stays. The server does intake and moderation.
- **Patents:** none. **This repository is public under MIT.** A Thunderstore mirror comes later.
- **Still open:** paid items and payouts (money and legal, not planned); trademarks. Plain "Astra" is
  taken in software classes (Astra Linux, Astra IT, DataStax Astra), so a distinctive mark or a logo
  is the realistic path.
