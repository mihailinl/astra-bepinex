// SPDX-License-Identifier: MIT
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
        }
    }
}
