// SPDX-License-Identifier: MIT
//
// The dedupe-by-GUID and config-redirect technique below is the one BepInEx.MultiFolderLoader
// (https://github.com/toebeann/BepInEx.MultiFolderLoader, MIT) pioneered for loading plugins from
// more than one folder under a single BepInEx — credited here per design §4.1/§4.4, the same as
// Astra.Chain.Hooks (the Mono/BepInEx 5 half). This is an independent implementation (no
// MultiFolderLoader code is reused), built against BepInEx.Unity.IL2CPP 6.0.0-be.788 — the one
// build this whole IL2CPP chain is gated on (Astra.Chain.CoreCLR/ChainCore.cs's ValidateTarget).
//
// This half's hook SURFACE differs from BE5's on purpose (docs/GAME-INTEGRATION-MANIFEST.md §7,
// design §4.4): BE6's own `BaseChainloader<TPlugin>.DiscoverPlugins()` is an INSTANCE method
// (there is exactly one chainloader instance, IL2CPPChainloader.Instance), not BE5's static
// `TypeLoader.FindPluginTypes`, and it returns a flat `IList<PluginInfo>` rather than a
// `Dictionary<string, List<PluginInfo>>` keyed by file path — so the merge below replaces
// `__result` outright instead of editing a dictionary's lists in place.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Mono.Cecil;

namespace Astra.Chain
{
    /// <summary>Installed once, right after the player's own BepInEx 6 preloader has returned
    /// (<c>Astra.Chain.ChainCore.InstallHooks</c>), and before their chainloader's
    /// <c>DiscoverPlugins</c> is ever called (it runs later, from IL2CPP's own domain-init detour —
    /// <c>IL2CPPChainloader.OnInvokeMethod</c>). Folds Astra's own plugins into THEIR discovery and
    /// keeps every Astra plugin's config in Astra's own profile (design §4.4).</summary>
    public static class Hooks
    {
        static string s_oursPluginsDir;
        static string s_oursConfigDir;
        static string s_lastRunPath;
        static readonly HashSet<string> s_myGuids = new HashSet<string>(StringComparer.Ordinal);
        static BepInEx.Logging.ManualLogSource s_log;

        static Dictionary<string, string> s_pluginIndex;                                   // simple name -> file path
        static readonly Dictionary<string, AssemblyDefinition> s_cecilCache =              // file path -> parsed once
            new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <param name="oursBepInExRoot">The `BepInEx` folder of ASTRA's OWN profile (never
        /// theirs) — `<dir of Astra.Chain.CoreCLR.dll>/../BepInEx`.</param>
        /// <param name="source">`chain.ini`'s `source`, for the log line only.</param>
        /// <param name="profile">`chain.ini`'s `profile`, for the log line only.</param>
        public static void Install(string oursBepInExRoot, string source, string profile)
        {
            s_oursPluginsDir = Path.Combine(oursBepInExRoot, "plugins");
            s_oursConfigDir = Path.Combine(oursBepInExRoot, "config");
            s_lastRunPath = Path.Combine(Path.Combine(Path.GetDirectoryName(oursBepInExRoot), "chain"), "last-run.txt");
            s_log = BepInEx.Logging.Logger.CreateLogSource("Astra.Chain");

            bool resolveClrAdded = false, resolveCecilAdded = false;
            Harmony harmony = null;
            try
            {
                AppDomain.CurrentDomain.AssemblyResolve += ResolveClrFromOurs;
                resolveClrAdded = true;
                TypeLoader.AssemblyResolve += ResolveCecilFromOurs;
                resolveCecilAdded = true;

                harmony = new Harmony("astra.chain.hooks.il2cpp");

                // ConfigFile's ctor is patched FIRST and DiscoverPlugins LAST, for the same reason
                // as the Mono half: if either patch call below throws, Astra's plugins are never
                // merged in without their config already safely redirected.
                ConstructorInfo configCtor = typeof(ConfigFile).GetConstructor(
                    new[] { typeof(string), typeof(bool), typeof(BepInPlugin) });
                harmony.Patch(configCtor,
                    prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(ConfigFileCtorPrefix), BindingFlags.NonPublic | BindingFlags.Static)));

                // BaseChainloader<BasePlugin>.DiscoverPlugins is `protected virtual` — IL2CPPChainloader
                // never overrides it, so IL2CPPChainloader's own direct base IS the constructed generic
                // type we need (no magic string naming its type argument).
                Type chainloaderBase = typeof(IL2CPPChainloader).BaseType;
                MethodInfo discoverPlugins = chainloaderBase.GetMethod("DiscoverPlugins",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (discoverPlugins == null)
                    throw new MissingMethodException("BaseChainloader<BasePlugin>.DiscoverPlugins() not found");
                harmony.Patch(discoverPlugins,
                    postfix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(DiscoverPluginsPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch
            {
                if (resolveClrAdded) AppDomain.CurrentDomain.AssemblyResolve -= ResolveClrFromOurs;
                if (resolveCecilAdded) TypeLoader.AssemblyResolve -= ResolveCecilFromOurs;
                harmony?.UnpatchSelf();
                DisposeCecilCache();
                throw;
            }

            // Handed to the foundation's own Load() (the BE6 analogue of BE5's Awake()) through the
            // process environment — the same ASTRA_CHAIN_* triple the Mono half publishes, so one
            // shared log line in the foundation reads identically under either backend.
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_SOURCE", source);
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_PROFILE", profile);
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_OWN_ROOT", oursBepInExRoot);
        }

        // Runs for EVERY call to BaseChainloader<BasePlugin>.DiscoverPlugins() — there is exactly
        // one such instance (IL2CPPChainloader.Instance), called exactly once from Execute(). MUST
        // NOT throw: it is a Harmony postfix on BepInEx's own method, called from inside their own
        // IL2CPP init detour, and an uncaught exception here would end plugin loading for the
        // player's own mods too. Any failure is caught, logged, left to leave __result exactly as
        // their own DiscoverPlugins produced it (their mods still load), and recorded over the "ok"
        // ChainCore already wrote.
        static void DiscoverPluginsPostfix(object __instance, ref IList<PluginInfo> __result)
        {
            try
            {
                MergeOurs(__instance, ref __result);
            }
            catch (Exception ex)
            {
                s_log?.LogError("could not fold Astra's own plugins into this session: " + ex.Message);
                WriteLastRun("hooks-failed", "postfix: " + ex.Message);
            }
        }

        static void MergeOurs(object chainloaderInstance, ref IList<PluginInfo> result)
        {
            if (!Directory.Exists(s_oursPluginsDir))
                throw new DirectoryNotFoundException("ours/plugins does not exist: " + s_oursPluginsDir);

            // `protected IList<PluginInfo> DiscoverPluginsFrom(string path, string cacheName =
            // "chainloader")` on BaseChainloader<TPlugin> — called on THE SAME chainloader instance
            // that is discovering theirs, with cacheName: null so no cache file of theirs is ever
            // touched (design §4.4 #2).
            MethodInfo discoverFrom = chainloaderInstance.GetType()
                .GetMethod("DiscoverPluginsFrom", BindingFlags.NonPublic | BindingFlags.Instance);
            if (discoverFrom == null)
                throw new MissingMethodException("BaseChainloader<BasePlugin>.DiscoverPluginsFrom(string,string) not found");
            var mine = (IList<PluginInfo>)discoverFrom.Invoke(chainloaderInstance, new object[] { s_oursPluginsDir, null });

            foreach (PluginInfo pi in mine)
                if (pi.Metadata != null)
                    s_myGuids.Add(pi.Metadata.GUID);

            List<PluginInfo> merged = result
                .Where(pi =>
                {
                    if (pi.Metadata == null || !s_myGuids.Contains(pi.Metadata.GUID))
                        return true;
                    s_log.LogWarning("Skipping " + pi.Metadata.GUID + " at " + pi.Location +
                        ": Astra loads its own copy");
                    return false;
                })
                .Concat(mine)
                .ToList();

            result = merged;
        }

        // ConfigFile(string configPath, bool saveOnInit, BepInPlugin ownerMetadata): redirect only
        // Astra's own plugins' config to Astra's own profile; every other plugin's config_path is
        // untouched (their profile's BepInEx/config, exactly as their own manager would run them).
        // Same rule as the Mono half: a plugin that builds ConfigFile with the 2-argument ctor (no
        // owner metadata) is never redirected — BasePlugin's own ctor always uses the 3-argument one.
        static bool ConfigFileCtorPrefix(ref string configPath, BepInPlugin ownerMetadata)
        {
            if (ownerMetadata != null && s_myGuids.Contains(ownerMetadata.GUID))
                configPath = Path.Combine(s_oursConfigDir, Path.GetFileName(configPath));
            return true;
        }

        // AppDomain.AssemblyResolve: the REAL CLR load, for when the chainloader instantiates a
        // discovered Astra plugin type (Activator.CreateInstance needs the assembly actually
        // loaded; it is not under their Paths.PluginPath, so the default probing never finds it).
        static Assembly ResolveClrFromOurs(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string path = FindUnderOurs(simpleName);
            return path != null ? Assembly.LoadFrom(path) : null;
        }

        // TypeLoader.AssemblyResolve: Mono.Cecil's own second-chance resolver, used while
        // DiscoverPluginsFrom reads candidate DLLs' metadata WITHOUT loading them as CLR assemblies.
        // Each file is parsed with Mono.Cecil at most once: cached by path and reused.
        static AssemblyDefinition ResolveCecilFromOurs(object sender, AssemblyNameReference reference)
        {
            string path = FindUnderOurs(reference.Name);
            if (path == null) return null;
            AssemblyDefinition def;
            if (!s_cecilCache.TryGetValue(path, out def))
            {
                def = AssemblyDefinition.ReadAssembly(path);
                s_cecilCache[path] = def;
            }
            return def;
        }

        // Answers ONLY for a name that exists under ours/plugins (recursive) — design §4.4 #1;
        // anything else returns null and the normal resolve chain (or failure) continues. The
        // folder is walked ONCE, the first time anything is resolved.
        static string FindUnderOurs(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName)) return null;
            if (s_pluginIndex == null)
            {
                s_pluginIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(s_oursPluginsDir))
                {
                    foreach (string f in Directory.EnumerateFiles(s_oursPluginsDir, "*.dll", SearchOption.AllDirectories))
                        s_pluginIndex[Path.GetFileNameWithoutExtension(f)] = f;
                }
            }
            string path;
            return s_pluginIndex.TryGetValue(simpleName, out path) ? path : null;
        }

        static void DisposeCecilCache()
        {
            foreach (AssemblyDefinition def in s_cecilCache.Values)
                def.Dispose();
            s_cecilCache.Clear();
        }

        static void WriteLastRun(string status, string detail)
        {
            try
            {
                string time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                File.WriteAllText(s_lastRunPath, status + " " + time + " " + detail + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // A log line we cannot write is not a reason to fail the chain itself.
            }
        }
    }
}
