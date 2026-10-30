// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Unity.IL2CPP;

namespace Doorstop
{
    /// <summary>Doorstop's CoreCLR host ABI (same convention as Astra.Chain.CoreCLR/Entrypoint.cs),
    /// used ONLY by tools/test-chain-il2cpp.sh to exercise Astra.Chain.Hooks.IL2CPP's merge logic
    /// under the real Doorstop + real be.788 runtime (see this project's own header for why).
    /// `oursBepInExRoot` is `<dir of self>/../BepInEx` and `gameExePath` is
    /// `<dir of self>/../fakegame/game.exe` — both sibling to this dll's OWN scratch root, entirely
    /// separate from wherever the real testbed executable Doorstop actually injected into lives (we
    /// are invoked AFTER IL2CPP's own native init already completed there, so the real game's own
    /// files are irrelevant to us — this test never touches any IL2CPP-specific API, and using our
    /// own scratch "their" plugins dir here, rather than the shared testbed's own BepInEx folder,
    /// keeps this test from ever touching that shared, reused fixture).</summary>
    internal static class Entrypoint
    {
        public static void Start()
        {
            string self = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
            string selfDir = Path.GetDirectoryName(self);
            string scratchRoot = Path.GetFullPath(Path.Combine(selfDir, ".."));
            string oursBepInExRoot = Path.Combine(scratchRoot, "BepInEx");
            string gameExePath = Path.Combine(scratchRoot, "fakegame", "game.exe");
            string resultPath = Path.Combine(selfDir, "merge-result.txt");

            // Same ordering requirement as Astra.Chain.ChainCore's real flow: Paths.
            // SetExecutablePath (inside Initialize) must run BEFORE anything touches TypeLoader or
            // ConfigFile for the first time (TypeLoader's static ctor reads ConfigFile.CoreConfig,
            // which needs Paths.BepInExConfigPath already set).
            TestChainloader chainloader;
            try
            {
                chainloader = new TestChainloader();
                chainloader.Initialize(gameExePath);
            }
            catch (Exception ex)
            {
                WriteResult(resultPath, "init-failed", ex.Message);
                return;
            }

            try
            {
                Astra.Chain.Hooks.Install(oursBepInExRoot, "manual", "mergetest");
            }
            catch (Exception ex)
            {
                WriteResult(resultPath, "install-failed", Unwrap(ex).Message);
                return;
            }

            try
            {
                chainloader.Execute();
                WriteResult(resultPath, "ok", "");
            }
            catch (Exception ex)
            {
                WriteResult(resultPath, "execute-failed", Unwrap(ex).Message);
            }
        }

        static Exception Unwrap(Exception ex)
        {
            var tie = ex as TargetInvocationException;
            return tie != null && tie.InnerException != null ? tie.InnerException : ex;
        }

        static void WriteResult(string path, string status, string detail)
        {
            try
            {
                File.WriteAllText(path, status + " " + detail + "\n", new UTF8Encoding(false));
            }
            catch
            {
                // A log line we cannot write is not a reason to fail the test entry itself.
            }
        }
    }

    sealed class TestChainloader : BaseChainloader<BasePlugin>
    {
        // Mirrors IL2CPPChainloader.LoadPlugin exactly (minus its PluginLoad event): construct,
        // THEN call the plugin's own Load() — the actual behaviour under test (Config.Bind, the
        // config redirect's real on-disk write, Logger output) lives in Load(), never the ctor.
        public override BasePlugin LoadPlugin(PluginInfo pluginInfo, Assembly pluginAssembly)
        {
            var type = pluginAssembly.GetType(pluginInfo.TypeName);
            var instance = (BasePlugin)Activator.CreateInstance(type);
            instance.Load();
            return instance;
        }
    }
}
