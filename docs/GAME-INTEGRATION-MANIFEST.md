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

Any CI works; the zip is all Astra sees. Ours (the four integrations under
`github.com/mihailinl/astra-*`) use a GitHub Actions workflow on a tag:

- it builds against public packages only: the Astra SDK, BepInEx.Core and UnityEngine.Modules;
- it never uses the game's own files;
- game types are reached through the SDK's reflection helper.

It then downloads the pinned BepInEx release, verifies its digest, lays out the zip as in §3 and
attaches it to the release.
