// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using Astra.Bridge;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>The Astra foundation's BepInEx 6 entry (IL2CPP games): registers the
    /// <see cref="Driver"/> with the IL2CPP runtime, binds the settings and starts it. A game's
    /// integration registers itself through <see cref="Sdk.AstraSdk"/>.</summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BasePlugin
    {
        public override void Load()
        {
            ClassInjector.RegisterTypeInIl2Cpp<Driver>();
            Driver.Configure(Log, Settings.Bind(Config));
            var host = new GameObject("Astra") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Driver>();
            LogIfRunningWithMods();
        }

        /// <summary>The IL2CPP half of the player's-own-mods design (gi-bugs/unity-mods-design.md
        /// §4.4), identical in shape to the Mono entry's (<c>Astra.BepInEx5</c>'s own
        /// <c>Plugin.LogIfRunningWithMods</c>, same name on purpose): the comparison and the
        /// line's wording live once in <see cref="Bridge.ModsLogLine"/>; only the plugin count's
        /// SOURCE differs from BE5's (<see cref="IL2CPPChainloader"/>.Instance.Plugins, not the
        /// static <c>BepInEx.Bootstrap.Chainloader.PluginInfos</c> BE5 reads) because BE6 has no
        /// such static facade — one instance, <see cref="IL2CPPChainloader.Instance"/>, is
        /// BE6's own.</summary>
        void LogIfRunningWithMods()
        {
            try
            {
                string ownRoot = Environment.GetEnvironmentVariable("ASTRA_CHAIN_OWN_ROOT");
                string theirRoot = Paths.BepInExRootPath;
                if (!ModsLogLine.ShouldLog(ownRoot, theirRoot))
                    return;

                string source = Environment.GetEnvironmentVariable("ASTRA_CHAIN_SOURCE");
                string profile = Environment.GetEnvironmentVariable("ASTRA_CHAIN_PROFILE");
                string theirVersion = "unknown";
                string bepinexDll = Path.Combine(Paths.BepInExAssemblyDirectory, "BepInEx.dll");
                if (File.Exists(bepinexDll))
                    theirVersion = FileVersionInfo.GetVersionInfo(bepinexDll).ProductVersion;
                int pluginCount = IL2CPPChainloader.Instance.Plugins.Count;

                Log.LogInfo(ModsLogLine.Format(MyPluginInfo.PLUGIN_VERSION, source, profile,
                    theirVersion, pluginCount));
            }
            catch
            {
                // A diagnostic line is never a reason to take the plugin down.
            }
        }
    }
}
