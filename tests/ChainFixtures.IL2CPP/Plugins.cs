// SPDX-License-Identifier: MIT
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace ChainFixtures
{
    /// <summary>Stands in for an ordinary third-party IL2CPP mod in the player's own profile. Reads
    /// a config value and logs it — proving the value came from THEIR profile's BepInEx/config
    /// (seeded by the test before the run), untouched by Astra's hooks.</summary>
    [BepInPlugin("astra.test.fixturemod", "FixtureMod", "1.0.0")]
    public sealed class FixtureMod : BasePlugin
    {
        public override void Load()
        {
            ConfigEntry<string> value = Config.Bind("Test", "Value", "default");
            Log.LogInfo("FixtureMod value=" + value.Value);
        }
    }

    /// <summary>A stale copy of the foundation's OWN plugin GUID. Version 99.0.0 ON PURPOSE (same
    /// reasoning as tests/ChainFixtures/Plugins.cs's StaleAstra, review S5): newer than any real
    /// foundation release, so only Astra.Chain.Hooks.IL2CPP's "mine always wins, by GUID" rule
    /// (never BepInEx's own newest-version-wins) can be what removes it here.</summary>
    [BepInPlugin("astra.unity-foundation", "Astra (stale)", "99.0.0")]
    public sealed class StaleAstra : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo("StaleAstra Load ran (this must never appear with the chain installed)");
        }
    }

    /// <summary>A per-game integration fixture: declares `[BepInDependency]` on the foundation's
    /// GUID. Its own Load() proves BepInEx's dependency sort placed it after the foundation even
    /// though the foundation was merged in by Astra.Chain.Hooks.IL2CPP, not discovered directly
    /// under THEIR Paths.PluginPath.</summary>
    [BepInPlugin("astra.test.dependsonastra", "DependsOnAstra", "1.0.0")]
    [BepInDependency("astra.unity-foundation")]
    public sealed class DependsOnAstra : BasePlugin
    {
        public override void Load()
        {
            Log.LogInfo("DependsOnAstra loaded");
        }
    }
}
