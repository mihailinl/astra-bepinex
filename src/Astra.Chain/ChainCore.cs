// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Astra.Chain
{
    /// <summary>The chain loader's whole algorithm (design §4.3), shared by both native entry
    /// points: Doorstop 4 calls <see cref="global::Doorstop.Entrypoint.Start"/>, Doorstop 3 calls
    /// <see cref="Program.Main"/>. Once either has been called we are ordinary managed code; which
    /// one got us here makes no difference to what happens next.</summary>
    internal static class ChainCore
    {
        public static void Run()
        {
            string reason = null;
            string chainDir;
            string ours;

            string self = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
            if (!string.IsNullOrEmpty(self))
            {
                chainDir = Path.GetDirectoryName(self);
            }
            else
            {
                // Pathological: Doorstop did not set the env var it always sets. There is no chain.ini
                // to read without knowing our own folder, so this is the one place we fall back to
                // Assembly.Location (never used for the normal path: Mono mangles non-ASCII paths
                // there, which is exactly the case this chain exists to survive).
                reason = "DOORSTOP_INVOKE_DLL_PATH is not set";
                chainDir = Path.GetDirectoryName(new Uri(Assembly.GetExecutingAssembly().CodeBase).LocalPath);
            }
            ours = Path.GetFullPath(Path.Combine(Path.Combine(chainDir, ".."), "BepInEx"));

            string chainIniPath = Path.Combine(chainDir, "chain.ini");
            string source = "unknown";
            string profile = "unknown";
            string target = null;
            MethodInfo theirEntry = null;

            if (reason == null)
            {
                try
                {
                    var ini = ChainIni.Read(chainIniPath);
                    target = ini.Target;
                    if (!string.IsNullOrEmpty(ini.Source)) source = ini.Source;
                    if (!string.IsNullOrEmpty(ini.Profile)) profile = ini.Profile;
                    ValidateTarget(target);
                    theirEntry = FindEntrypoint(target);
                }
                catch (Exception ex)
                {
                    reason = Describe(chainIniPath, target, ex);
                }
            }

            if (reason != null)
            {
                // FALLBACK: anything above failed BEFORE their preloader's Start() was invoked. Run
                // Astra's own preloader exactly as an unchained install would (today's path, byte for
                // byte) — this is the one Invoke() in this method whose exceptions are allowed to
                // propagate uncaught, matching what happens when Doorstop invokes a preloader directly.
                string ownPreloader = Path.Combine(Path.Combine(ours, "core"), "BepInEx.Preloader.dll");
                WriteLastRun(chainDir, "fallback", reason);
                Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", ownPreloader);
                MethodInfo ownEntry = FindEntrypoint(ownPreloader);
                InvokeEntrypoint(ownEntry);
                return;
            }

            // Hand the whole process to THEIR preloader. DOORSTOP_INVOKE_DLL_PATH must read back as
            // their path from here on: their own Entrypoint.Start() reads it via EnvVars.LoadVars(),
            // exactly as if their manager's Doorstop had invoked it directly.
            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", target);
            // A failure INSIDE their Start() is their BepInEx's own failure mode (it writes its own
            // preloader_*.log, as it would under their manager); we do not catch it and we do not
            // retry with ours, because some of their patchers may already have run. Let it propagate.
            InvokeEntrypoint(theirEntry);

            // Their whole preloader ran and returned. Install Astra's hooks so its plugins are
            // discovered alongside theirs; a failure here is reported but never undoes the above.
            try
            {
                InstallHooks(chainDir, ours, source, profile);
                string theirVersion = ReadProductVersion(Path.Combine(Path.GetDirectoryName(target), "BepInEx.dll"));
                WriteLastRun(chainDir, "ok", string.IsNullOrEmpty(theirVersion) ? "unknown" : theirVersion);
            }
            catch (Exception ex)
            {
                // Astra is simply missing from this session (its plugins were never folded into
                // theirs); the game and the player's own mods are unaffected.
                WriteLastRun(chainDir, "fallback", "hooks-install: " + Unwrap(ex).Message);
            }
        }

        static string Describe(string chainIniPath, string target, Exception ex)
        {
            ex = Unwrap(ex);
            if (ex is FileNotFoundException && !File.Exists(chainIniPath))
                return "chain.ini: not found at " + chainIniPath;
            return "chain.ini: " + ex.Message;
        }

        static Exception Unwrap(Exception ex)
        {
            var tie = ex as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : ex;
        }

        /// <summary>`target` must be an existing `BepInEx.Preloader.dll` inside a `BepInEx/core`
        /// folder (design §4.3 step 2) — never anything else, however it got into chain.ini.</summary>
        static void ValidateTarget(string target)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                throw new FileNotFoundException("chain.ini: target does not exist", target);
            if (!string.Equals(Path.GetFileName(target), "BepInEx.Preloader.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chain.ini: target is not named BepInEx.Preloader.dll: " + target);
            string coreDir = Path.GetDirectoryName(target);
            if (coreDir == null || !string.Equals(Path.GetFileName(coreDir), "core", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chain.ini: target is not inside a 'core' folder: " + target);
            string bepinexDir = Path.GetDirectoryName(coreDir);
            if (bepinexDir == null || !string.Equals(Path.GetFileName(bepinexDir), "BepInEx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("chain.ini: target is not inside a 'BepInEx' folder: " + target);
        }

        /// <summary>Loads `assemblyPath` and finds its Doorstop entry method by reflection, so it
        /// binds whatever preloader is actually there (stock, forked, ours) without this assembly
        /// referencing BepInEx at all. Everything in here is "before step 4 invokes": a failure
        /// means fall back.
        ///
        /// Tries `Doorstop.Entrypoint.Start()` first — the method Doorstop 4 itself looks up
        /// (design §2.4) and every BepInEx 5.4.20+ preloader exposes. A preloader from BEFORE that
        /// convention existed (verified against the Subnautica pack's BepInEx.Preloader.dll
        /// 5.4.19, one of §8 test 2's own fixtures: it has no `Doorstop.Entrypoint` at all, only
        /// `BepInEx.Preloader.Entrypoint.Main()`) has no such method — so this falls back to the
        /// OLDER convention Doorstop 3 itself used: the first static `Main()`/`Main(string[])`
        /// anywhere in the assembly (UnityDoorstop v3.4.1.0 `Proxy/main.c`'s own
        /// `mono_method_desc_new("*:Main", FALSE)` search, mirrored here in managed reflection).</summary>
        static MethodInfo FindEntrypoint(string assemblyPath)
        {
            Assembly asm = Assembly.LoadFrom(assemblyPath);

            Type doorstopEntry = asm.GetType("Doorstop.Entrypoint", false);
            if (doorstopEntry != null)
            {
                MethodInfo m = doorstopEntry.GetMethod("Start",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (m != null) return m;
            }

            foreach (Type t in LoadableTypes(asm))
            {
                MethodInfo m = t.GetMethod("Main",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null)
                    ?? t.GetMethod("Main",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(string[]) }, null);
                if (m != null) return m;
            }

            throw new MissingMethodException(
                "neither Doorstop.Entrypoint.Start() nor any static Main() found in " + assemblyPath);
        }

        /// <summary>Invokes an entry method found by <see cref="FindEntrypoint"/>: zero arguments
        /// for `Doorstop.Entrypoint.Start()` or a parameterless `Main()`, an empty `string[]` for
        /// `Main(string[])` — exactly what Doorstop 3's own invoker passes (design §2.4).</summary>
        static void InvokeEntrypoint(MethodInfo m)
        {
            object[] args = m.GetParameters().Length == 1 ? new object[] { new string[0] } : null;
            m.Invoke(null, args);
        }

        static Type[] LoadableTypes(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return Array.FindAll(ex.Types, t => t != null);
            }
        }

        static void InstallHooks(string chainDir, string ours, string source, string profile)
        {
            string hooksPath = Path.Combine(chainDir, "Astra.Chain.Hooks.dll");
            if (!File.Exists(hooksPath))
                throw new FileNotFoundException("Astra.Chain.Hooks.dll not found", hooksPath);
            Assembly asm = Assembly.LoadFrom(hooksPath);
            Type t = asm.GetType("Astra.Chain.Hooks", true);
            MethodInfo m = t.GetMethod("Install", BindingFlags.Static | BindingFlags.Public);
            if (m == null)
                throw new MissingMethodException("Astra.Chain.Hooks.Install(string,string,string) not found");
            try
            {
                m.Invoke(null, new object[] { ours, source, profile });
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                throw tie.InnerException;
            }
        }

        static string ReadProductVersion(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return FileVersionInfo.GetVersionInfo(path).ProductVersion;
            }
            catch
            {
                return null;
            }
        }

        static void WriteLastRun(string chainDir, string status, string detail)
        {
            try
            {
                string time = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                string line = status + " " + time + " " + detail;
                File.WriteAllText(Path.Combine(chainDir, "last-run.txt"), line + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // A log line we cannot write is not a reason to fail the chain itself.
            }
        }
    }
}
