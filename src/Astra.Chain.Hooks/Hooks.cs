// SPDX-License-Identifier: MIT
//
// The dedupe-by-GUID and config-redirect technique below is the one BepInEx.MultiFolderLoader
// (https://github.com/toebeann/BepInEx.MultiFolderLoader, MIT) pioneered for loading plugins from
// more than one folder under a single BepInEx — credited here per design §4.1/§4.4. This is an
// independent implementation (no MultiFolderLoader code is reused), built against the SAME BepInEx
// 5.4 internals it relies on (`TypeLoader.FindPluginTypes`'s Harmony-postfix seam), verified against
// the real BepInEx.dll 5.4.20 shipped in the packs under gi-bugs/packs/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using Mono.Cecil;

namespace Astra.Chain
{
    /// <summary>Installed once, right after the player's own BepInEx preloader has returned
    /// (<c>Astra.Chain.ChainCore.InstallHooks</c>), and before their chainloader ever runs (it runs
    /// later, from the patched entrypoint). Folds Astra's own plugins into THEIR discovery and keeps
    /// every Astra plugin's config in Astra's own profile (design §4.4).</summary>
    public static class Hooks
    {
        static string s_oursPluginsDir;
        static string s_oursConfigDir;
        static readonly HashSet<string> s_myGuids = new HashSet<string>(StringComparer.Ordinal);
        static BepInEx.Logging.ManualLogSource s_log;

        /// <param name="oursBepInExRoot">The `BepInEx` folder of ASTRA's OWN profile (never
        /// theirs) — `<dir of Astra.Chain.dll>/../BepInEx`.</param>
        /// <param name="source">`chain.ini`'s `source` (r2modman/thunderstore/gale/manual), for the
        /// log line only.</param>
        /// <param name="profile">`chain.ini`'s `profile`, for the log line only.</param>
        public static void Install(string oursBepInExRoot, string source, string profile)
        {
            s_oursPluginsDir = Path.Combine(oursBepInExRoot, "plugins");
            s_oursConfigDir = Path.Combine(oursBepInExRoot, "config");
            s_log = BepInEx.Logging.Logger.CreateLogSource("Astra.Chain");

            AppDomain.CurrentDomain.AssemblyResolve += ResolveClrFromOurs;
            TypeLoader.AssemblyResolve += ResolveCecilFromOurs;

            var harmony = new Harmony("astra.chain.hooks");

            MethodInfo findPluginTypes = typeof(TypeLoader)
                .GetMethod("FindPluginTypes", BindingFlags.Public | BindingFlags.Static)
                .MakeGenericMethod(typeof(PluginInfo));
            harmony.Patch(findPluginTypes,
                postfix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(FindPluginTypesPostfix), BindingFlags.NonPublic | BindingFlags.Static)));

            ConstructorInfo configCtor = typeof(ConfigFile).GetConstructor(
                new[] { typeof(string), typeof(bool), typeof(BepInPlugin) });
            harmony.Patch(configCtor,
                prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(ConfigFileCtorPrefix), BindingFlags.NonPublic | BindingFlags.Static)));

            // Handed to the foundation's own Awake (design §4.4's log line is THEIRS to print, in
            // its own voice, with the GUID-dedupe and plugin count it can read from the public
            // Chainloader API) through the one channel every later-loaded assembly can read without
            // this DLL referencing the foundation at all: the process environment.
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_SOURCE", source);
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_PROFILE", profile);
        }

        // Runs only for the exact call Chainloader.Start makes: FindPluginTypes<PluginInfo>(Paths.PluginPath, ..., "chainloader").
        // A re-entrant call from THIS postfix (below) carries cacheName == null, so it never recurses.
        static void FindPluginTypesPostfix(
            ref Dictionary<string, List<PluginInfo>> __result,
            string directory,
            Func<TypeDefinition, PluginInfo> typeSelector,
            Func<AssemblyDefinition, bool> assemblyFilter,
            string cacheName)
        {
            if (!string.Equals(directory, Paths.PluginPath, StringComparison.Ordinal) ||
                !string.Equals(cacheName, "chainloader", StringComparison.Ordinal))
                return;

            Dictionary<string, List<PluginInfo>> mine =
                TypeLoader.FindPluginTypes(s_oursPluginsDir, typeSelector, assemblyFilter, null);

            foreach (List<PluginInfo> list in mine.Values)
                foreach (PluginInfo pi in list)
                    if (pi.Metadata != null)
                        s_myGuids.Add(pi.Metadata.GUID);

            foreach (KeyValuePair<string, List<PluginInfo>> kv in __result)
            {
                // PluginInfo.Location is filled in by Chainloader AFTER FindPluginTypes returns;
                // the dictionary's own key (the file path FindPluginTypes just read) is what we have.
                string path = kv.Key;
                kv.Value.RemoveAll(pi =>
                {
                    if (pi.Metadata == null || !s_myGuids.Contains(pi.Metadata.GUID))
                        return false;
                    s_log.LogWarning("Skipping " + pi.Metadata.GUID + " at " + path +
                        ": Astra loads its own copy");
                    return true;
                });
            }

            foreach (KeyValuePair<string, List<PluginInfo>> kv in mine)
            {
                List<PluginInfo> existing;
                if (__result.TryGetValue(kv.Key, out existing))
                    existing.AddRange(kv.Value);
                else
                    __result[kv.Key] = kv.Value;
            }
        }

        // ConfigFile(string configPath, bool saveOnInit, BepInPlugin ownerMetadata): redirect only
        // Astra's own plugins' config to Astra's own profile; every other plugin's config_path is
        // untouched (their profile's BepInEx/config, exactly as their own manager would run them).
        static bool ConfigFileCtorPrefix(ref string configPath, BepInPlugin ownerMetadata)
        {
            if (ownerMetadata != null && s_myGuids.Contains(ownerMetadata.GUID))
                configPath = Path.Combine(s_oursConfigDir, Path.GetFileName(configPath));
            return true;
        }

        // AppDomain.AssemblyResolve: the REAL CLR load, for when the chainloader instantiates a
        // discovered Astra plugin type (Activator-style activation needs the assembly actually
        // loaded; it is not in Paths.PluginPath, so the default probing never finds it).
        static Assembly ResolveClrFromOurs(object sender, ResolveEventArgs args)
        {
            string simpleName = new AssemblyName(args.Name).Name;
            string path = FindUnderOurs(simpleName);
            return path != null ? Assembly.LoadFrom(path) : null;
        }

        // TypeLoader.AssemblyResolve: Mono.Cecil's own second-chance resolver, used while
        // FindPluginTypes reads candidate DLLs' metadata WITHOUT loading them as CLR assemblies (so
        // a cross-reference between two of Astra's own plugin DLLs resolves during that static scan
        // too, not only at real load time).
        static AssemblyDefinition ResolveCecilFromOurs(object sender, AssemblyNameReference reference)
        {
            string path = FindUnderOurs(reference.Name);
            return path != null ? AssemblyDefinition.ReadAssembly(path) : null;
        }

        // Answers ONLY for a name that exists under ours/plugins (recursive) — design §4.4 #1;
        // anything else returns null and the normal resolve chain (or failure) continues.
        static string FindUnderOurs(string simpleName)
        {
            if (string.IsNullOrEmpty(simpleName) || !Directory.Exists(s_oursPluginsDir))
                return null;
            string fileName = simpleName + ".dll";
            return Directory.EnumerateFiles(s_oursPluginsDir, "*.dll", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
