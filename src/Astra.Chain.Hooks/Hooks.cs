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
using System.Reflection;
using System.Text;
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
        static string s_lastRunPath;
        static readonly HashSet<string> s_myGuids = new HashSet<string>(StringComparer.Ordinal);
        static BepInEx.Logging.ManualLogSource s_log;

        static Dictionary<string, string> s_pluginIndex;                                   // simple name -> file path
        static readonly Dictionary<string, AssemblyDefinition> s_cecilCache =              // file path -> parsed once
            new Dictionary<string, AssemblyDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <param name="oursBepInExRoot">The `BepInEx` folder of ASTRA's OWN profile (never
        /// theirs) — `<dir of Astra.Chain.dll>/../BepInEx`.</param>
        /// <param name="source">`chain.ini`'s `source` (r2modman/thunderstore/gale/manual), for the
        /// log line only.</param>
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

                harmony = new Harmony("astra.chain.hooks");

                // ConfigFile's ctor is patched FIRST and FindPluginTypes LAST, on purpose: once the
                // FindPluginTypes patch is active, Astra's plugins are folded into theirs and the
                // chainloader can construct them at any time after. Patching the config redirect
                // first means that if EITHER patch call below throws, Astra's plugins are never
                // merged in without their config already safely redirected — the failure mode is
                // "Astra does not appear in this session", never "Astra appears with its config
                // sitting in the player's own profile".
                ConstructorInfo configCtor = typeof(ConfigFile).GetConstructor(
                    new[] { typeof(string), typeof(bool), typeof(BepInPlugin) });
                harmony.Patch(configCtor,
                    prefix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(ConfigFileCtorPrefix), BindingFlags.NonPublic | BindingFlags.Static)));

                MethodInfo findPluginTypes = typeof(TypeLoader)
                    .GetMethod("FindPluginTypes", BindingFlags.Public | BindingFlags.Static)
                    .MakeGenericMethod(typeof(PluginInfo));
                harmony.Patch(findPluginTypes,
                    postfix: new HarmonyMethod(typeof(Hooks).GetMethod(nameof(FindPluginTypesPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
            }
            catch
            {
                if (resolveClrAdded) AppDomain.CurrentDomain.AssemblyResolve -= ResolveClrFromOurs;
                if (resolveCecilAdded) TypeLoader.AssemblyResolve -= ResolveCecilFromOurs;
                harmony?.UnpatchSelf();
                DisposeCecilCache();
                throw;
            }

            // Handed to the foundation's own Awake (design §4.4's log line is THEIRS to print, in
            // its own voice, with the GUID-dedupe and plugin count it can read from the public
            // Chainloader API) through the one channel every later-loaded assembly can read without
            // this DLL referencing the foundation at all: the process environment. ASTRA_CHAIN_OWN_ROOT
            // carries the SAME non-mangled root Astra.Chain itself computed from
            // DOORSTOP_INVOKE_DLL_PATH before it was overwritten with THEIR target — Assembly.Location
            // is never used here either, for the same non-ASCII-path reason.
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_SOURCE", source);
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_PROFILE", profile);
            Environment.SetEnvironmentVariable("ASTRA_CHAIN_OWN_ROOT", oursBepInExRoot);
        }

        // Runs only for the exact call Chainloader.Start makes: FindPluginTypes<PluginInfo>(Paths.PluginPath, ..., "chainloader").
        // A re-entrant call from THIS postfix (below) carries cacheName == null, so it never recurses.
        //
        // This runs LATER than Install() (whenever their chainloader actually calls it, which can be
        // long after this method returned) and MUST NOT throw: it is a Harmony postfix on BepInEx's
        // own method, called from inside THEIR Chainloader.Start(), so an uncaught exception here
        // would end plugin loading for the player's own mods too, not just Astra's. Any failure is
        // caught, logged, left to leave __result exactly as their own FindPluginTypes produced it
        // (their mods still load), and recorded over the "ok" ChainCore already wrote.
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

            try
            {
                MergeOurs(ref __result, typeSelector, assemblyFilter);
            }
            catch (Exception ex)
            {
                s_log?.LogError("could not fold Astra's own plugins into this session: " + ex.Message);
                WriteLastRun("hooks-failed", "postfix: " + ex.Message);
            }
        }

        static void MergeOurs(
            ref Dictionary<string, List<PluginInfo>> result,
            Func<TypeDefinition, PluginInfo> typeSelector,
            Func<AssemblyDefinition, bool> assemblyFilter)
        {
            if (!Directory.Exists(s_oursPluginsDir))
                throw new DirectoryNotFoundException("ours/plugins does not exist: " + s_oursPluginsDir);

            Dictionary<string, List<PluginInfo>> mine =
                TypeLoader.FindPluginTypes(s_oursPluginsDir, typeSelector, assemblyFilter, null);

            foreach (List<PluginInfo> list in mine.Values)
                foreach (PluginInfo pi in list)
                    if (pi.Metadata != null)
                        s_myGuids.Add(pi.Metadata.GUID);

            foreach (KeyValuePair<string, List<PluginInfo>> kv in result)
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
                if (result.TryGetValue(kv.Key, out existing))
                    existing.AddRange(kv.Value);
                else
                    result[kv.Key] = kv.Value;
            }
        }

        // ConfigFile(string configPath, bool saveOnInit, BepInPlugin ownerMetadata): redirect only
        // Astra's own plugins' config to Astra's own profile; every other plugin's config_path is
        // untouched (their profile's BepInEx/config, exactly as their own manager would run them).
        // Only the 3-argument ctor carries ownerMetadata — a plugin that builds its ConfigFile with
        // the 2-argument ctor (no owner metadata) is never redirected; docs/GAME-INTEGRATION-
        // MANIFEST.md §7 says so.
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
        // too, not only at real load time). Each file is parsed with Mono.Cecil at most once: cached
        // by path and reused, so a name resolved many times during one scan opens one file handle.
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
        // folder is walked ONCE, the first time anything is resolved — not on every single resolve.
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
