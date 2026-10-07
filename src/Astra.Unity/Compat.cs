// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace Astra.Unity
{
    /// <summary>
    /// EVERY place the Mono and IL2CPP builds differ, and nothing else: delegates handed to Unity
    /// (an IL2CPP game takes its own delegate types), arrays passed to and from Unity, asset loads by
    /// type. The rest of the plugin is written once against UnityEngine and compiles for both.
    /// </summary>
    static class Compat
    {
        static readonly List<Action> unhook = new List<Action>();

        /// <summary>Called for each camera as the SRP starts / finishes rendering it.</summary>
        public static void HookSrp(Action<Camera> begin, Action<Camera> end)
        {
#if IL2CPP
            var b = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>((_, cam) => begin(cam)));
            var e = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Camera>>(
                new Action<ScriptableRenderContext, Camera>((_, cam) => end(cam)));
            RenderPipelineManager.add_beginCameraRendering(b);
            RenderPipelineManager.add_endCameraRendering(e);
            unhook.Add(() => RenderPipelineManager.remove_beginCameraRendering(b));
            unhook.Add(() => RenderPipelineManager.remove_endCameraRendering(e));
#else
            Action<ScriptableRenderContext, Camera> b = (_, cam) => begin(cam);
            Action<ScriptableRenderContext, Camera> e = (_, cam) => end(cam);
            RenderPipelineManager.beginCameraRendering += b;
            RenderPipelineManager.endCameraRendering += e;
            unhook.Add(() => RenderPipelineManager.beginCameraRendering -= b);
            unhook.Add(() => RenderPipelineManager.endCameraRendering -= e);
#endif
        }

        /// <summary>
        /// Called when the SRP has finished a whole render CONTEXT — every camera, the stack's final
        /// blit included, before the overlay UI. False on a Unity without the event (before 2021.1):
        /// the caller then draws at the end of its camera instead.
        /// </summary>
        public static bool HookSrpContextEnd(Action end)
        {
            try
            {
                HookContextEnd(end);
                return true;
            }
            catch (Exception e) when (e is MissingMethodException || e is MissingMemberException || e is TypeLoadException)
            {
                return false;
            }
        }

        // Its own method, so a Unity without endContextRendering fails when THIS is compiled, inside
        // the caller's try.
        static void HookContextEnd(Action end)
        {
#if IL2CPP
            var e = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>>(
                new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, __) => end()));
            RenderPipelineManager.add_endContextRendering(e);
            unhook.Add(() => RenderPipelineManager.remove_endContextRendering(e));
#else
            Action<ScriptableRenderContext, List<Camera>> e = (_, __) => end();
            RenderPipelineManager.endContextRendering += e;
            unhook.Add(() => RenderPipelineManager.endContextRendering -= e);
#endif
        }

        /// <summary>The Built-in pipeline's per-camera callback before culling.</summary>
        public static void HookBuiltIn(Action<Camera> preCull)
        {
#if IL2CPP
            var d = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(preCull));
            Camera.onPreCull = Il2CppSystem.Delegate.Combine(Camera.onPreCull, d).Cast<Camera.CameraCallback>();
            unhook.Add(() => Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, d)?.TryCast<Camera.CameraCallback>());
#else
            Camera.CameraCallback d = cam => preCull(cam);
            Camera.onPreCull += d;
            unhook.Add(() => Camera.onPreCull -= d);
#endif
        }

        public static void UnhookAll()
        {
            foreach (var u in unhook)
            {
                try { u(); }
                catch (Exception) { /* the game is shutting down */ }
            }
            unhook.Clear();
        }

        /// <summary>Load a bundle from memory and find the shader NAMED <paramref name="shaderName"/>
        /// in it (by the shader's own name, not the asset path: paths are lower-cased in a bundle).
        /// Null with <paramref name="why"/> when it cannot.</summary>
        public static Shader LoadShader(byte[] bundleBytes, string shaderName, out AssetBundle bundle, out string why)
        {
#if IL2CPP
            bundle = AssetBundle.LoadFromMemory(new Il2CppStructArray<byte>(bundleBytes));
#else
            bundle = AssetBundle.LoadFromMemory(bundleBytes);
#endif
            if (bundle == null)
            {
                why = "Unity refused the bundle (see the player log: built for another Unity line or platform?)";
                return null;
            }
            var names = new List<string>();
#if IL2CPP
            foreach (var o in bundle.LoadAllAssets(Il2CppType.Of<Shader>()))
            {
                var sh = o.TryCast<Shader>();
#else
            foreach (var sh in bundle.LoadAllAssets<Shader>())
            {
#endif
                if (sh == null) continue;
                if (sh.name == shaderName)
                {
                    why = null;
                    return sh;
                }
                names.Add(sh.name);
            }
            why = $"no shader '{shaderName}' in the bundle (it has: {string.Join(", ", names)})";
            return null;
        }

        /// <summary>Every loaded object of a type, including inactive ones (filter yourself).</summary>
        public static List<T> FindAll<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
#if IL2CPP
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
            {
                var t = o.TryCast<T>();
                if (t != null) list.Add(t);
            }
#else
            list.AddRange(Resources.FindObjectsOfTypeAll<T>());
#endif
            return list;
        }

        public static List<GameObject> WithTag(string tag)
        {
            var list = new List<GameObject>();
            try
            {
                foreach (var g in GameObject.FindGameObjectsWithTag(tag)) list.Add(g);
            }
            catch (Exception)
            {
                // The game never defined this tag (a UnityException — under IL2CPP an Il2CppException).
            }
            return list;
        }

        public static List<Collider> CollidersUnder(GameObject root)
        {
            var list = new List<Collider>();
#if IL2CPP
            foreach (var c in root.GetComponentsInChildren(Il2CppType.Of<Collider>(), true))
            {
                var t = c.TryCast<Collider>();
                if (t != null) list.Add(t);
            }
#else
            list.AddRange(root.GetComponentsInChildren<Collider>(true));
#endif
            return list;
        }

#if IL2CPP
        static readonly Il2CppStructArray<RaycastHit> hits = new Il2CppStructArray<RaycastHit>(32);
#else
        static readonly RaycastHit[] hits = new RaycastHit[32];
#endif

        /// <summary>The nearest hit along a ray, skipping colliders in <paramref name="ignore"/> and triggers.</summary>
        public static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        /// <summary>The nearest hit of a capsule swept along <paramref name="dir"/>, skipping <paramref name="ignore"/>.</summary>
        public static bool CapsuleCast(Vector3 p1, Vector3 p2, float radius, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.CapsuleCastNonAlloc(p1, p2, radius, dir, hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        static bool Nearest(int n, HashSet<int> ignore, out RaycastHit nearest)
        {
            nearest = default;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                var c = h.collider;
                if (c == null || ignore.Contains(c.GetInstanceID())) continue;
                // A cast that starts inside a collider reports it at distance 0 with no point: that is
                // her standing in the geometry, not something ahead of her.
                if (h.distance <= 0f && h.point == Vector3.zero) continue;
                if (found && h.distance >= nearest.distance) continue;
                nearest = h;
                found = true;
            }
            return found;
        }

        /// <summary>The ambient light around a point, from the scene's spherical-harmonics probe:
        /// the average of straight up and the horizon.</summary>
        public static Color Ambient()
        {
            var sh = RenderSettings.ambientProbe;
#if IL2CPP
            var dirs = new Il2CppStructArray<Vector3>(2);
            var cols = new Il2CppStructArray<Color>(2);
#else
            var dirs = new Vector3[2];
            var cols = new Color[2];
#endif
            dirs[0] = Vector3.up;
            dirs[1] = Vector3.forward;
            sh.Evaluate(dirs, cols);
            return (cols[0] + cols[1]) * 0.5f;
        }
    }
}
