// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Astra.Bridge;
using Astra.Sdk;

namespace Astra.Unity
{
    public sealed partial class Driver
    {
        /// <summary>The integration whose Astra Link this frame runs (its <see cref="GameLink"/>), or null.</summary>
        GameIntegration linked;

        /// <summary>Astra Link is optional: its first failure closes it and the rest goes on.</summary>
        readonly Feature linking = new Feature("Astra Link");

        Action stepLink;

        /// <summary>
        /// Astra Link, the AI's channel (AstraPlugins <c>spec/games/link-v1.md</c>): the integration in
        /// charge gets a connection when Astra wrote a link file for it on Play, and only then (no file,
        /// no connection and no line in the log); its tool calls run here, on the main thread, once a
        /// frame. It does not follow her picture: the bridge's rule B1 is about holding HER, and a Link
        /// session holds nothing of hers. A tool that moves her refuses <c>not_holding</c> while she is not
        /// drawn (<see cref="AstraSdk.Drawn"/>); one that reads the game answers while she is hidden, and
        /// after the drawing chain stopped. Called first in every LateUpdate, before the frame's own fence.
        /// </summary>
        void StepLink() => linking.Run(stepLink ??= StepLinkNow, warn, CloseLink);

        void StepLinkNow()
        {
            if (!ReferenceEquals(linked, integration))
            {
                // A new integration in charge, or none (it faulted): the old one's connection is its own.
                CloseLink();
                linked = integration;
                if (linked != null) OpenLink(linked);
            }
            linked?.Link.Pump();
        }

        void OpenLink(GameIntegration gi)
        {
            var c = LinkClient.TryCreate(ProfileDir(), gi.Id, "astra-unity/" + AstraSdk.Version, out string why);
            if (c == null)
            {
                if (why != null) log.LogWarning(why.StartsWith("Astra Link", StringComparison.Ordinal) ? why : "Astra Link: " + why);
                return;
            }
            // From the client's own threads too: the bridge's log lines already come from its thread.
            c.Log = m => log.LogInfo("link: " + m);
            gi.Link.Attach(c);
            log.LogInfo($"Astra Link for '{gi.Id}' (game {c.Game ?? "?"}): {gi.Link.ToolCount} tool(s) answered here; what is granted is the user's choice in Astra");
        }

        void CloseLink()
        {
            var gi = linked;
            linked = null;
            gi?.Link.Detach();
        }

        /// <summary>
        /// The game's Astra profile, the folder holding Astra's <c>BepInEx</c>, where the launcher writes
        /// the link file (<c>ASTRA_LINK_FILE</c> names it first when Astra started the game itself). With
        /// the player's own mods chained, <c>BepInEx.Paths</c> is THEIR root: Astra's comes from
        /// <c>ASTRA_CHAIN_OWN_ROOT</c>, which the chain sets from the path Doorstop was given.
        /// </summary>
        static string ProfileDir()
        {
            try
            {
                string root = Environment.GetEnvironmentVariable("ASTRA_CHAIN_OWN_ROOT");
                if (string.IsNullOrEmpty(root)) root = BepInEx.Paths.BepInExRootPath;
                if (string.IsNullOrEmpty(root)) return null;
                return Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception e) when (e is ArgumentException || e is PathTooLongException)
            {
                return null;
            }
        }
    }
}
