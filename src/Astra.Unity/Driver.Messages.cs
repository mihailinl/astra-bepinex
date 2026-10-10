// SPDX-License-Identifier: MIT
// A readability shard of Driver: the messages sent to Astra.
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using Astra.Sdk;
using UnityEngine;

namespace Astra.Unity
{
    public sealed partial class Driver
    {
        float nextLightLog;
        string loggedLight;
        readonly HashSet<string> refusalsSaid = new HashSet<string>();

        /// <summary>Her light message for <paramref name="cam"/>, when one is due: what lights her where
        /// she stands (the sun, the game's lamps, the ambient and the fog) — and every 10 s, when it
        /// changed, the line that says so.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        void SendLight(Camera cam)
        {
            // The integration's sun runs under ITS guard: a throw there drops the integration
            // (the scene's sun takes over), never the foundation.
            var choose = integration?.Sun;
            Light sun = choose != null ? Guarded("sun", () => choose(), () => null) : null;
            light.SendFog = link.Supports("light.fog");
            light.Physical = hdrp;
            if (hdrp)
            {
                light.Exposure = HdrpHook.Exposure(cam);
                light.PipelineAmbient ??= directions => HdrpHook.Ambient(main, directions);
                light.PipelineFog ??= () => HdrpHook.Fog(main);
            }
            // URP 17's post-exposure (1 elsewhere, including HDRP: its own exposure already
            // covers it): read once per light message, folded into every value the feed sends.
            light.PostExposure = UrpHook.PostExposure();
            Look? look = integration?.LookFloor is float floor
                ? new Look { Floor = floor, Ceiling = integration.LookCeiling ?? 1.40f }
                : (Look?)null;
            // What she is lit with, every 10 s when it changed: the line a player pastes when
            // she looks wrong. wantSummary (M5) tells Message() to build the interpolated
            // Summary text only when it might actually be logged, not on every call (up to
            // 20 Hz) only for this comparison to throw it away unread the other ~19 times.
            bool wantSummary = Time.unscaledTime > nextLightLog;
            Send(light.Message(her.Position, HerHeight * scale, ignore, choose != null && integration != null, sun,
                cam, frame.Player?.Root, look, wantSummary));
            if (wantSummary)
            {
                nextLightLog = Time.unscaledTime + 10f;
                if (light.Summary != null && light.Summary != loggedLight)
                {
                    log.LogInfo("her light here: " + light.Summary);
                    loggedLight = light.Summary;
                }
            }
        }
        /// <summary>Her shadow, off: the caster is taken out of the scene.</summary>
        void DropShadow()
        {
            shadow?.Dispose();
            shadow = null;
        }
        /// <summary>Send a built message; a builder that refused its input (null) is said once per reason.</summary>
        void Send(string json)
        {
            if (json != null) link.Send(json);
            else if (Messages.Rejection != null) Once("rejected: " + Messages.Rejection, "a message to Astra was not sent: " + Messages.Rejection);
        }
        /// <summary>Send <paramref name="cam"/> as <paramref name="view"/>. A camera the feed refused (an
        /// orthographic one) is said once per reason and per camera. The link is kept: a game that switches
        /// to a menu's camera and back would otherwise bounce her between the desktop and the game.</summary>
        void SendCamera(Camera cam, int view = 0)
        {
            string json = shots.Message(cam, settings.MaxPictureHeight.Value, view);
            if (json == null && shots.Refusal != null)
            {
                // Looked up by the text itself (the feed builds it once per camera and reason): no key is
                // built every frame of an orthographic camera.
                if (refusalsSaid.Add(shots.Refusal)) log.LogInfo(shots.Refusal);
            }
            else Send(json);
        }
        void SendCues(Cues cues)
        {
            foreach (var json in cues.Pending) link.Send(json);
            cues.Pending.Clear();
        }
    }
}
