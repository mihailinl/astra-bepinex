// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using Astra.Bridge;
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
    /// <para>
    /// THE RULE: an adapter's pass runs inside the game's own render, where a member the game's engine
    /// lacks fails as the runtime compiles the method that names it — and the pipeline then drops the
    /// camera's whole render, every frame (0.8.0 in a Unity 6000.3 game: a black screen). So before a
    /// pass goes in, its adapter's essential class is PROVEN in this game (<see cref="Prove"/>: every
    /// method of it compiled, here, inside a try); a class that does not prove is left out, she is
    /// composited the way that needs no adapter, and the log says once which member is missing. A pass
    /// that still fails inside the render catches it there and switches itself off; its hook reads that
    /// before the next frame's pass and stands the adapter down, saying so once.
    /// </para>
    /// </summary>
    static class Adapters
    {
        const BindingFlags Statics = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

        /// <summary>An adapter's type, from its assembly beside this plugin.</summary>
        /// <exception cref="Exception">The adapter is missing or its type does not load.</exception>
        static Type Type(string assembly, string type)
        {
            string dir = Path.GetDirectoryName(typeof(Adapters).Assembly.Location);
            var asm = Assembly.LoadFrom(Path.Combine(dir, assembly + ".dll"));
            return asm.GetType(type, true);
        }

        /// <summary>A static method of an adapter's type, as a delegate.</summary>
        /// <exception cref="Exception">The adapter is missing or does not bind.</exception>
        public static T Method<T>(string assembly, string type, string method) where T : Delegate
        {
            var m = Type(assembly, type).GetMethod(method, Statics) ?? throw new MissingMethodException(type, method);
            return (T)Delegate.CreateDelegate(typeof(T), m);
        }

        /// <summary>
        /// May the adapter's ESSENTIAL class — everything its pass runs — go into this game's render?
        /// Compiles all of it now (<see cref="AdapterProof.Prove"/>), then runs its <c>Bind</c> (where it
        /// has one: what differs between engine lines, looked up once). Null when it may; otherwise why
        /// not, in one line that names the missing member. Never throws.
        /// </summary>
        public static string Prove(string assembly, string type)
        {
            Type t;
            try { t = Type(assembly, type); }
            catch (Exception e) { return $"{assembly}.dll does not load: {Feature.Describe(e)}"; }
            string why = AdapterProof.Prove(t);
            if (why != null) return why;
            try
            {
                var bind = t.GetMethod("Bind", Statics);
                bind?.Invoke(null, null);
                return null;
            }
            catch (Exception e)
            {
                return $"{type.Substring(type.LastIndexOf('.') + 1)}.Bind: {Feature.Describe(e)}";
            }
        }

        public static string PipelineName => Compat.PipelineClass();

        /// <summary>Where a hook says, once, that it stood its adapter down (the Driver's warning).</summary>
        public static Action<string> Warn;
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
        static Func<string> failed;
        static Func<float?> postExposure;

        /// <summary>Why there is no adapter (null when there is one).</summary>
        public static string Missing { get; private set; } = "not looked for";

        /// <summary>This game should have had the adapter's pass and does not: it did not load, or did not
        /// prove (<see cref="Adapters.Prove"/>). The start's log line is then a warning.</summary>
        public static bool Refused { get; private set; }

        public static bool Active => enqueue != null;

        /// <summary>The player's <c>Picture.BeforePostProcessing</c>: on URP 2022–2023, draw her inside
        /// the camera before its post-processing (set before <see cref="TryLoad"/>).</summary>
        public static bool BeforePost { get; set; } = true;

        /// <summary>Does URP run its render graph (false: Compatibility Mode)? For the log.</summary>
        public static bool RenderGraph { get; private set; }

        /// <summary>The adapter loaded is the URP 2022–2023 one (before post-processing): a queued
        /// pass is checked for having RUN (<c>Driver.RanInGraph</c>).</summary>
        public static bool BeforePostAdapter { get; private set; }

        /// <summary>Drop the adapter for the rest of the run, and say so once; she is composited after
        /// the camera.</summary>
        public static void StandDown(string why)
        {
            if (enqueue == null) return;
            enqueue = null;
            failed = null;
            Missing = why;
            Adapters.Warn?.Invoke($"compositing after the camera from now on: {why}");
        }

        /// <summary>The adapter is left out at load: <paramref name="why"/> goes in the start's line.</summary>
        static void Refuse(string why)
        {
            enqueue = null;
            failed = null;
            Missing = why;
            Refused = true;
        }

        public static void TryLoad()
        {
            enqueue = null;
            failed = null;
            postExposure = null;
            Refused = false;
            BeforePostAdapter = false;
            if (Adapters.PipelineName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
            {
                Missing = "not a URP game";
                return;
            }
#if IL2CPP
            // The adapters are compiled against URP's MANAGED assemblies: an IL2CPP game has none (its
            // URP is native code behind interop wrappers), and the IL2CPP package ships no adapter. Said
            // as what it is, never as a broken install. Her pass then cannot go inside the camera's
            // render: she is composited after its URP camera stack, over its overlay cameras (a held
            // item, the hands) — tested only against the base camera's depth.
            Missing = "the IL2CPP flavour has no URP adapter: drawn over the game's URP overlay cameras too";
            return;
#else
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
                if (Load("Astra.Urp14", "Astra.Unity.Urp14", "URP 14")) BeforePostAdapter = true;
                return;
            }
            if (!Load("Astra.Urp17", "Astra.Unity.Urp17", "URP 17")) return;
            // The optional readings, each on its own: one a line lacks costs only itself.
            try { RenderGraph = Adapters.Method<Func<bool>>("Astra.Urp17", "Astra.Unity.Urp17Light", "RenderGraphOn")(); }
            catch (Exception) { RenderGraph = true; } // no setting to read: the render graph is all there is
            try { postExposure = Adapters.Method<Func<float?>>("Astra.Urp17", "Astra.Unity.Urp17Light", "PostExposure"); }
            catch (Exception) { postExposure = null; } // light values unconverted
#endif
        }

        /// <summary>Prove the adapter's essential class in this game and bind its pass; false (and why
        /// in <see cref="Missing"/>) when it is left out.</summary>
        static bool Load(string assembly, string type, string which)
        {
            string why = Adapters.Prove(assembly, type);
            if (why != null)
            {
                Refuse($"the {which} adapter does not bind in this game, so her pass is left out: {why}");
                return false;
            }
            try
            {
                failed = Adapters.Method<Func<string>>(assembly, type, "Failed");
                enqueue = Adapters.Method<Func<Camera, Compositor, bool>>(assembly, type, "Enqueue");
                Missing = null;
                return true;
            }
            catch (Exception e)
            {
                Refuse($"the {which} adapter did not load: {Feature.Describe(e)}");
                return false;
            }
        }

        /// <summary>Queue her pass on this camera; false (and the adapter stood down) once the pass has
        /// switched itself off inside the render, or the queueing fails.</summary>
        public static bool Enqueue(Camera cam, Compositor compositor)
        {
            if (enqueue == null) return false;
            try
            {
                string why = failed();
                if (why != null)
                {
                    StandDown($"her pass failed inside the camera's URP render and switched itself off ({why})");
                    return false;
                }
                return enqueue(cam, compositor);
            }
            catch (Exception e)
            {
                StandDown($"the URP adapter failed ({Feature.Describe(e)})");
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
        static Func<string> failed;
        static Func<Camera, float> exposure;
        static bool ambient;
        static Func<Camera, (bool enabled, float density, Color color)> fog;
        static Camera attachedTo;

        public static string Missing { get; private set; } = "not looked for";

        /// <summary>This HDRP game should have had the adapter's pass and does not (it did not load, or
        /// did not prove): the start's log line is then a warning.</summary>
        public static bool Refused { get; private set; }

        public static bool Active => attach != null;

        public static void TryLoad()
        {
            attach = null;
            failed = null;
            attachedTo = null;
            Refused = false;
            if (Adapters.PipelineName != "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset")
            {
                Missing = "not an HDRP game";
                return;
            }
#if IL2CPP
            // As for URP (UrpHook.TryLoad): no managed HDRP to compile an adapter against, none shipped.
            Missing = "the IL2CPP flavour has no HDRP adapter";
            return;
#else
            string why = Adapters.Prove("Astra.Hdrp", "Astra.Unity.Hdrp");
            if (why != null)
            {
                Missing = $"the HDRP adapter does not bind in this game, so her pass is left out: {why}";
                Refused = true;
                return;
            }
            try
            {
                failed = Adapters.Method<Func<string>>("Astra.Hdrp", "Astra.Unity.Hdrp", "Failed");
                attach = Adapters.Method<Func<Camera, Compositor, bool>>("Astra.Hdrp", "Astra.Unity.Hdrp", "Attach");
                Missing = null;
            }
            catch (Exception e)
            {
                attach = null;
                Missing = $"the HDRP adapter did not load: {Feature.Describe(e)}";
                Refused = true;
                return;
            }
            // The optional readings, each on its own: one an HDRP lacks costs only itself.
            try { exposure = Adapters.Method<Func<Camera, float>>("Astra.Hdrp", "Astra.Unity.HdrpLight", "Exposure"); }
            catch (Exception) { exposure = null; } // lights unconverted
            try { Sh.BindHdrp(); ambient = true; }
            catch (Exception) { ambient = false; }
            try { fog = Adapters.Method<Func<Camera, (bool, float, Color)>>("Astra.Hdrp", "Astra.Unity.HdrpLight", "ReadFog"); }
            catch (Exception) { fog = null; } // no HDRP fog
#endif
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

        /// <summary>Make sure her pass is on <paramref name="main"/>; false (and the adapter stood down,
        /// saying so once) once the pass has switched itself off inside the render, or attaching
        /// fails. She is then composited after the camera, without a depth test: HDRP's depth is a
        /// mip-pyramid atlas only the pass could read.</summary>
        public static bool Ensure(Camera main, Compositor compositor)
        {
            if (attach == null) return false;
            string why;
            try
            {
                why = failed();
                if (why == null)
                {
                    if (main == attachedTo) return true;
                    attach(main, compositor);
                    attachedTo = main;
                    return true;
                }
                why = $"her pass failed inside the camera's HDRP render and switched itself off ({why})";
            }
            catch (Exception e)
            {
                why = $"the HDRP adapter failed ({Feature.Describe(e)})";
            }
            attach = null;
            failed = null;
            Missing = why;
            if (compositor != null) compositor.DepthTest = false;
            Adapters.Warn?.Invoke($"compositing after the camera from now on, over everything: {why}");
            return false;
        }
    }
}
