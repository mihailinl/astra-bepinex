// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
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
    static partial class Compat
    {
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

        /// <summary>
        /// Does this game's runtime have <typeparamref name="T"/> at all? An IL2CPP build strips every
        /// engine class the game never uses; Il2CppInterop may still put the C# type back in the
        /// interop (MiSide's TerrainCollider), but with no native class behind it every generic call
        /// naming it — <c>TryCast</c>, <c>GetComponent</c>, <c>Il2CppType.Of</c> — throws "is not an
        /// Il2Cpp reference type" (that stopped her in MiSide's first level). No object of a class the
        /// game lacks can exist, so a check for one is simply false. Always true under Mono.
        /// </summary>
        public static bool Exists<T>() where T : UnityEngine.Object
        {
#if IL2CPP
            try
            {
                return Il2CppClassPointerStore<T>.NativeClassPtr != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
#else
            return true;
#endif
        }

        /// <summary><c>GetComponent&lt;T&gt;</c>, null when the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static T Get<T>(GameObject go) where T : Component => go != null && Exists<T>() ? go.GetComponent<T>() : null;

        /// <summary><c>TryCast</c> / <c>as</c>, null when the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static T As<T>(UnityEngine.Object o) where T : UnityEngine.Object
        {
            if (o == null || !Exists<T>()) return null;
#if IL2CPP
            return o.TryCast<T>();
#else
            return o as T;
#endif
        }

        /// <summary>Every loaded object of a type, including inactive ones (filter yourself); none when
        /// the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static List<T> FindAll<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            if (!Exists<T>()) return list;
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

        /// <summary>The failure a call throws in a game whose build STRIPPED a type or member the
        /// called method names (an IL2CPP build removes every engine API the game never uses): the
        /// method fails to compile the first time it runs. Catch it at the CALLER of a method that
        /// names the API — never inside that method, which cannot even start. Il2CppInterop puts
        /// some stripped members back, and two of its stand-ins throw on use: a body it could not
        /// restore ("Method unstripping failed", NotSupportedException) and a native call the player
        /// never registered (a plain Exception: "ICall with signature … was not resolved"). The failure
        /// may come wrapped: in a type initializer's (a static field of a stripped type) or a reflective
        /// call's — the wrapped <see cref="Feature.Cause"/> is judged. And a build that left out a whole
        /// engine module (UnityEngine.PhysicsModule in a game without 3D physics) fails to LOAD its
        /// assembly: a FileNotFoundException naming a "UnityEngine." assembly is stripped too.</summary>
        public static bool Stripped(Exception e)
        {
            e = Feature.Cause(e);
            return e is TypeLoadException || e is MissingMemberException || e is NotSupportedException
                || (e is System.IO.FileNotFoundException f && f.FileName != null
                    && f.FileName.StartsWith("UnityEngine.", StringComparison.Ordinal))
                || (e.GetType() == typeof(Exception) && e.Message.IndexOf("ICall", StringComparison.Ordinal) >= 0);
        }

        /// <summary>Why a call failed, in one line for the log (<see cref="Feature.Describe"/>): the
        /// failure under its wrappers, its type and message, and the method that threw where the message
        /// names nothing ("Method unstripping failed").</summary>
        public static string Why(Exception e) => Feature.Describe(e);

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
            if (root == null || !Exists<Collider>()) return list;
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

        /// <summary>
        /// The one hit buffer every physics query here fills, in a class of its own: Compat's own type
        /// initializer then names no physics type, so a build without 3D physics (no RaycastHit, or no
        /// UnityEngine.PhysicsModule at all) fails only the queries that need it — at their callers,
        /// as <see cref="Stripped"/> — and never poisons HookSrp, HookBuiltIn or LoadShader.
        /// </summary>

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
            return t != null && Exists<RenderTexture>() ? t.TryCast<RenderTexture>() : null;
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
