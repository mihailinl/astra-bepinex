// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using Astra.Bridge;
using BepInEx;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>The Astra foundation's BepInEx 5 entry (Mono games): binds the settings and starts
    /// the <see cref="Driver"/>. A game's integration registers itself through <see cref="Sdk.AstraSdk"/>.</summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public sealed class Plugin : BaseUnityPlugin
    {
        void Awake()
        {
            Driver.Configure(Logger, Settings.Bind(Config));
            var host = new GameObject("Astra") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<Driver>();
            LogIfRunningWithMods();
        }

        /// <summary>The player's-own-mods design (gi-bugs/unity-mods-design.md §4.4): when this
        /// plugin's own install folder is not the BepInEx root Doorstop actually started under
        /// (<see cref="Paths.BepInExRootPath"/>), the chain loader handed the process to the
        /// PLAYER's own BepInEx instead of Astra's — one line in THEIR LogOutput.log, where their
        /// mod manager's users look, says so. An ordinary standalone session (today's, unchanged)
        /// prints nothing here.
        ///
        /// Astra's own root comes from `ASTRA_CHAIN_OWN_ROOT`, set only by
        /// `Astra.Chain.Hooks.Install` from the SAME non-mangled path Astra.Chain itself derived
        /// from `DOORSTOP_INVOKE_DLL_PATH` before overwriting it with THEIR target — never
        /// `Assembly.Location`, which Mono mangles for a non-ASCII install path (the chained case
        /// Astra's players actually hit). The variable is simply absent in an ordinary,
        /// unchained session, so this prints nothing without ever comparing anything. The
        /// comparison and the line's wording are shared with the IL2CPP backend
        /// (<see cref="Bridge.ModsLogLine"/>); only the BepInEx-specific reads (paths, the
        /// plugin count, the logger) stay here, one per backend.</summary>
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
                int pluginCount = BepInEx.Bootstrap.Chainloader.PluginInfos.Count;

                Logger.LogInfo(ModsLogLine.Format(MyPluginInfo.PLUGIN_VERSION, source, profile,
                    theirVersion, pluginCount));
            }
            catch
            {
                // A diagnostic line is never a reason to take the plugin down.
            }
        }
    }
}
