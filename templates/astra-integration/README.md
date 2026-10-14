# Astra for GAME_NAME

A game integration that makes **Astra** better in GAME_NAME. It runs on the Astra Unity foundation
([astra-bepinex](https://github.com/mihailinl/astra-bepinex)), which already draws her inside the
game, lit by its sun and hidden by its walls. This plugin adds what is specific to GAME_NAME.

Only you see her. Single-player and co-op games without anti-cheat only.

## Install with Astra

Paste this repository's address into Astra's **Games** tab:

```
https://github.com/GITHUB_OWNER/AstraIntegration
```

Astra fetches the latest release's `astra-gi.zip`, installs it into a profile of its own and starts
GAME_NAME with it when you press Play.

## Parameters it sends

Her animation set reads these raw facts (pack authors: this is what you can react to):

| parameter | type | meaning |
|---|---|---|
| `speed`, `airborne` | float, bool | measured by the Astra foundation in every game |

## Build

```bash
bash tools/fetch.sh       # once: the pinned Astra SDK into nuget-local/, the foundation into foundation/
#if (mono)
dotnet build -c Release   # → bin/Release/netstandard2.1/AstraIntegration.dll and astra-item.json
#else
dotnet build -c Release -p:GameDir=<the game's Astra profile, which holds BepInEx/interop>
#endif
bash tools/make-astra-gi.sh   # → astra-gi.zip, what a release carries
```

#if (mono)
`GameDir` stays empty: the game's own types are reached by name through `Astra.Sdk.GameType`, so
this builds from public packages alone, in CI too. Commit `packages.lock.json`: CI restores exactly
those packages.
#else
An IL2CPP game's Unity API comes from the interop assemblies BepInEx 6 generates from the game on its
first start: `GameDir` is the folder that holds `BepInEx/interop` (with Astra's launcher, the
game's profile). Those are the game's files, so this cannot build in CI: release by hand (below).
#endif

## Release

1. Bump the version in three places: `<Version>` in `AstraIntegration.csproj`, `[integration].version`
   in `astra-gi.toml`, `version` in `astra-item.toml`.
#if (mono)
2. Commit, then tag and push: `git tag v0.1.0 && git push origin v0.1.0`.
   `.github/workflows/release.yml` builds `astra-gi.zip` and attaches it to the tag's release.
#else
2. Build and lay out the zip (above), commit, tag and push the tag, then attach the zip:
   `gh release create v0.1.0 astra-gi.zip`.
#endif
3. A tag is final: Astra records each tag's zip by its digest. To fix anything, publish a new tag.

A newer foundation for your players: `tools/fetch.sh` says how to move to it.

## Rules of the road

- Send raw facts (`climbing = true`), never animation names for movement: her animation set decides
  what a fact looks like, and a pack author can change it without touching code.
- Never touch rendering, the socket or the frame ring. The foundation owns them; if you need
  something from them, open an issue on the foundation.
- Never put the game's files, or code decompiled from it, in this repository or its zip.
- Single-player and co-op games without anti-cheat only.

The full guide: [Write a game integration](https://github.com/mihailinl/astra-bepinex/blob/main/docs/WRITE-AN-INTEGRATION.md).
