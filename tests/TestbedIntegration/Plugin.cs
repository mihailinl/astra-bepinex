using Astra.Sdk;
using BepInEx;
using UnityEngine;

namespace TestbedIntegration
{
    /// <summary>
    /// The integration the foundation's own tests run on the testbed (unity/AstraTestbed), made from
    /// the astra-integration template. It exercises every extension point a real one uses: its own
    /// player locator (the CharacterController's feet and height), sizing her to the player, a raw
    /// fact every frame, and a cue.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency(AstraSdk.Guid, AstraSdk.Dependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        static int frames;

        void Awake()
        {
            var astra = AstraSdk.Register(MyPluginInfo.PLUGIN_GUID, "AstraTestbed");
            astra.Defaults.MatchPlayerHeight = 0.9f;
            astra.UsePlayer(_ => Locate());
            astra.OnFrame(f =>
            {
                f.Params.Set("testbed_frame", ++frames);
                if (frames == 120) f.Cues.Trigger("testbed_hello");
            });
            Logger.LogInfo("testbed integration registered");
        }

        static PlayerInfo? Locate()
        {
            var p = GameObject.Find("Player");
            if (p == null) return null;
            var cc = p.GetComponent<CharacterController>();
            float height = cc != null ? cc.height : 2f;
            var feet = p.transform.position + Vector3.down * (height * 0.5f - (cc != null ? cc.center.y : 0));
            return new PlayerInfo
            {
                Feet = feet,
                Forward = p.transform.forward,
                Root = p,
                Grounded = cc == null || cc.isGrounded,
                Height = height,
            };
        }
    }
}
