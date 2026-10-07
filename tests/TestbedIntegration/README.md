# Astra for AstraTestbed

A game integration for Astra on the Astra Unity foundation: it makes Astra better in AstraTestbed.

## Build

```bash
dotnet build -c Release -p:GameDir="/path/to/AstraTestbed"
```

Or put the path in `GameDir.props.user` (git ignores it):

```xml
<Project><PropertyGroup><GameDir>/path/to/AstraTestbed</GameDir></PropertyGroup></Project>
```

## Install (by hand, until the Astra marketplace installs it)

Copy `bin/Release/*/TestbedIntegration.dll` and `astra-item.json` into
`<game>/BepInEx/plugins/TestbedIntegration/`. The game also needs BepInEx and the Astra foundation in
`BepInEx/plugins/Astra/`. **Never ship the foundation's DLLs with this plugin**: it is installed once
per game and updates by itself.

## Rules of the road

- Send raw facts (`climbing = true`), never animation names for movement: her animation set decides
  what a fact looks like, and a pack author can change it without touching code.
- Never touch rendering, the socket or the frame ring. The foundation owns them; if you need
  something from them, open an issue on the foundation.
- Single-player and co-op games without anti-cheat only.
