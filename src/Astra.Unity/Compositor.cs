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
    /// Puts her picture into the game's: pulls the newest frame out of the ring into two textures
    /// (her colour, her depth) and draws one full-screen pass over the finished camera image — after
    /// the game's post-processing, because her picture is already display-ready — that tests her
    /// depth against the game's both ways and reprojects her from the camera she was rendered for to
    /// the one presenting now (the shader, <c>AstraComposite.shader</c>, says how).
    /// </summary>
    public sealed class Compositor : IDisposable
    {
        const string ShaderName = "Hidden/Astra/Composite";

        readonly Material mat;
        readonly AssetBundle bundle;
        readonly CommandBuffer cmd = new CommandBuffer { name = "Astra" };
        Texture2D colour, depth;
        RingFrame shown;
        bool haveFrame;
        Camera attached; // the Built-in camera our command buffer is on
        long seen;
        CameraFeed shots;
        Settings settings;

        /// <summary>The ring frames come from (null between sessions).</summary>
        public FrameRing Ring { get; set; }

        /// <summary>Whether she is shown at all (the toggle, General.Enabled, and a live link).</summary>
        public bool Show { get; set; } = true;

        /// <summary>False in a pipeline whose depth texture this pass cannot read as it is (HDRP):
        /// she is then drawn over everything rather than hidden at random.</summary>
        public bool DepthTest { get; set; } = true;

        /// <summary>The material her pass draws with (for a pass a pipeline adapter records).</summary>
        public Material Material => mat;

        static readonly int IdColour = Shader.PropertyToID("_AstraColour");
        static readonly int IdDepth = Shader.PropertyToID("_AstraDepth");
        static readonly int IdSize = Shader.PropertyToID("_AstraSize");
        static readonly int IdTanNow = Shader.PropertyToID("_AstraTanNow");
        static readonly int IdTanThen = Shader.PropertyToID("_AstraTanThen");
        static readonly int IdRot0 = Shader.PropertyToID("_AstraRot0");
        static readonly int IdRot1 = Shader.PropertyToID("_AstraRot1");
        static readonly int IdRot2 = Shader.PropertyToID("_AstraRot2");
        static readonly int IdEyeThen = Shader.PropertyToID("_AstraEyeThen");
        static readonly int IdZParams = Shader.PropertyToID("_AstraZParams");
        static readonly int IdTest = Shader.PropertyToID("_AstraTest");
        static readonly int IdOut = Shader.PropertyToID("_AstraOut");
        static readonly int IdGameDepth = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>Why the compositor could not be made (null when it was).</summary>
        public static string Failure { get; private set; }

        Compositor(Material mat, AssetBundle bundle)
        {
            this.mat = mat;
            this.bundle = bundle;
        }

        /// <summary>What every draw reads: the cameras sent (to reproject from) and the settings.</summary>
        internal void Bind(CameraFeed shots, Settings settings)
        {
            this.shots = shots;
            this.settings = settings;
        }

        /// <summary>Load the shader bundle built for this game's Unity line and platform.</summary>
        public static Compositor Create()
        {
            Failure = null;
            string platform = Application.platform == RuntimePlatform.WindowsPlayer ? "windows"
                : Application.platform == RuntimePlatform.LinuxPlayer ? "linux" : null;
            if (platform == null) return Fail($"no shaders for {Application.platform} yet");
            int major = Major(Application.unityVersion);
            // The newest bundle not newer than the game's Unity: a bundle loads in its own line and later ones.
            var asm = Assembly.GetExecutingAssembly();
            string best = null;
            int bestMajor = 0;
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("astra-", StringComparison.Ordinal) || !res.EndsWith("-" + platform + ".bundle", StringComparison.Ordinal)) continue;
                int m = Major(res.Substring(6));
                if (m <= major && m > bestMajor) { best = res; bestMajor = m; }
            }
            if (best == null) return Fail($"no shader bundle for Unity {Application.unityVersion} on {platform}");
            byte[] bytes;
            using (var s = asm.GetManifestResourceStream(best))
            using (var m = new MemoryStream())
            {
                s.CopyTo(m);
                bytes = m.ToArray();
            }
            var shader = Compat.LoadShader(bytes, ShaderName, out var bundle, out string why);
            if (shader == null)
            {
                if (bundle != null) bundle.Unload(true);
                return Fail($"{best} in Unity {Application.unityVersion}: {why}");
            }
            if (!shader.isSupported) return Fail($"the composite shader is not supported on {SystemInfo.graphicsDeviceType}");
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return new Compositor(mat, bundle);
        }

        static Compositor Fail(string why)
        {
            Failure = why;
            return null;
        }

        static int Major(string version)
        {
            int n = 0;
            foreach (char c in version)
            {
                if (c < '0' || c > '9') break;
                n = n * 10 + (c - '0');
            }
            return n;
        }

        /// <summary>A new session: forget the old ring's frames.</summary>
        public void Reset()
        {
            haveFrame = false;
            seen = 0;
        }

        /// <summary>Take the newest frame from <paramref name="ring"/>, if there is one, into the textures.</summary>
        public void Pull(FrameRing ring)
        {
            if (ring == null || !ring.TryLatest(ref seen, out var f)) return;
            if (f.Width < 1 || f.Height < 1)
            {
                haveFrame = false;
                return;
            }
            if (colour == null || colour.width != f.Width || colour.height != f.Height)
            {
                Release();
                colour = new Texture2D(f.Width, f.Height, TextureFormat.RGBA32, false, true)
                {
                    name = "Astra colour", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
                depth = new Texture2D(f.Width, f.Height, TextureFormat.RFloat, false, true)
                {
                    name = "Astra depth", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
                };
            }
            colour.LoadRawTextureData(f.Colour, f.PlaneBytes);
            depth.LoadRawTextureData(f.Depth, f.PlaneBytes);
            if (!ring.StillValid(ref f))
            {
                // The engine reused the slot while we copied: this copy is torn. Show nothing this
                // frame rather than half of two; the next pull takes a whole one.
                haveFrame = false;
                return;
            }
            colour.Apply(false, false);
            depth.Apply(false, false);
            shown = f;
            haveFrame = true;
        }

        /// <summary>
        /// SRP: draw her over <paramref name="cam"/>'s finished image, now (call when the camera has
        /// finished rendering — its post-processing included).
        /// </summary>
        public void DrawNow(Camera cam)
        {
            if (!Show) return;
            Pull(Ring);
            if (!Prepare(cam, srp: true, cam.targetTexture != null, DepthTest && Shader.GetGlobalTexture(IdGameDepth) != null)) return;
            cmd.Clear();
            if (cam.targetTexture != null) cmd.SetRenderTarget(cam.targetTexture);
            else cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        /// <summary>
        /// The Built-in pipeline: <paramref name="cam"/> draws her itself, from a command buffer it runs
        /// after its image effects (attached once; the game's depth texture is switched on for it).
        /// Call before it renders (pre-cull): this sets what that pass draws, or hides it.
        /// </summary>
        public void PrepareBuiltIn(Camera cam)
        {
            Pull(Ring);
            if (attached != cam)
            {
                DetachBuiltIn();
                cam.depthTextureMode |= DepthTextureMode.Depth;
                cmd.Clear();
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3);
                cam.AddCommandBuffer(CameraEvent.AfterImageEffects, cmd);
                attached = cam;
            }
            // Built-in binds the game's depth during the camera's own render (we switched it on).
            if (!Show || !Prepare(cam, srp: false, cam.targetTexture != null, depthTest: true))
                mat.SetVector(IdOut, Vector4.zero); // opacity 0: the pass discards every pixel
        }

        void DetachBuiltIn()
        {
            if (attached != null) attached.RemoveCommandBuffer(CameraEvent.AfterImageEffects, cmd);
            attached = null;
        }

        /// <summary>
        /// For a pass a pipeline adapter records INSIDE the camera's render (URP 17's render graph):
        /// take the newest frame and set everything the pass reads. The adapter then draws
        /// <see cref="Material"/> with the game's depth texture bound as <c>_CameraDepthTexture</c>
        /// in its own property block. False when there is nothing to draw.
        /// </summary>
        /// <param name="cam">The camera being rendered.</param>
        /// <param name="intoTexture">The pass draws into a render texture (not the back buffer).</param>
        public bool PrepareForPass(Camera cam, bool intoTexture)
        {
            if (!Show) return false;
            Pull(Ring);
            return Prepare(cam, srp: true, intoTexture, depthTest: true);
        }

        /// <summary>Set everything the pass reads for <paramref name="cam"/>; false when there is nothing to show.</summary>
        bool Prepare(Camera cam, bool srp, bool intoTexture, bool depthTest)
        {
            var s = settings;
            if (Ring == null || !haveFrame || !shots.Find(shown.HostFrame, out var then)) return false;

            var t = cam.transform;
            Vector3 right = t.right, up = t.up, fwd = t.forward;
            float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            // Rows of the rotation from the present view to the one she was drawn in, and her eye then.
            var eye = then.Pos - t.position;
            mat.SetVector(IdRot0, new Vector4(Vector3.Dot(then.Right, right), Vector3.Dot(then.Right, up), Vector3.Dot(then.Right, fwd), 0));
            mat.SetVector(IdRot1, new Vector4(Vector3.Dot(then.Up, right), Vector3.Dot(then.Up, up), Vector3.Dot(then.Up, fwd), 0));
            mat.SetVector(IdRot2, new Vector4(Vector3.Dot(then.Fwd, right), Vector3.Dot(then.Fwd, up), Vector3.Dot(then.Fwd, fwd), 0));
            mat.SetVector(IdEyeThen, new Vector4(Vector3.Dot(eye, right), Vector3.Dot(eye, up), Vector3.Dot(eye, fwd), 0));
            mat.SetVector(IdTanNow, new Vector4(tanY * cam.aspect, tanY, 0, 0));
            mat.SetVector(IdTanThen, new Vector4(then.TanX, then.TanY, 0, 0));
            mat.SetVector(IdSize, new Vector4(colour.width, colour.height, 1f / colour.width, 1f / colour.height));
            mat.SetTexture(IdColour, colour);
            mat.SetTexture(IdDepth, depth);

            mat.SetVector(IdZParams, ZParams(cam.nearClipPlane, cam.farClipPlane));
            mat.SetVector(IdTest, new Vector4(depthTest ? 1 : 0, s.DepthBias.Value, s.DepthSoftness.Value, 0));
            // Which way up the target is: an SRP's pass gets it worked out here; inside a
            // Built-in camera, 0 = "ask _ProjectionParams", which Unity sets for that target.
            float flip = !srp ? 0 : GL.GetGPUProjectionMatrix(Matrix4x4.identity, intoTexture).m11 < 0 ? -1 : 1;
            mat.SetVector(IdOut, new Vector4(flip, WriteLinear(s) ? 1 : 0, 1, 0));
            return true;
        }


        static bool WriteLinear(Settings s)
        {
            switch ((s.ColourSpace.Value ?? "auto").Trim().ToLowerInvariant())
            {
                case "linear": return true;
                case "gamma": return false;
                default: return QualitySettings.activeColorSpace == ColorSpace.Linear;
            }
        }

        /// <summary>Unity's <c>_ZBufferParams</c> for a camera: linear eye depth = 1 / (z·raw + w).</summary>
        static Vector4 ZParams(float near, float far)
        {
            float x = SystemInfo.usesReversedZBuffer ? -1 + far / near : 1 - far / near;
            float y = SystemInfo.usesReversedZBuffer ? 1 : far / near;
            return new Vector4(x, y, x / far, y / far);
        }

        void Release()
        {
            if (colour != null) UnityEngine.Object.Destroy(colour);
            if (depth != null) UnityEngine.Object.Destroy(depth);
            colour = depth = null;
        }

        public void Dispose()
        {
            DetachBuiltIn();
            Release();
            cmd.Release();
            if (mat != null) UnityEngine.Object.Destroy(mat);
            if (bundle != null) bundle.Unload(true);
        }
    }
}
