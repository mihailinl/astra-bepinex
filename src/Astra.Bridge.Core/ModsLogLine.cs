// SPDX-License-Identifier: MIT
using System;
using System.IO;

namespace Astra.Bridge
{
    /// <summary>The player's-own-mods design (gi-bugs/unity-mods-design.md §4.4): one line a
    /// chained session logs into THEIR LogOutput.log (where their mod manager's users look) when
    /// the plugin's own install folder is not the BepInEx root Doorstop actually started under —
    /// the chain loader handed the process to the PLAYER's own BepInEx instead of Astra's own.
    /// Shared by both backends (<c>Astra.BepInEx5</c>/Mono and <c>Astra.BepInEx6.IL2CPP</c>) so the
    /// line reads identically under either — each plugin reads its own two roots (BE5/BE6 share
    /// <c>BepInEx.Paths</c>'s shape, nothing here needs to differ) and its own plugin count
    /// (BE5: <c>BepInEx.Bootstrap.Chainloader.PluginInfos.Count</c>; BE6/IL2CPP:
    /// <c>IL2CPPChainloader.Instance.Plugins.Count</c> — a ONE-TIME snapshot taken at <c>Load()</c>
    /// /<c>Awake()</c>, never a subscription, so neither backend's chainloader TYPE needs to be
    /// named here) and hands both to this type, which has no BepInEx or Unity dependency at all.</summary>
    public static class ModsLogLine
    {
        /// <summary>True only when <paramref name="ownRoot"/> is set (the chain loader ran) AND it
        /// differs from <paramref name="theirRoot"/> (ignoring trailing separators and case). An
        /// ordinary standalone session (today's, unchained) has <paramref name="ownRoot"/>
        /// null/empty and returns false without comparing anything.</summary>
        public static bool ShouldLog(string ownRoot, string theirRoot)
        {
            if (string.IsNullOrEmpty(ownRoot) || theirRoot == null)
                return false;
            return !SamePath(ownRoot, theirRoot);
        }

        /// <summary>The line itself, identical in shape across both backends:
        /// <c>Astra &lt;ver&gt; with your mods: &lt;source&gt; «&lt;profile&gt;», BepInEx
        /// &lt;their version&gt;, &lt;N&gt; plugins</c>.</summary>
        public static string Format(string pluginVersion, string source, string profile,
            string bepinexVersion, int pluginCount)
        {
            return "Astra " + pluginVersion + " with your mods: " + (source ?? "unknown") +
                " «" + (profile ?? "unknown") + "», BepInEx " + (bepinexVersion ?? "unknown") +
                ", " + pluginCount + " plugins";
        }

        static bool SamePath(string a, string b)
        {
            char[] trim = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(trim),
                Path.GetFullPath(b).TrimEnd(trim),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
