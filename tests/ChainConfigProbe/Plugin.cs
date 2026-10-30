// SPDX-License-Identifier: MIT
using BepInEx;
using BepInEx.Configuration;

namespace ChainConfigProbe
{
    /// <summary>Placed in ASTRA'S OWN plugins folder for tools/test-chain.sh. Its GUID is folded
    /// into Astra.Chain.Hooks' `myGuids` by the FindPluginTypes postfix (it is discovered under
    /// `ours/plugins`, same as the real foundation), so the ConfigFile ctor prefix must redirect
    /// its config to Astra's own profile even though it is constructed while `Paths.ConfigPath`
    /// points at the player's. Unlike the read-only fixtures in tests/ChainFixtures, this one SETS
    /// a value, forcing BepInEx to actually write the .cfg file — the one concrete, on-disk proof
    /// the redirect happened (design §4.4 #3, §4.5).</summary>
    [BepInPlugin("astra.chain.config-probe", "ChainConfigProbe", "1.0.0")]
    public sealed class Plugin : BaseUnityPlugin
    {
        void Awake()
        {
            ConfigEntry<string> value = Config.Bind("Probe", "Value", "unset");
            value.Value = "written-by-ours";
            Logger.LogInfo("ChainConfigProbe wrote " + value.Value);
        }
    }
}
