// SPDX-License-Identifier: MIT
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace ChainConfigProbe
{
    /// <summary>Placed in ASTRA'S OWN plugins folder for tools/test-chain-il2cpp.sh. Its GUID is
    /// folded into Astra.Chain.Hooks.IL2CPP's `myGuids` by the DiscoverPlugins postfix, so the
    /// ConfigFile ctor prefix must redirect its config to Astra's own profile even though it is
    /// constructed while `Paths.ConfigPath` points at the player's.</summary>
    [BepInPlugin("astra.chain.config-probe", "ChainConfigProbe", "1.0.0")]
    public sealed class Plugin : BasePlugin
    {
        public override void Load()
        {
            ConfigEntry<string> value = Config.Bind("Probe", "Value", "unset");
            value.Value = "written-by-ours";
            Log.LogInfo("ChainConfigProbe wrote " + value.Value);
        }
    }

    /// <summary>Stands in for the REAL IL2CPP foundation (`astra.unity-foundation`,
    /// `Astra.BepInEx6.IL2CPP`) in `tools/test-chain-il2cpp.sh` only: the real one compiles against
    /// `Il2CppInteropDir`, a `BepInEx/interop` folder BepInEx 6 generates for a real IL2CPP game on
    /// its first start, which this sandboxed test environment cannot produce (no working native
    /// IL2CPP toolchain here — ld.lld needs a host ICU this box does not have, a DIFFERENT gap from
    /// CONTRIBUTING.md's documented older-editor one). Astra.Chain.Hooks.IL2CPP's own merge/dedupe
    /// logic is agnostic to what is actually inside `ours/plugins` — it keys on the GUID alone — so
    /// a stand-in with the real foundation's exact GUID exercises the SAME dedupe path
    /// `tests/ChainFixtures.IL2CPP`'s `StaleAstra` collides with, without needing the real plugin.</summary>
    [BepInPlugin("astra.unity-foundation", "Astra (stand-in)", "0.1.0")]
    public sealed class FoundationStandIn : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo("FoundationStandIn loaded (stands in for the real foundation)");
        }
    }
}
