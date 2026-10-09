// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// The pipeline ADAPTERS: small assemblies beside this plugin, each compiled against one render
    /// pipeline's API, for the pipelines where her picture must be drawn from INSIDE the camera's
    /// render (its depth exists only there). They are loaded by reflection and only in a game that
    /// runs that pipeline, so this plugin depends on none of them and keeps loading in every game.
    /// Anything that fails to bind leaves the plugin on its own path and says why.
    /// </summary>
    static class Adapters
    {
        /// <summary>A static method of an adapter's type, as a delegate.</summary>
        /// <exception cref="Exception">The adapter is missing or does not bind.</exception>
        public static T Method<T>(string assembly, string type, string method) where T : Delegate
        {
            string dir = Path.GetDirectoryName(typeof(Adapters).Assembly.Location);
            var asm = Assembly.LoadFrom(Path.Combine(dir, assembly + ".dll"));
            var m = asm.GetType(type, true).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                ?? throw new MissingMethodException(type, method);
            return (T)Delegate.CreateDelegate(typeof(T), m);
        }

        public static string PipelineName => Compat.PipelineClass();
    }

    /// <summary>
    /// URP 17 (Unity 6): her pass is enqueued INTO the camera's render every frame
    /// (<c>Astra.Urp17.dll</c>), right after post-processing — inside the render graph (where the
    /// camera's depth texture is a transient that is gone when the camera ends), or, in
    /// Compatibility Mode, through Execute. Either way she is in the picture before the camera's
    /// final blit and before anything the game captures at AfterRendering (a recorder). URP 12–16
    /// draw her after the camera instead.
    /// </summary>
    static class UrpHook
    {
        static Func<Camera, Compositor, bool> enqueue;
        static Func<float?> postExposure;

        /// <summary>Why there is no adapter (null when there is one).</summary>
        public static string Missing { get; private set; } = "not looked for";

        public static bool Active => enqueue != null;

        /// <summary>The player's <c>Picture.BeforePostProcessing</c>: on URP 2022–2023, draw her inside
        /// the camera before its post-processing (set before <see cref="TryLoad"/>).</summary>
        public static bool BeforePost { get; set; } = true;

        /// <summary>Does URP run its render graph (false: Compatibility Mode)? For the log.</summary>
        public static bool RenderGraph { get; private set; }

        /// <summary>The adapter loaded is the URP 2022–2023 one (before post-processing): a queued
        /// pass is checked for having RUN (<c>Driver.RanInGraph</c>).</summary>
        public static bool BeforePostAdapter { get; private set; }

        /// <summary>Drop the adapter for the rest of the run; she is composited after the camera.</summary>
        public static void StandDown(string why)
        {
            enqueue = null;
            Missing = why;
        }

        public static void TryLoad()
        {
            enqueue = null;
            postExposure = null;
            BeforePostAdapter = false;
            if (Adapters.PipelineName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
            {
                Missing = "not a URP game";
                return;
            }
            if (!Application.unityVersion.StartsWith("6000.", StringComparison.Ordinal))
            {
                // URP 13–16 (Unity 2022–2023): her pass goes into the camera's render BEFORE its
                // post-processing, so the game's look (grading, a stylised game's pixelation) is on
                // her too — unless the player keeps her over the finished frame (BeforePostProcessing
                // off), or the adapter does not bind (URP 12 and older: no RTHandle targets), where
                // she is composited after the frame's cameras as before: its depth texture outlives
                // the camera.
                if (!BeforePost || !(Application.unityVersion.StartsWith("2022.", StringComparison.Ordinal) || Application.unityVersion.StartsWith("2023.", StringComparison.Ordinal)))
                {
                    Missing = $"URP on Unity {Application.unityVersion}: drawn over the finished frame (its depth texture outlives the camera)";
                    return;
                }
                try
                {
                    enqueue = Adapters.Method<Func<Camera, Compositor, bool>>("Astra.Urp14", "Astra.Unity.Urp14", "Enqueue");
                    Missing = null;
                    BeforePostAdapter = true;
                }
                catch (Exception e)
                {
                    Missing = $"the URP 14 adapter did not load ({e.GetType().Name}: {e.Message}): drawn over the finished frame";
                }
                return;
            }
            try
            {
                RenderGraph = Adapters.Method<Func<bool>>("Astra.Urp17", "Astra.Unity.Urp17", "RenderGraphOn")();
                enqueue = Adapters.Method<Func<Camera, Compositor, bool>>("Astra.Urp17", "Astra.Unity.Urp17", "Enqueue");
                Missing = null;
                try { postExposure = Adapters.Method<Func<float?>>("Astra.Urp17", "Astra.Unity.Urp17", "PostExposure"); }
                catch (Exception) { postExposure = null; } // an older adapter: light values unconverted
            }
            catch (Exception e)
            {
                Missing = $"the URP 17 adapter did not load ({e.GetType().Name}: {e.Message})";
            }
        }

        /// <summary>Queue her pass on this camera; false (and the adapter dropped) if it fails.</summary>
        public static bool Enqueue(Camera cam, Compositor compositor)
        {
            if (enqueue == null) return false;
            try
            {
                return enqueue(cam, compositor);
            }
            catch (Exception e)
            {
                Missing = $"the URP adapter failed ({e.GetType().Name}: {e.Message})";
                enqueue = null;
                return false;
            }
        }

        /// <summary>URP's <c>2^postExposure</c> multiplier from its active ColorAdjustments volume
        /// override; 1 (no change) when there is none or the adapter cannot read it.</summary>
        public static float PostExposure()
        {
            if (postExposure == null) return 1f;
            try
            {
                float? v = postExposure();
                return v is float f && f > 0 && !float.IsInfinity(f) && !float.IsNaN(f) ? f : 1f;
            }
            catch (Exception)
            {
                postExposure = null;
                return 1f;
            }
        }
    }

    /// <summary>
    /// HDRP: her pass is a custom pass on the main camera, after post-processing, tested against the
    /// camera's depth BUFFER (<c>Astra.Hdrp.dll</c>). Attached once, and again when the main camera
    /// changes.
    /// </summary>
    static class HdrpHook
    {
        static Func<Camera, Compositor, bool> attach;
        static Func<Camera, float> exposure;
        static bool ambient;
        static Func<Camera, (bool enabled, float density, Color color)> fog;
        static Camera attachedTo;

        public static string Missing { get; private set; } = "not looked for";

        public static bool Active => attach != null;

        public static void TryLoad()
        {
            attach = null;
            attachedTo = null;
            if (Adapters.PipelineName != "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset")
            {
                Missing = "not an HDRP game";
                return;
            }
            try
            {
                attach = Adapters.Method<Func<Camera, Compositor, bool>>("Astra.Hdrp", "Astra.Unity.Hdrp", "Attach");
                Missing = null;
                try { exposure = Adapters.Method<Func<Camera, float>>("Astra.Hdrp", "Astra.Unity.Hdrp", "Exposure"); }
                catch (Exception) { exposure = null; } // an older adapter: lights unconverted
                try { Sh.BindHdrp(); ambient = true; }
                catch (Exception) { ambient = false; }
                try { fog = Adapters.Method<Func<Camera, (bool, float, Color)>>("Astra.Hdrp", "Astra.Unity.Hdrp", "ReadFog"); }
                catch (Exception) { fog = null; } // an older adapter: no HDRP fog
            }
            catch (Exception e)
            {
                Missing = $"the HDRP adapter did not load ({e.GetType().Name}: {e.Message})";
            }
        }

        /// <summary>HDRP's current exposure multiplier for <paramref name="cam"/> (its lights are in
        /// physical units, its picture is exposed); 0 while UNKNOWN — no adapter (an IL2CPP game, a
        /// release without it), no readback landed yet. Never a guess: lux taken for display units
        /// turn her white.</summary>
        public static float Exposure(Camera cam)
        {
            if (exposure == null) return 0;
            try
            {
                float e = exposure(cam);
                return e > 0 && !float.IsInfinity(e) && !float.IsNaN(e) ? e : 0;
            }
            catch (Exception)
            {
                exposure = null;
                return 0;
            }
        }

        /// <summary>HDRP's own ambient probe for <paramref name="cam"/> (the sky's, in physical units),
        /// evaluated toward <paramref name="directions"/>; null when the adapter cannot read it.</summary>
        public static Color[] Ambient(Camera cam, Vector3[] directions)
        {
            if (!ambient) return null;
            try
            {
                return Sh.Hdrp(cam, directions);
            }
            catch (Exception)
            {
                ambient = false;
                return null;
            }
        }

        /// <summary>HDRP's active Fog volume for <paramref name="cam"/> — density + the lit haze
        /// colour, already on the picture's scale; <c>enabled</c> false when there is none or the
        /// adapter cannot read it.</summary>
        public static (bool enabled, float density, Color color) Fog(Camera cam)
        {
            if (fog == null) return (false, 0f, Color.black);
            try
            {
                var f = fog(cam);
                return f.enabled && f.density > 0 && !float.IsInfinity(f.density) && !float.IsNaN(f.density) ? f : (false, 0f, Color.black);
            }
            catch (Exception)
            {
                fog = null;
                return (false, 0f, Color.black);
            }
        }

        /// <summary>Make sure her pass is on <paramref name="main"/>; false (and the adapter dropped)
        /// if it fails.</summary>
        public static bool Ensure(Camera main, Compositor compositor)
        {
            if (attach == null) return false;
            if (main == attachedTo) return true;
            try
            {
                attach(main, compositor);
                attachedTo = main;
                return true;
            }
            catch (Exception e)
            {
                Missing = $"the HDRP adapter failed ({e.GetType().Name}: {e.Message})";
                attach = null;
                return false;
            }
        }
    }
}
