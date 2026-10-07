// SPDX-License-Identifier: MIT
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
            Object.DontDestroyOnLoad(host);
            host.AddComponent<Driver>();
        }
    }
}
