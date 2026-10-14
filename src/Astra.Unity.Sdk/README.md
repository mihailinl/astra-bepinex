# Astra.Unity.Sdk

Build a **game integration** for Astra: make Astra better
in one Unity game — who the player is, where she stands, her size in that world, raw facts for her
animations. The Astra foundation already makes her work in any Unity game; your integration only
improves one.

```xml
<PackageReference Include="Astra.Unity.Sdk" Version="[0.6.1,1)" ExcludeAssets="runtime" PrivateAssets="all" />
```

The floor of the range is the SDK you build against: NuGet takes the LOWEST version a range allows.
This package is not on nuget.org: every foundation release on GitHub
(https://github.com/mihailinl/astra-bepinex/releases) carries it; put it in a local package folder
your `nuget.config` names.

```csharp
[BepInPlugin("astra.mygame", "Astra for My Game", "1.0.0")]
[BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]
public sealed class Plugin : BaseUnityPlugin
{
    void Awake()
    {
        var astra = AstraSdk.Register("astra.mygame", "My Game");
        astra.Defaults.MatchPlayerHeight = 0.95f;
        astra.OnFrame(f => f.Params.Set("swimming", MyGame.Player.InWater));
        astra.DeclareParam("swimming", ParamType.Bool);   // SDK 0.7.0+ (using Astra.Bridge;)
    }
}
```

`ExcludeAssets="runtime"` matters: never ship the foundation's DLLs with your integration — it is
installed in the game once, and updates by itself.

Start from the template, which sets all of this up (the SDK fetched and digest-checked, the manifest,
a release workflow). It is not on nuget.org either; install it from a clone:

```bash
git clone https://github.com/mihailinl/astra-bepinex
dotnet new install ./astra-bepinex/templates/astra-integration
dotnet new astra-integration -n AstraMyGame --game "My Game" --id astra.mygame --steamAppId 1234560 --owner <your GitHub name>
```

The guide: https://github.com/mihailinl/astra-bepinex/blob/main/docs/WRITE-AN-INTEGRATION.md
