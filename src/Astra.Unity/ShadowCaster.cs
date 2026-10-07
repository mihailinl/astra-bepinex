// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// Her SHADOW, cast by the game itself. With every picture the engine publishes a coarse copy of
    /// her body in that picture's pose (the protocol's "caster"); this puts it into the game's scene as
    /// a renderer that casts shadows and is never drawn. The game's own pipeline then draws her into
    /// its shadow maps — under every light that casts shadows (a sun, a flashlight, a lamp), with its
    /// own filtering, cascades and distance, under its post-processing, in every camera and mirror.
    /// Nothing here is pipeline-specific: a shadows-only renderer is the same in Built-in, URP and HDRP.
    /// <para>
    /// Its material is BORROWED from the game — a copy of the most common opaque material among the
    /// scene's shadow casters — so its shadow pass is the game's own and fits the game's pipeline; a
    /// pipeline's stock lit shader is the fallback. Both sides of every triangle are drawn (her hair and
    /// skirt are single sheets), each with its own normal for the pipeline's normal bias.
    /// </para>
    /// </summary>
    sealed class ShadowCaster : IDisposable
    {
        GameObject host;
        Mesh mesh;
        MeshRenderer renderer;
        Material material; // our own copy
        bool borrowed;     // of the game's material (else of a stock shader, and the game's is looked for again)
        ulong topology;
        bool haveTopology;
        long seen;
        readonly Compat.VertexArrays arrays = new Compat.VertexArrays();
        float nextLook, lookEvery = 5;
        Camera lookedFor; // a new main camera (a scene change, mostly) looks again at once

        /// <summary>Told (once per kind) what it does.</summary>
        internal Action<string> Note;
        readonly HashSet<string> said = new HashSet<string>();

        void Say(string what)
        {
            if (said.Add(what)) Note?.Invoke(what);
        }

        /// <summary>Before <paramref name="main"/> renders (its shadows are drawn first): take the newest
        /// caster from the main view's frames, or hide hers when <paramref name="show"/> is false or the
        /// engine sends none.</summary>
        public void Update(FrameRing ring, Camera main, bool show)
        {
            if (!show || ring == null)
            {
                Hide();
                return;
            }
            if (!ring.TryLatestOf(0, ref seen, out var f)) return; // nothing newer: keep the last pose
            if (!f.HasCaster)
            {
                Hide();
                return;
            }
            Ensure(main);
            if (material == null) return; // no shader to cast with
            var c = f.Caster;
            int v = c.Vertices, n = 2 * v;
            arrays.Resize(n);
            int[] triangles = null;
            float[] box;
            unsafe
            {
                arrays.Fill((float*)c.Positions, (float*)c.Normals, v, out box);
                if (!haveTopology || c.Topology != topology || mesh.vertexCount != n)
                {
                    // The flip reverses the winding: the front copy is (a, c, b), the back one (a, b, c).
                    ushort* t = (ushort*)c.Indices;
                    triangles = new int[6 * c.Triangles];
                    for (int k = 0; k < c.Triangles; k++)
                    {
                        int a = t[3 * k], b = t[3 * k + 1], d = t[3 * k + 2];
                        if (a >= v || b >= v || d >= v) return; // a torn or lying frame: keep the last
                        triangles[6 * k] = a;
                        triangles[6 * k + 1] = d;
                        triangles[6 * k + 2] = b;
                        triangles[6 * k + 3] = a + v;
                        triangles[6 * k + 4] = b + v;
                        triangles[6 * k + 5] = d + v;
                    }
                }
            }
            if (!ring.StillValid(ref f)) return; // the engine rewrote the slot while we copied
            float loX = box[0], loY = box[1], loZ = box[2];
            float sx = box[3] - loX, sy = box[4] - loY, sz = box[5] - loZ;
            if (!(sx >= 0 && sy >= 0 && sz >= 0 && sx + sy + sz < 1e4f)) return; // NaN or absurd
            arrays.Upload(mesh, triangles);
            if (triangles != null)
            {
                topology = c.Topology;
                haveTopology = true;
            }
            mesh.bounds = new Bounds(new Vector3(loX + 0.5f * sx, loY + 0.5f * sy, loZ + 0.5f * sz), new Vector3(sx, sy, sz));
            host.transform.position = new Vector3((float)c.Origin.X, (float)c.Origin.Y, -(float)c.Origin.Z);
            renderer.enabled = true;
        }

        void Ensure(Camera main)
        {
            if (host == null)
            {
                host = new GameObject("Astra (shadow)") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(host);
                mesh = new Mesh { name = "Astra shadow caster", hideFlags = HideFlags.HideAndDontSave };
                mesh.MarkDynamic();
                host.AddComponent<MeshFilter>().sharedMesh = mesh;
                renderer = host.AddComponent<MeshRenderer>();
                renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = LightProbeUsage.Off;
                renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                renderer.allowOcclusionWhenDynamic = false;
                haveTopology = false;
            }
            // A camera does not draw the shadows of layers it culls: be on one the main camera sees.
            int layer = LayerSeenBy(main);
            if (host.layer != layer) host.layer = layer;
            // A borrowed shader the game has unloaded (a scene change) is looked for again.
            if (material != null && (material.shader == null || !material.shader.isSupported)) borrowed = false;
            if (main != lookedFor)
            {
                lookedFor = main;
                lookEvery = 5;
                nextLook = 0;
            }
            if (Time.unscaledTime < nextLook || (material != null && borrowed)) return;
            var game = Borrow(renderer);
            // A scene still loading may have no shadow casters yet: look again, less and less often.
            lookEvery = game != null ? 5 : Mathf.Min(lookEvery * 2, 60);
            nextLook = Time.unscaledTime + lookEvery;
            if (game == null && material != null) return; // keep the stock one until the game has one
            var made = game != null ? new Material(game) : Stock();
            if (made == null)
            {
                Say("no shader to cast her shadow with: she casts none in this game");
                return;
            }
            made.name = "Astra shadow";
            made.hideFlags = HideFlags.HideAndDontSave;
            // Nothing that cuts holes or moves vertices: her shadow is her silhouette.
            foreach (var k in CutsOrMoves) made.DisableKeyword(k);
            // Her shadow samples nothing; let go of the game's textures so a scene can unload them.
            Compat.ClearTextures(made);
            if (material != null) UnityEngine.Object.Destroy(material);
            material = made;
            borrowed = game != null;
            renderer.sharedMaterial = material;
            Say($"her shadow is cast with {(borrowed ? "the game's" : "the stock")} shader '{material.shader.name}'");
        }

        static int LayerSeenBy(Camera main)
        {
            int seenBy = main != null ? main.cullingMask : ~0;
            if ((seenBy & 1) != 0) return 0; // Default
            for (int l = 0; l < 32; l++)
                if ((seenBy & (1 << l)) != 0) return l;
            return 0;
        }

        /// <summary>The most common opaque material among the scene's enabled shadow casters, whose
        /// shader is not one that moves its vertices (foliage, water) or cuts holes.</summary>
        static readonly string[] CutsOrMoves =
            { "_ALPHATEST_ON", "_VERTEX_DISPLACEMENT", "_TESSELLATION_DISPLACEMENT", "_PIXEL_DISPLACEMENT", "_DEPTHOFFSET_ON" };

        static readonly string[] MovingShaders =
            { "tree", "grass", "foliage", "leaf", "leaves", "vegetation", "wind", "water", "ocean", "particle", "sky", "terrain", "ui/", "sprite", "hidden/" };

        /// <param name="own">Her own caster's renderer — a shadow caster too, never to borrow from.</param>
        static Material Borrow(MeshRenderer own)
        {
            var count = new Dictionary<Shader, int>();
            var example = new Dictionary<Shader, Material>();
            foreach (var r in Compat.FindAll<MeshRenderer>())
            {
                if (r == null || r == own || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                // Things the game DRAWS and that cast: a shadows-only renderer is someone's trick.
                if (r.shadowCastingMode != ShadowCastingMode.On && r.shadowCastingMode != ShadowCastingMode.TwoSided) continue;
                var m = r.sharedMaterial;
                if (m == null || m.shader == null || m.renderQueue >= 2450 || m.IsKeywordEnabled("_ALPHATEST_ON")) continue;
                if (Moves(m.shader.name)) continue;
                count.TryGetValue(m.shader, out int k);
                count[m.shader] = k + 1;
                if (k == 0) example[m.shader] = m;
            }
            Shader best = null;
            int most = 0;
            foreach (var kv in count)
                if (kv.Value > most)
                {
                    best = kv.Key;
                    most = kv.Value;
                }
            return best != null ? example[best] : null;
        }

        static bool Moves(string shader)
        {
            string s = shader.ToLowerInvariant();
            foreach (var word in MovingShaders)
                if (s.Contains(word)) return true;
            return false;
        }

        static Material Stock()
        {
            foreach (var name in new[] { "Universal Render Pipeline/Lit", "HDRP/Lit", "Standard", "Legacy Shaders/Diffuse" })
            {
                var s = Shader.Find(name);
                if (s != null) return new Material(s);
            }
            return null;
        }

        /// <summary>A new session: take the main view's frames from <paramref name="floor"/> on (older ones
        /// are the previous engine's), and hide her shadow until one comes.</summary>
        public void Reset(long floor)
        {
            seen = floor;
            Hide();
        }

        void Hide()
        {
            if (renderer != null && renderer.enabled) renderer.enabled = false;
        }

        public void Dispose()
        {
            if (host != null) UnityEngine.Object.Destroy(host);
            if (mesh != null) UnityEngine.Object.Destroy(mesh);
            if (material != null) UnityEngine.Object.Destroy(material);
            host = null;
            mesh = null;
            material = null;
        }
    }
}
