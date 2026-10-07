// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// Finds the pipeline ADAPTER for URP 17 (Unity 6's render graph, where the camera's depth exists
    /// only inside the graph): <c>Astra.Urp17.dll</c>, beside this plugin, compiled against Unity 6.
    /// It is loaded only in a game that runs URP on Unity 6, by reflection, so this plugin keeps
    /// loading in every other game; anything that fails to bind leaves the plugin on its own
    /// (end-of-camera) path and says why.
    /// </summary>
    static class UrpHook
    {
        static Func<Camera, Compositor, bool> enqueue;

        /// <summary>Why there is no adapter (null when there is one).</summary>
        public static string Missing { get; private set; } = "not looked for";

        public static bool Active => enqueue != null;

        public static void TryLoad()
        {
            enqueue = null;
            var pipeline = GraphicsSettings.currentRenderPipeline;
            if (pipeline == null || pipeline.GetType().FullName != "UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset")
            {
                Missing = "not a URP game";
                return;
            }
            if (!Application.unityVersion.StartsWith("6000.", StringComparison.Ordinal))
            {
                Missing = $"URP on Unity {Application.unityVersion}: its depth texture outlives the camera, no adapter needed";
                return;
            }
            try
            {
                string dir = Path.GetDirectoryName(typeof(UrpHook).Assembly.Location);
                var asm = Assembly.LoadFrom(Path.Combine(dir, "Astra.Urp17.dll"));
                var type = asm.GetType("Astra.Unity.Urp17", true);
                const BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
                if (!(bool)type.GetMethod("RenderGraphOn", any).Invoke(null, null))
                {
                    Missing = "URP 17 in Compatibility Mode (no render graph): its depth texture outlives the camera";
                    return;
                }
                var m = type.GetMethod("Enqueue", any);
                enqueue = (Func<Camera, Compositor, bool>)Delegate.CreateDelegate(typeof(Func<Camera, Compositor, bool>), m);
                Missing = null;
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
                Missing = $"the URP 17 adapter failed ({e.GetType().Name}: {e.Message})";
                enqueue = null;
                return false;
            }
        }
    }
}
