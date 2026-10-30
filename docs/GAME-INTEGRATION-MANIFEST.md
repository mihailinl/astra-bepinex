# The game integration manifest (`astra-gi.toml`, schema 1)

A **game integration** puts Astra inside one game. It is a mod for that game. For Unity games
it is usually a small BepInEx plugin on this repository's foundation; for other games it can be a
ReShade add-on, a MelonLoader mod, a UE4SS script or whatever that game needs.

Astra installs an integration from its **GitHub repository**. The user pastes the address into
Astra's **Games** tab. The work is done by the **game launcher**, a separate open-source program
with no UI that Astra runs. The launcher:

1. fetches the repository's latest release;
2. installs it into a profile of its own;
3. launches the game with it when the user presses **Play**.

The launcher knows nothing about Astra: it fetches, extracts, places files and launches. Anything
Astra-specific reaches it as a variable passed on its command line (see *Placeholders*).

There is no catalogue yet. Integrations are shared by their address and always shown as
**Experimental**: **Astra does not review their code.**

This document is the contract between an integration's author and Astra. The launcher in Astra
knows no game and no mod loader. Everything game-specific is written by the author in this
manifest, as data.

---

## 1. The repository

- **Address.** `https://github.com/<owner>/<repo>`, optionally followed by `/releases/tag/<tag>`
  to pin one release. Nothing else is accepted. Without a tag, Astra takes the newest release
  that is not a pre-release.
- **Identity.** At install, Astra records GitHub's numeric repository id and owner id. An update
  from a repository whose ids changed is refused: a deleted and re-created, or transferred,
  repository is not the one the user trusted. The user is shown the owner's login.
- **Release asset.** The release must carry an asset named **`astra-gi.zip`**. Astra never clones
  the repository and never builds anything. How the zip is built is the author's business (see
  §6 for ours).
- **Tags are immutable.** Astra records the zip's SHA-256 per tag. If the same tag later serves a
  different zip, Astra refuses it ("the author replaced the files of version X"). To change
  anything, publish a new tag.

## 2. The zip

- `astra-gi.toml` at the root, plus every file the manifest names.
- Only the files the manifest names are extracted; anything else in the zip is ignored.
- Refused outright:
  - entries that are symbolic links;
  - entries with absolute paths or `..`;
  - a zip over Astra's size cap, or over its total uncompressed size or entry count.
- **Licences.** Ship the licence of everything you bundle (for example BepInEx's LGPL-2.1
  `LICENSE`, with a link to its source, and this foundation's MIT `LICENSE`). Astra shows the
  manifest's `license` on the consent sheet.

## 3. The manifest

```toml
schema = 1

[integration]                  # metadata: shown to the user, never acted on
id = "astra.lethal-company"    # stable id (letters, digits, . - _)
name = "Astra for Lethal Company"
version = "0.1.1"
authors = ["mihailinl"]
license = "MIT"                # SPDX expression for the integration itself
repo = "https://github.com/mihailinl/astra-lethal"

[target]                       # which game
steam_appid = 1966720
exe = "Lethal Company.exe"     # relative to the game folder
platforms = ["windows-x64", "linux-proton"]
anti_cheat = "none"            # anything else: Astra refuses to install

# A RUNNER for many games instead of one: no appid, the user picks the exe ("Add game"), and the
# launcher checks that the folder is that engine's (Unity Mono: UnityPlayer + <name>_Data/Managed/
# Assembly-CSharp.dll; IL2CPP has GameAssembly.dll instead). The foundation itself ships one such
# manifest — "Astra for any Unity game" — so a game without its own integration still gets her.
#   [target]
#   engine = "unity-mono"
#   exe = "*"
#   platforms = ["windows-x64", "linux-proton"]
#   anti_cheat = "none"

# Into the PROFILE (Astra's own folder for this game; never the game folder).
[[files]]
from = "BepInEx/core/**"       # paths inside the zip; ** = everything below
to = "${profile}/BepInEx/core/"

[[files]]
from = "BepInEx/plugins/**"
to = "${profile}/BepInEx/plugins/"

[[files]]
from = "BepInEx/config/BepInEx.cfg"
to = "${profile}/BepInEx/config/BepInEx.cfg"

# Beside the game's exe — the only files that touch the game folder. Each one is on the consent
# sheet, pinned by its digest, backed up if it replaces a game file and restored on uninstall
# (only if it still has our digest: a file the game updated since is the game's, and it wins).
[[game_files]]
from = "winhttp.dll"
to = "${game}/winhttp.dll"
sha256 = "8c6cdbc38836dee87e3368f5de1994d7c0ccebf29e4ce7aba3c0981f9375412c"

[[game_files]]
from = "doorstop_config.ini"   # ships with enabled = false: Steam's own Play stays vanilla
to = "${game}/doorstop_config.ini"

[launch]
# An argv ARRAY, never a shell string. Placeholders are filled per element (as Windows paths,
# Z:\…, under Proton).
args = ["--doorstop-enabled", "true",
        "--doorstop-target-assembly", "${profile}/BepInEx/core/BepInEx.Preloader.dll"]
# Wine DLL overrides for the game's Proton prefix (Linux), written into that prefix only.
proton_dll_overrides = { winhttp = "native,builtin" }
# env = { KEY = "value" }      # reaches the game ONLY when Astra starts the exe itself (a game
                               # not launched through Steam); a Steam launch carries args only

# INI keys set at every Play, inside the profile only.
[[config_writes]]
file = "${profile}/BepInEx/config/astra.unity-foundation.cfg"
section = "Connection"
set = { Port = "${bridge_port}", Token = "${bridge_token}" }
```

### Placeholders

The launcher's own placeholders:

| placeholder | value |
|---|---|
| `${profile}` | this integration's profile folder |
| `${game}` | the game's folder (the one holding `exe`) |

Every other `${name}` is a **variable** the program running the launcher passes for this launch.
Astra passes these two:

| variable | value |
|---|---|
| `${bridge_port}` | the port Astra's engine listens on for games |
| `${bridge_token}` | the token of this session (empty when the engine asks for none) |

A placeholder that is neither the launcher's own nor passed by the caller is refused.

### Rules

- **Paths.** Every `to`/`file` path starts with `${profile}/` or `${game}/`, is relative after it
  and has no `..`. Astra checks the jail after resolving links. `[[config_writes]]` may only
  write inside `${profile}`.
- **Unknown keys.**
  - In the sections that ACT (`[target]`, `[[files]]`, `[[game_files]]`, `[launch]`,
    `[[config_writes]]`), an unknown key is refused. A newer feature read as nothing would make
    an install that looks fine and is broken.
  - In `[integration]` (metadata), an unknown key is ignored.
- **`schema`.** A schema newer than the user's Astra understands → "this integration needs a
  newer Astra".
- **Anti-cheat.** Astra refuses a manifest whose `anti_cheat` is not `"none"`. It also refuses
  any game whose folder carries a known anti-cheat (EasyAntiCheat, BattlEye, GameGuard,
  XIGNCODE). Using mods in such games can get the player's account banned.
- **One integration per game** at a time. Installing a second one for the same game replaces the
  first, after the user agrees.
- **Updates.** Astra asks the user again when `[target]`, `[[game_files]]`, `[launch]` or
  `[[config_writes]]` changed. A change to `[[files]]` alone (the author's own code) installs
  without a new question. The release notes are shown as plain text.
- **No programs at install.** Schema 1 runs nothing while installing: it only places files, sets
  launch arguments and writes INI keys. Astra never elevates.

## 4. What happens at Play

1. Astra checks the profile and the game files against the ledger it keeps, and re-applies the
   `[[config_writes]]`.
2. Astra makes sure her engine is running:
   - **companion on:** the desktop Astra goes into the game when it connects, and comes back
     when it closes;
   - **companion off:** the engine starts for the game only and stops after it.
3. The game is started through Steam (`steam -applaunch <appid> <args>`) or, for a game Astra
   found in a folder, directly.

Steam's own Play button is left as it was. A loader Astra did not install is never rewritten.
Astra only reuses a proxy whose digest is a known release of the loader it expects; otherwise it
says that another mod loader is installed there.

## 5. For BepInEx integrations on this foundation

- Bundle BepInEx 5 (the official `BepInEx_win_x64_<version>.zip` from its GitHub releases).
- Put BepInEx's `core` and the plugins in the profile, as above. With the preloader in the
  profile, BepInEx keeps its plugins, config, cache and `LogOutput.log` under
  `${profile}/BepInEx`.
- Put Doorstop's proxy (`winhttp.dll`) and `doorstop_config.ini` with `enabled = false` beside
  the exe.
- Turn Doorstop on at Play with `--doorstop-enabled true --doorstop-target-assembly …`. Doorstop
  4's command line overrides its ini.
- The foundation (`BepInEx/plugins/Astra/`) is bundled in every integration's zip. One game has
  one profile, so two copies never meet.
- The IL2CPP flavour (BepInEx 6) also needs `--doorstop-clr-corlib-dir` and
  `--doorstop-clr-runtime-coreclr-path` pointing into the profile.

## 6. Building `astra-gi.zip` in your own CI

Any CI works; the zip is all Astra sees. The integration template (`dotnet new astra-integration`,
[`WRITE-AN-INTEGRATION.md`](WRITE-AN-INTEGRATION.md) §9) writes a GitHub Actions workflow that runs
on a tag; the four integrations under `github.com/mihailinl/astra-*` use an older form of it:

- it builds against public packages only: the Astra SDK, BepInEx.Core and UnityEngine.Modules;
- it never uses the game's own files;
- game types are reached through the SDK's reflection helper.

It then downloads the foundation's pinned release zip (which carries BepInEx's official release;
the older form downloads BepInEx on its own), verifies its digest, lays out the zip as in §3 and
attaches it to the release.

## 7. Playing together with the player's own mods

A BepInEx integration (the runner, or any per-game one built on it) can carry a second, OPTIONAL
`[integration.mods]` table. It changes nothing about how Astra plays by itself; it only gives a
launcher that understands it the means to instead run Astra **alongside** the player's own mods —
their mod manager's profile (r2modman, Thunderstore Mod Manager, Gale) or a hand-installed BepInEx
in the game folder — rather than in Astra's own, separate BepInEx. The full design is
`astra-companion`'s research note (private; the shape that matters here is below).

```toml
[integration.mods]
loader = "bepinex5"                              # "bepinex6-il2cpp" for an IL2CPP integration
chain = "${profile}/chain/Astra.Chain.dll"       # IL2CPP: "${profile}/chain/Astra.Chain.CoreCLR.dll"
chain_config = "${profile}/chain/chain.ini"      # the launcher writes it at every Play with mods
bepinex_build = 788                              # IL2CPP ONLY — see "The IL2CPP flavour" below
```

Plus a files rule that places the chain loader's two DLLs:

```toml
[[files]]
from = "chain/**"
to = "${profile}/chain/"
```

**Why nested under `[integration]`, never a top-level `[mods]`.** §3's rule is absolute: an
unknown key directly under `[target]`, `[[files]]`, `[[game_files]]`, `[launch]` or
`[[config_writes]]` is refused outright — a newer feature an older launcher read as nothing would
make an install that looks fine and is broken. A top-level `[mods]` table would be exactly that
kind of unknown key to every launcher built before this feature, and would break them outright on
this integration's very next release. `[integration]` is different: it is metadata, read with no
key check at all — an unknown field or table inside it is silently dropped. So an integration can
publish `[integration.mods]` the moment it is ready, and:

- an **older launcher** installs the same release and plays Astra on its own, exactly as before —
  it never even notices the table is there;
- a **launcher that reads `integration.mods`** can additionally offer "play with your mods",
  pointing Doorstop at `chain` instead of the usual preloader and writing `chain_config` under the
  profile lock at every Play (the launcher fills in `chain_config`'s `target`/`source`/`profile`
  from whichever mod source the player picked; `chain` itself never changes).

This is the one escape from the "unknown key refused" rule in this whole document, and it exists
for exactly this reason: a capability that is additive for the player (nothing of theirs is ever
touched; §4.4 of the design keeps their config, cache and plugins untouched) must also be additive
for every launcher already in the field.

**`chain.ini`** (UTF-8, no BOM), written at the path `chain_config` names:

```ini
[Chain]
target = Z:\home\u\.local\share\com.kesomannen.gale\peak\profiles\Default\BepInEx\core\BepInEx.Preloader.dll
source = gale          ; r2modman | thunderstore | gale | manual — for the log only
profile = Default      ; for the log only
```

`target` must be an existing `BepInEx.Preloader.dll` inside a `BepInEx/core` folder that also holds
`BepInEx.dll`, `0Harmony.dll` and `Mono.Cecil.dll` — the player's own preloader, wherever their mod
manager put it. The chain loader (`Astra.Chain.dll`) hands the whole process to it exactly as their
own manager would (Doorstop's own `Doorstop.Entrypoint.Start()` ABI, or a pre-5.4.20 preloader's own
static `Main()`), then folds Astra's plugins into their discovery and keeps every Astra plugin's
config in Astra's own profile. Anything wrong with `target` or a broken/partial core (missing,
renamed, a stale path from an uninstalled mod source) makes the chain loader fall back to Astra's
own preloader instead — an ordinary, Astra-only session, never a failure to start. A failure AFTER
the hand-off (folding Astra's plugins into theirs) is reported as `hooks-failed` in
`chain/last-run.txt`, never `fallback` — their preloader already ran under their own BepInEx by
then, so there is no "Astra's own session" to fall back to; Astra is simply missing from that one
session.

**A plugin's `ConfigFile` must be built with its owner metadata to be redirected.** The hook that
keeps every Astra plugin's config in Astra's own profile patches `ConfigFile`'s 3-argument
constructor (`configPath, saveOnInit, ownerMetadata`) — the one `BaseUnityPlugin` itself uses. A
plugin (Astra's own, or a per-game integration on the foundation) that constructs a SECOND
`ConfigFile` by hand with the 2-argument constructor (no owner metadata) gets no redirect: that
file is written wherever `configPath` says, which under a chained session is the player's profile.
Always pass the plugin's own `BepInPlugin` metadata.

**The IL2CPP flavour** (`loader = "bepinex6-il2cpp"`, `Astra.Chain.CoreCLR.dll` +
`Astra.Chain.Hooks.IL2CPP.dll`) hands off to `BepInEx.Unity.IL2CPP.dll` instead of
`BepInEx.Preloader.dll` — same `Doorstop.Entrypoint.Start()` ABI (confirmed by disassembling the
real be.788 core: the method is identical in name and shape to BE5's), same fallback rule, same
`hooks-failed` distinction, same `ConfigFile` 3-argument-ctor rule. Two differences:

- **The exact-build gate.** BepInEx 6 is pre-1.0; its internal APIs (`BaseChainloader<BasePlugin>
  .DiscoverPlugins`, the `ConfigFile` ctor) are not guaranteed stable between be.xxx builds the way
  BE5's plugin API is across 5.4.x. `target`'s `BepInEx.Core.dll` must report a `ProductVersion`
  starting with `6.0.0-be.<bepinex_build>` (the manifest's own field, so a future chain pinned to a
  newer build only has to change one number) — anything else, including a DIFFERENT BepInEx 6 build
  (verified against a real `6.0.0-dev+…` V Rising pack), takes the real fallback, never a
  half-started session.
- **One CoreCLR-only wrinkle.** Doorstop's CoreCLR host fixes `APP_PATHS` exactly once, at process
  boot, to `"<corlib dir>;<directory of the doorstop target assembly>"` — and that target assembly
  is the CHAIN's own path, never theirs (or, on fallback, never Astra's own `BepInEx/core` either).
  So before invoking ANY preloader's `Start()` — theirs on hand-off, Astra's own on fallback —
  `Astra.Chain.CoreCLR` registers an `AssemblyLoadContext.Default.Resolving` handler over that
  preloader's own core directory; without it, every dependency it (or
  `Astra.Chain.Hooks.IL2CPP.dll`) resolves by simple name — `BepInEx.Core`, `0Harmony`,
  `Mono.Cecil`, `MonoMod.*`, `Il2CppInterop.*` — fails to load. BE5/Mono needs no equivalent: .NET
  Framework's `Assembly.LoadFrom` already probes the loaded assembly's own directory for its
  dependencies on its own.
