// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using Astra.Sdk;
using BepInEx.Logging;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The "her:" diagnostic line: where she stands for the camera and whether she is drawn — checked
    /// every 10 s, said when it changed. It answers "I don't see her" (behind you? off screen? pictures
    /// arrive but none is drawn? no picture from Astra at all?). It is OPTIONAL: the Driver runs it
    /// through a <see cref="Astra.Bridge.Feature"/>, so a game that cannot answer it loses the line,
    /// never her.
    /// </summary>
    sealed class WhereLine
    {
        float next;
        string last;
        int drawnThen, picturesThen;

        /// <summary>Ten seconds have passed since the last <see cref="Check"/>.</summary>
        public bool Due => Time.unscaledTime > next;

        /// <summary>Check where <paramref name="her"/> stands for <paramref name="cam"/> (aimed at the
        /// middle of her <paramref name="height"/>, in the game's units) and how often she was drawn in
        /// the last 10 s, and log the line when its state changed.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Check(Camera cam, Her her, float height, Compositor compositor, ManualLogSource log)
        {
            next = Time.unscaledTime + 10f;
            if (compositor == null) return;
            int drawn = compositor.Drawn - drawnThen, pictures = compositor.Pictures - picturesThen;
            drawnThen = compositor.Drawn;
            picturesThen = compositor.Pictures;
            string state, where;
            if (!her.Placed)
            {
                state = "unplaced";
                where = "not placed yet";
            }
            else
            {
                var p = cam.WorldToViewportPoint(her.Position + Vector3.up * (height * 0.5f));
                float d = Vector3.Distance(cam.transform.position, her.Position);
                state = p.z <= 0 ? "behind" : p.x < 0 || p.x > 1 || p.y < 0 || p.y > 1 ? "off" : "on";
                where = (state == "behind" ? "behind the camera" : state == "off" ? $"off screen ({p.x:0.##}, {p.y:0.##})" : $"on screen ({p.x:0.##}, {p.y:0.##})")
                    + $", {d:0.#} m from '{cam.name}'";
            }
            float cover = compositor.Coverage;
            state += (drawn > 0 ? "+drawn" : "") + (pictures > 0 ? "+pictures" : "") + (compositor.Show ? "" : "+hidden")
                + (cover == 0 ? "+empty" : "");
            if (state == last) return;
            last = state;
            log.LogInfo($"her: {where}; in 10 s drawn {drawn} times from {pictures} new pictures" +
                (cover >= 0 ? $", her picture {cover * 100:0.#}% covered" : "") +
                (!compositor.Show ? " (hidden: toggled off, or Astra not connected)"
                    : pictures == 0 ? " (no picture from Astra)"
                    : drawn == 0 ? " (pictures arrive, none is drawn)"
                    : cover == 0 ? " (Astra's picture is empty)" : ""));
        }
    }
}
