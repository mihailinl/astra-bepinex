// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
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

        /// <summary>
        /// The vertex arrays her shadow caster fills every frame and hands to its mesh: plain arrays
        /// under Mono; under IL2CPP the il2cpp arrays themselves, made once per size, so a frame
        /// allocates nothing (a managed array would be copied into a new il2cpp one at every hand-over).
        /// Fill them by FIELD (<c>v.x = …</c>): under IL2CPP a <c>Vector3</c> constructor or operator is
        /// a call into the runtime.
        /// </summary>
        public sealed class VertexArrays
        {
#if IL2CPP
            public Il2CppStructArray<Vector3> Positions, Normals;
#else
            public Vector3[] Positions, Normals;
#endif
            public int Length => Positions == null ? 0 : Positions.Length;

            public void Resize(int n)
            {
                if (Length == n) return;
#if IL2CPP
                Positions = new Il2CppStructArray<Vector3>(n);
                Normals = new Il2CppStructArray<Vector3>(n);
#else
                Positions = new Vector3[n];
                Normals = new Vector3[n];
#endif
            }

            /// <summary>
            /// Her caster's <paramref name="v"/> vertices from the ring — glTF's right-handed world to
            /// Unity's left-handed one (z flips), each one twice (the front copy, then the back one
            /// with its normal reversed) — and their bounds. Through ONE pointer per array per frame:
            /// under IL2CPP an indexed element write is several calls into the runtime.
            /// </summary>
            public unsafe void Fill(float* p, float* q, int v, out float[] bounds)
            {
                float loX = float.MaxValue, loY = float.MaxValue, loZ = float.MaxValue;
                float hiX = float.MinValue, hiY = float.MinValue, hiZ = float.MinValue;
#if IL2CPP
                fixed (Vector3* pos = Positions.AsSpan(), nrm = Normals.AsSpan())
#else
                fixed (Vector3* pos = Positions, nrm = Normals)
#endif
                {
                    for (int i = 0; i < v; i++)
                    {
                        float x = p[3 * i], y = p[3 * i + 1], z = -p[3 * i + 2];
                        float nx = q[3 * i], ny = q[3 * i + 1], nz = -q[3 * i + 2];
                        pos[i].x = x; pos[i].y = y; pos[i].z = z;
                        pos[i + v].x = x; pos[i + v].y = y; pos[i + v].z = z;
                        nrm[i].x = nx; nrm[i].y = ny; nrm[i].z = nz;
                        nrm[i + v].x = -nx; nrm[i + v].y = -ny; nrm[i + v].z = -nz;
                        if (x < loX) loX = x;
                        if (y < loY) loY = y;
                        if (z < loZ) loZ = z;
                        if (x > hiX) hiX = x;
                        if (y > hiY) hiY = y;
                        if (z > hiZ) hiZ = z;
                    }
                }
                bounds = new[] { loX, loY, loZ, hiX, hiY, hiZ };
            }

            /// <summary>Into <paramref name="mesh"/>, with its triangles when they changed (null = the same).</summary>
            public void Upload(Mesh mesh, int[] triangles)
            {
                if (triangles != null) mesh.Clear();
                mesh.vertices = Positions;
                mesh.normals = Normals;
                if (triangles != null) mesh.triangles = triangles;
            }
        }

        /// <summary>Ask URP to render this camera's depth texture (her depth test needs it). Through
        /// reflection: the plugin does not depend on URP. Harmless where there is no URP.</summary>
        public static void RequestDepthTexture(Camera cam)
        {
#if !IL2CPP
            foreach (var c in cam.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "UniversalAdditionalCameraData") continue;
                var p = c.GetType().GetProperty("requiresDepthTexture");
                if (p != null && p.CanWrite) p.SetValue(c, true, null);
            }
#endif
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

        static readonly Dictionary<int, bool> castsShadow = new Dictionary<int, bool>();
        // When a NEGATIVE verdict was judged (unscaled time); a POSITIVE one is kept forever and
        // never appears here.
        static readonly Dictionary<int, float> castsShadowNegativeAt = new Dictionary<int, float>();

        /// <summary>
        /// Does something that casts a SHADOW lie along the ray? Only a collider that belongs to a
        /// renderer the game draws shadows from — on its object, under it, on an ancestor up to 3
        /// levels up, or on that ancestor's OTHER direct children (the collider's siblings, and its
        /// parent's siblings, and so on — never a whole subtree past this object's own, and never
        /// past a scene root) — counts, and terrain: a game's invisible colliders — map bounds,
        /// zones, blockers — stop no light. (Lethal Company is full of them: her sun read "hidden"
        /// in open daylight.) A verdict is cached; a NEGATIVE one expires after 5 s (a renderer can
        /// start casting shadows after the game first judges it), a positive one never does.
        /// </summary>
        public static bool Shadowed(Vector3 origin, Vector3 dir, float distance, HashSet<int> ignore)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                var c = h.collider;
                if (c == null || h.distance <= 0) continue;
                int id = c.GetInstanceID();
                if (ignore.Contains(id)) continue;
                if (!castsShadow.TryGetValue(id, out bool casts) ||
                    (!casts && Time.unscaledTime - castsShadowNegativeAt[id] > 5f))
                {
                    if (castsShadow.Count > 4096) { castsShadow.Clear(); castsShadowNegativeAt.Clear(); }
                    castsShadow[id] = casts = CastsShadow(c);
                    if (!casts) castsShadowNegativeAt[id] = Time.unscaledTime;
                }
                if (casts) return true;
            }
            return false;
        }

        /// <summary>Forgets every shadow-caster verdict, positive and negative: a new session may
        /// find different casters where the same collider ids sat before.</summary>
        public static void ClearShadowCache()
        {
            castsShadow.Clear();
            castsShadowNegativeAt.Clear();
        }

        static bool CastsShadow(Collider c)
        {
#if IL2CPP
            if (c.TryCast<TerrainCollider>() != null) return true;
#else
            if (c is TerrainCollider) return true;
#endif
            var go = c.gameObject;
            if (Casts(go.GetComponent<Renderer>())) return true;
#if IL2CPP
            foreach (var o in go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
                if (Casts(o.TryCast<Renderer>())) return true;
#else
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                if (Casts(r)) return true;
#endif
            // Up to 3 ancestors (parent, grandparent, great-grandparent): each one's own renderer,
            // and each one's OTHER direct children (this object's siblings at that level) — never
            // past a scene root (an ancestor with no parent of its own ends the walk).
            var at = c.transform.parent;
            for (int level = 0; level < 3 && at != null; level++)
            {
                if (Casts(at.GetComponent<Renderer>())) return true;
                for (int i = 0; i < at.childCount; i++)
                {
                    var sibling = at.GetChild(i);
                    if (sibling != c.transform && Casts(sibling.GetComponent<Renderer>())) return true;
                }
                at = at.parent;
            }
            return false;
        }

        static bool Casts(Renderer r) => r != null && r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

#if !IL2CPP
        static readonly Dictionary<int, Component> dimmerComponent = new Dictionary<int, Component>();
        static readonly Dictionary<int, PropertyInfo> dimmerProperty = new Dictionary<int, PropertyInfo>();
#endif

        /// <summary>HDRP's own per-light multiplier (<c>HDAdditionalLightData.lightDimmer</c>), read
        /// by reflection so this assembly never references the HDRP package; 1 where there is none (no
        /// HDRP, or the component is absent). Mono only: under IL2CPP a component's native type is
        /// never reachable as this C# property by reflection, so it is always 1 there.</summary>
        public static float LightDimmer(Light l)
        {
#if IL2CPP
            return 1f;
#else
            if (l == null) return 1f;
            int id = l.GetInstanceID();
            if (!dimmerComponent.TryGetValue(id, out var comp))
            {
                foreach (var c in l.GetComponents<Component>())
                {
                    if (c == null || c.GetType().Name != "HDAdditionalLightData") continue;
                    comp = c;
                    break;
                }
                if (dimmerComponent.Count > 4096) { dimmerComponent.Clear(); dimmerProperty.Clear(); }
                dimmerComponent[id] = comp; // null cached too: no HDAdditionalLightData on this light
            }
            if (comp == null) return 1f;
            if (!dimmerProperty.TryGetValue(id, out var prop))
            {
                prop = comp.GetType().GetProperty("lightDimmer");
                dimmerProperty[id] = prop;
            }
            return prop != null && prop.PropertyType == typeof(float) ? (float)prop.GetValue(comp, null) : 1f;
#endif
        }

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

        /// <summary>A light probe's light on a surface facing each of <paramref name="directions"/>
        /// (what the game's own shaders get from it).</summary>
        public static Color[] EvaluateSh(SphericalHarmonicsL2 sh, Vector3[] directions)
        {
#if IL2CPP
            var dirs = new Il2CppStructArray<Vector3>(directions.Length);
            var cols = new Il2CppStructArray<Color>(directions.Length);
            for (int i = 0; i < directions.Length; i++) dirs[i] = directions[i];
            sh.Evaluate(dirs, cols);
            var result = new Color[directions.Length];
            for (int i = 0; i < result.Length; i++) result[i] = cols[i];
            return result;
#else
            var cols = new Color[directions.Length];
            sh.Evaluate(directions, cols);
            return cols;
#endif
        }

        /// <summary>The active render pipeline asset's class, by its NATIVE type: under IL2CPP a wrapper's
        /// C# type is the property's declared one (<c>RenderPipelineAsset</c>), never HDRP's or URP's.
        /// Null in the Built-in pipeline.</summary>
        public static string PipelineClass()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return null;
#if IL2CPP
            return asset.GetIl2CppType().FullName;
#else
            return asset.GetType().FullName;
#endif
        }

        /// <summary><paramref name="t"/> as a render texture, or null (an IL2CPP wrapper is never a
        /// C# <c>RenderTexture</c> by a type test).</summary>
        public static RenderTexture AsRenderTexture(Texture t)
        {
#if IL2CPP
            return t?.TryCast<RenderTexture>();
#else
            return t as RenderTexture;
#endif
        }

        /// <summary>Let go of every texture <paramref name="m"/> holds (a copy of a game's material that
        /// samples none of them, so a scene's unload is not held up by it).</summary>
        public static void ClearTextures(Material m)
        {
            foreach (int id in m.GetTexturePropertyNameIDs()) m.SetTexture(id, null);
        }
    }
}
