# Astra.Unity.Sdk

Build a **game integration** for Astra: make Astra better
in one Unity game — who the player is, where she stands, her size in that world, raw facts for her
animations. The Astra foundation already makes her work in any Unity game; your integration only
improves one.

```xml
<PackageReference Include="Astra.Unity.Sdk" Version="0.2.*" ExcludeAssets="runtime" PrivateAssets="all" />
```

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
    }
}
```

`ExcludeAssets="runtime"` matters: never ship the foundation's DLLs with your integration — it is
installed in the game once, and updates by itself. Start from the template:
`dotnet new install Astra.Unity.Templates` then `dotnet new astra-integration --game "My Game"`.
