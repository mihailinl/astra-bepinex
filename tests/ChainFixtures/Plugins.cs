// SPDX-License-Identifier: MIT
using BepInEx;
using BepInEx.Configuration;

namespace ChainFixtures
{
    /// <summary>Stands in for an ordinary third-party mod in the player's own profile. Reads a
    /// config value and logs it — proving the value came from THEIR profile's BepInEx/config
    /// (seeded by the test before the run), untouched by Astra's hooks (which only redirect
    /// config paths for Astra's own GUIDs).</summary>
    [BepInPlugin("astra.test.fixturemod", "FixtureMod", "1.0.0")]
    public sealed class FixtureMod : BaseUnityPlugin
    {
        void Awake()
        {
            ConfigEntry<string> value = Config.Bind("Test", "Value", "default");
            Logger.LogInfo("FixtureMod value=" + value.Value);
        }
    }

    /// <summary>A semver-prerelease plugin version ("1.0.0-beta.1"). Stock BepInEx 5.4.x rejects
    /// this (its own version parsing throws on the metadata), so this plugin's Awake only ever
    /// runs under a fork that accepts semver versions (the Valheim pack) — proof that pack's own
    /// core, not a substitute, is actually loading plugins.</summary>
    [BepInPlugin("astra.test.semvermod", "SemverMod", "1.0.0-beta.1")]
    public sealed class SemverMod : BaseUnityPlugin
    {
        void Awake()
        {
            Logger.LogInfo("SemverMod loaded");
        }
    }

    /// <summary>A stale copy of the foundation's OWN plugin GUID, standing in for a hand-copied or
    /// mirrored Astra sitting in the player's profile. Astra.Chain.Hooks' dedupe-by-GUID (design
    /// §4.4 #2) must remove this from the discovered set before the chainloader ever constructs
    /// it, so this Awake must never run once the chain is installed.</summary>
    [BepInPlugin("astra.unity-foundation", "Astra (stale)", "0.1.0")]
    public sealed class StaleAstra : BaseUnityPlugin
    {
        void Awake()
        {
            Logger.LogInfo("StaleAstra Awake ran (this must never appear with the chain installed)");
        }
    }
}
