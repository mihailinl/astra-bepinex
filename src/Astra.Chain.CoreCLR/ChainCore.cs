// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;

namespace Astra.Chain
{
    /// <summary>The CoreCLR/IL2CPP chain loader's whole algorithm (design §4.3, §9), the sibling of
    /// Astra.Chain/ChainCore.cs (Mono/BepInEx 5). Doorstop's CoreCLR host calls
    /// <see cref="global::Doorstop.Entrypoint.Start"/> directly (there is no Doorstop-3-era fallback
    /// convention to support here: CoreCLR/IL2CPP support in Doorstop post-dates that era entirely —
    /// every be.xxx BepInEx 6 build exposes <c>Doorstop.Entrypoint.Start()</c>, confirmed against the
    /// real be.788 core). Once that is called we are ordinary managed code, same as the Mono half —
    /// with exactly one CoreCLR-only wrinkle: see <see cref="MakeCoreResolver"/>.</summary>
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
                // Pathological: Doorstop did not set the env var it always sets. Assembly.Location is
                // used here only (never below): on CoreCLR, unlike Mono, Location is NOT mangled for
                // non-ASCII install paths, and Assembly.CodeBase (what the Mono half uses for this
                // exact fallback) throws PlatformNotSupportedException on .NET Core — it does not
                // exist here at all.
                reason = "DOORSTOP_INVOKE_DLL_PATH is not set";
                chainDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            }
            ours = Path.GetFullPath(Path.Combine(Path.Combine(chainDir, ".."), "BepInEx"));

            // What a direct `--doorstop-target-assembly` start would have had as its CoreCLR corlib
            // dir: Doorstop's own il2cpp_doorstop_bootstrap (gi-bugs/src/doorstop/src_bootstrap.c)
            // repurposes DOORSTOP_MANAGED_FOLDER_DIR to carry clr_corlib_dir for THIS boot path (its
            // Mono meaning — the game's Managed folder — does not apply to an IL2CPP/CoreCLR launch).
            string corlibDir = Environment.GetEnvironmentVariable("DOORSTOP_MANAGED_FOLDER_DIR");

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
                    ValidateTarget(target, ours);
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
                // propagate uncaught, matching what happens when Doorstop invokes a preloader
                // directly. Astra's OWN core dir is not on APP_PATHS either (APP_PATHS was fixed, at
                // CoreCLR boot, to "<corlib dir>;<this chain's own dir>") — so the resolver below is
                // needed for the fallback path too, pointed at OUR core instead of theirs.
                string ownPreloader = Path.Combine(Path.Combine(ours, "core"), "BepInEx.Unity.IL2CPP.dll");
                string ownCoreDir = Path.GetDirectoryName(ownPreloader);
                WriteLastRun(chainDir, "fallback", reason);
                Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", ownPreloader);
                if (!string.IsNullOrEmpty(corlibDir))
                    Environment.SetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS", corlibDir + Path.PathSeparator + ownCoreDir);
                AssemblyLoadContext.Default.Resolving += MakeCoreResolver(ownCoreDir);
                MethodInfo ownEntry = FindEntrypoint(ownPreloader);
                InvokeEntrypoint(ownEntry);
                return;
            }

            // Hand the whole process to THEIR preloader. DOORSTOP_INVOKE_DLL_PATH must read back as
            // their path from here on: their own Entrypoint.Start() reads it via EnvVars.LoadVars(),
            // exactly as if their manager's Doorstop had invoked it directly. DOORSTOP_DLL_SEARCH_DIRS
            // is updated the same way (nothing in BE6's own IL2CPP preloader reads it today — Paths.
            // SetExecutablePath is called with no dllSearchPath argument — but setting it to what a
            // direct start would have costs nothing and keeps a future patcher's expectations honest.
            string theirCoreDir = Path.GetDirectoryName(target);
            Environment.SetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH", target);
            if (!string.IsNullOrEmpty(corlibDir))
                Environment.SetEnvironmentVariable("DOORSTOP_DLL_SEARCH_DIRS", corlibDir + Path.PathSeparator + theirCoreDir);
            // CoreCLR-only wrinkle (design §9): APP_PATHS was fixed, once, at coreclr_initialize, to
            // "<corlib dir>;<OUR chain dir>" — never theirs. Without this, every dependency THEIR
            // preloader (or Astra.Chain.Hooks.IL2CPP below) resolves by simple name — BepInEx.Core,
            // 0Harmony, Mono.Cecil, MonoMod.*, Il2CppInterop.* — fails to load.
            AssemblyLoadContext.Default.Resolving += MakeCoreResolver(theirCoreDir);
            // A failure INSIDE their Start() is their BepInEx's own failure mode (it writes its own
            // preloader_*.log, as it would under their manager); we do not catch it and we do not
            // retry with ours, because some of their patchers may already have run. Let it propagate.
            InvokeEntrypoint(theirEntry);

            // Their whole preloader ran and returned. Install Astra's hooks so its plugins are
            // discovered alongside theirs; a failure here is reported but never undoes the above.
            try
            {
                InstallHooks(chainDir, ours, source, profile);
                string theirVersion = ReadProductVersion(Path.Combine(theirCoreDir, "BepInEx.Core.dll"));
                WriteLastRun(chainDir, "ok", string.IsNullOrEmpty(theirVersion) ? "unknown" : theirVersion);
            }
            catch (Exception ex)
            {
                // Astra is simply missing from this session (its plugins were never folded into
                // theirs); the game and the player's own mods are unaffected. This is NOT a
                // fallback — their preloader already ran under their own BepInEx, so this is its
                // own, later status: "hooks-failed", never "fallback" (which means Astra ran its
                // OWN preloader instead, which did not happen here).
                WriteLastRun(chainDir, "hooks-failed", Unwrap(ex).Message);
            }
        }

        // Resolves, by simple name, any assembly that lives directly inside `coreDir` — a flat
        // folder (BepInEx.Core.dll, 0Harmony.dll, Mono.Cecil.dll, MonoMod.*, Il2CppInterop.*,
        // AsmResolver.*, … all sit beside the preloader, never in a subfolder). The ALC already
        // dedupes repeat requests for the same simple name, so no cache is needed here (contrast
        // Astra.Chain.Hooks' Cecil-level cache, which Mono.Cecil does NOT dedupe on its own).
        static Func<AssemblyLoadContext, AssemblyName, Assembly> MakeCoreResolver(string coreDir)
        {
            return (context, name) =>
            {
                string candidate = Path.Combine(coreDir, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };
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

        /// <summary>`target` must be an existing `BepInEx.Unity.IL2CPP.dll` inside a `BepInEx/core`
        /// folder that also holds the rest of be.788's core assemblies — EVERY file name present in
        /// OUR own `<paramref name="ours"/>/core` (the exact be.788 drop
        /// tools/make-runner-il2cpp-gi.sh ships beside chain/, ~35 files) must also exist in theirs,
        /// not just a hand-picked handful: a partial core (e.g. Il2CppInterop.Runtime.dll missing)
        /// used to pass a 5-file gate and then hand off to a preloader that loads nothing. AND their
        /// `BepInEx.Core.dll` must report the EXACT build this chain is built for (design §9:
        /// "allowed only when their BepInEx 6 is the exact build the runner is built for" — BepInEx 6
        /// is pre-1.0 and its internal APIs, which Astra.Chain.Hooks.IL2CPP patches directly, are not
        /// guaranteed stable between be.xxx builds the way BE5's plugin API is across 5.4.x), compared
        /// EXACTLY on the text before '+' (not by prefix: "6.0.0-be.788" must not also accept
        /// "6.0.0-be.7880"). Checked BEFORE their Start() is ever called: a wrong or broken core takes
        /// the real fallback (Astra's own session), not a half-started one. None of these messages say
        /// "chain.ini:" themselves — the one caller that reports them (<see cref="Describe"/>) adds
        /// that prefix once.</summary>
        static void ValidateTarget(string target, string ours)
        {
            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                throw new FileNotFoundException("target does not exist", target);
            if (!string.Equals(Path.GetFileName(target), "BepInEx.Unity.IL2CPP.dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("target is not named BepInEx.Unity.IL2CPP.dll: " + target);
            string coreDir = Path.GetDirectoryName(target);
            if (coreDir == null || !string.Equals(Path.GetFileName(coreDir), "core", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("target is not inside a 'core' folder: " + target);
            string bepinexDir = Path.GetDirectoryName(coreDir);
            if (bepinexDir == null || !string.Equals(Path.GetFileName(bepinexDir), "BepInEx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("target is not inside a 'BepInEx' folder: " + target);
            string ownCoreDir = Path.Combine(ours, "core");
            string[] ownNames = Array.ConvertAll(Directory.GetFiles(ownCoreDir), Path.GetFileName);
            Array.Sort(ownNames, StringComparer.Ordinal); // deterministic "first missing file" below
            foreach (string name in ownNames)
            {
                string path = Path.Combine(coreDir, name);
                if (!File.Exists(path))
                    throw new FileNotFoundException("their core is missing " + name, path);
            }
            string theirVersion = ReadProductVersion(Path.Combine(coreDir, "BepInEx.Core.dll"));
            string theirVersionBase = theirVersion == null ? null : theirVersion.Split('+')[0];
            if (theirVersionBase == null || !string.Equals(theirVersionBase, RequiredBuild, StringComparison.Ordinal))
                throw new InvalidDataException("their BepInEx.Core build is " +
                    (theirVersion ?? "unknown") + ", not " + RequiredBuild +
                    " — Astra's IL2CPP chain is built for that exact build only");
        }

        // The exact build the runner (and Astra.Chain.Hooks.IL2CPP) is compiled against —
        // tools/make-runner-il2cpp-gi.sh's own BEPINEX6_URL/BEPINEX6_SHA256 pin. Compared EXACTLY
        // against the version text before '+' (ValidateTarget) so the trailing "+<commit sha>"
        // never has to be spelled here twice, and so "...788" never matches "...7880".
        const string RequiredBuild = "6.0.0-be.788";

        /// <summary>Loads `assemblyPath` and finds its Doorstop entry method by reflection, so it
        /// binds whatever preloader is actually there (stock, forked, ours) without this assembly
        /// referencing BepInEx at all. Everything in here is "before step 4 invokes": a failure
        /// means fall back. Mirrors Astra.Chain/ChainCore.cs's FindEntrypoint exactly (including its
        /// static-Main fallback, kept for the same defensive reason even though no IL2CPP-era
        /// BepInEx core actually needs it — CoreCLR/IL2CPP support post-dates that convention).</summary>
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
            string hooksPath = Path.Combine(chainDir, "Astra.Chain.Hooks.IL2CPP.dll");
            if (!File.Exists(hooksPath))
                throw new FileNotFoundException("Astra.Chain.Hooks.IL2CPP.dll not found", hooksPath);
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
