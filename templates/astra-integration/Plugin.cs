using Astra.Sdk;
using BepInEx;
#if (!mono)
using BepInEx.Unity.IL2CPP;
#endif
using UnityEngine;

namespace AstraIntegration
{
    /// <summary>
    /// Astra for GAME_NAME. The Astra foundation already makes her work here: she follows the player
    /// through the game's colliders, is lit by its sun and hidden by its walls. This plugin makes her
    /// better in THIS game. Replace only what you know better; every point has a working default.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]
#if (mono)
    public sealed class Plugin : BaseUnityPlugin
    {
        void Awake() => Setup();
#else
    public sealed class Plugin : BasePlugin
    {
        public override void Load() => Setup();
#endif

        static void Setup()
        {
            var astra = AstraSdk.Register(MyPluginInfo.PLUGIN_GUID, "GAME_NAME");

            // Her size: as tall as 95% of the player, once the player's height is known
            // (Player.Height below), else a fixed scale.
            // astra.Defaults.MatchPlayerHeight = 0.95f;
            // astra.Defaults.Scale = 1.2f;

            // Who she accompanies: the game's LOCAL player (in multiplayer, you).
            // astra.UsePlayer(cam => new PlayerInfo
            // {
            //     Feet = MyGame.LocalPlayer.transform.position,
            //     Forward = MyGame.LocalPlayer.transform.forward,
            //     Root = MyGame.LocalPlayer.gameObject,
            //     Grounded = MyGame.LocalPlayer.IsGrounded,
            //     Height = 1.8f,
            // });

            // Raw FACTS for her animation set, every frame: "climbing = true", never "play climb".
            // What a fact means is her animation set's business, so a pack author can animate it.
            // astra.OnFrame(f => f.Params.Set("swimming", MyGame.LocalPlayer.InWater));

            // Animation by name on a game event: astra.Cues.Play("wave");
        }
    }
}
