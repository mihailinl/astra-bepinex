// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Astra.Bridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// Puts her picture into the game's: pulls the newest frame out of the ring into two textures
    /// (her colour, her depth) and draws one full-screen pass into the camera's image — over the
    /// finished image, after the game's post-processing (her picture is already display-ready), or,
    /// where the pipeline lets her be drawn inside the camera's render (URP 2022–2023, Built-in),
    /// before it, so the game's look is on her too — that tests her depth against the game's and
    /// reprojects her from the camera she was rendered for to the one presenting now (the shader,
    /// <c>AstraComposite.shader</c>, says how).
    /// </summary>
    public sealed class Compositor : IDisposable
    {
        const string ShaderName = "Hidden/Astra/Composite";

        readonly Material mat;
        readonly AssetBundle bundle;
        // A name no game's buffer has: a Built-in camera's buffers are told apart by name (Compat.KeepFirst).
        readonly CommandBuffer cmd = new CommandBuffer { name = "Astra composite" };
        // Every draw's values go in a property block the command COPIES when recorded: a material's
        // own values are read when the command EXECUTES, and two cameras drawn in one frame (the main
        // one, a video camera) would both get the last camera's.
        readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        // Her picture per VIEW (0 = the main camera; others an in-game camera, a mirror).
        readonly Dictionary<int, View> views = new Dictionary<int, View>();
        Camera attached; // the Built-in camera our command buffer is on
        CameraEvent attachedEvt; // the event it is on there
        // PrepareBuiltIn switched attached's depth texture on (it was off): only then is it switched off
        // again, and only that bit — never a mode restored wholesale over what the game set since.
        bool addedDepthBit;
        // The camera's motion vectors were said to move her (PrepareBuiltIn, inside its render).
        bool motionSaid;
        // attached's buffer count when hers was last kept first, and the frame of the next check anyway.
        int seenBuffers = -1, nextOrderCheck;
        // Frames numbered up to this belong to an earlier session (see Reset).
        long floor;
        // The main camera's depth, kept at its end for a draw at the end of the whole context (KeepDepth).
        RenderTexture keptDepth;
        bool haveKeptDepth;
        bool disposed;
        CameraFeed shots;
        Settings settings;
        Func<Camera, int> viewOf = _ => 0;

        /// <summary>One view's picture: two textures, the frame they hold, the newest frame seen.</summary>
        sealed class View
        {
            public readonly Picture Picture = new Picture();
            public RingFrame Shown;
            public bool Have;
            public long Seen;
        }

        /// <summary>The ring frames come from (null between sessions).</summary>
        public FrameRing Ring { get; set; }

        /// <summary>Whether she is shown at all (the toggle, General.Enabled, and a live link).</summary>
        public bool Show { get; set; } = true;

        /// <summary>How many times she was drawn into a camera, and how many new pictures were taken
        /// from Astra — running counts, for the "where is she" line.</summary>
        public int Drawn, Pictures;

        /// <summary>The share of her newest picture that is not fully transparent, sampled every 5 s
        /// (−1 before the first): 0 means Astra drew an EMPTY picture — she is out of the view it was
        /// drawn for.</summary>
        public float Coverage { get; private set; } = -1;
        float nextCoverage;

        static float AlphaShare(IntPtr colour, int bytes)
        {
            int pixels = bytes / 4, step = Math.Max(1, pixels / 8192), seen = 0, solid = 0;
            for (int i = 0; i < pixels; i += step)
            {
                seen++;
                if (System.Runtime.InteropServices.Marshal.ReadByte(colour, i * 4 + 3) != 0) solid++;
            }
            return seen == 0 ? 0 : (float)solid / seen;
        }

        /// <summary>False in a pipeline whose depth texture this pass cannot read as it is (HDRP):
        /// she is then drawn over everything rather than hidden at random.</summary>
        public bool DepthTest { get; set; } = true;

        /// <summary>What her last draw after a camera (<see cref="DrawNow"/>) was tested against, for the
        /// log: "kept from '&lt;camera&gt;'" (the copy <see cref="KeepDepth"/> made at that camera's end),
        /// "bound at the end of '&lt;camera&gt;'", or "none: …" and why — she is then drawn over
        /// everything. Null before the first.</summary>
        public string DepthState { get; private set; }

        /// <summary>Whether that draw was depth-tested at all (false for every "none: …" state).</summary>
        public bool DepthTested { get; private set; }

        /// <summary>The material her pass draws with (for a pass a pipeline adapter records).</summary>
        public Material Material => mat;

        /// <summary>The shader pass that tests her against the camera's depth BUFFER bound with the
        /// target (she writes SV_Depth): for an adapter whose pipeline's depth texture this shader
        /// cannot read (HDRP). Pass 0 tests against the depth texture.</summary>
        public const int DepthBufferPass = 1;

        static readonly int IdColour = Shader.PropertyToID("_AstraColour");
        static readonly int IdDepth = Shader.PropertyToID("_AstraDepth");
        static readonly int IdSize = Shader.PropertyToID("_AstraSize");
        static readonly int IdProjNow = Shader.PropertyToID("_AstraProjNow");
        static readonly int IdGpuProj = Shader.PropertyToID("_AstraGpuProj");
        static readonly int IdInvGpuProj = Shader.PropertyToID("_AstraInvGpuProj");
        static readonly int IdZNdc = Shader.PropertyToID("_AstraZNdc");
        static readonly int IdTanThen = Shader.PropertyToID("_AstraTanThen");
        static readonly int IdRot0 = Shader.PropertyToID("_AstraRot0");
        static readonly int IdRot1 = Shader.PropertyToID("_AstraRot1");
        static readonly int IdRot2 = Shader.PropertyToID("_AstraRot2");
        static readonly int IdEyeThen = Shader.PropertyToID("_AstraEyeThen");
        static readonly int IdTest = Shader.PropertyToID("_AstraTest");
        static readonly int IdOut = Shader.PropertyToID("_AstraOut");
        static readonly int IdGameDepth = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>Diagnostics: told (once per kind) why there is nothing to draw.</summary>
        internal Action<string> Note;
        string lastNote;

        void Say(string why)
        {
            if (why == lastNote) return;
            lastNote = why;
            Note?.Invoke(why);
        }

        // Lines told once for the whole run: a game that rebuilds its buffers every frame must not fill the log.
        readonly HashSet<string> saidOnce = new HashSet<string>();

        void SayOnce(string line)
        {
            if (saidOnce.Add(line)) Note?.Invoke(line);
        }

        /// <summary>Why the compositor could not be made (null when it was).</summary>
        public static string Failure { get; private set; }

        Compositor(Material mat, AssetBundle bundle)
        {
            this.mat = mat;
            this.bundle = bundle;
        }

        /// <summary>What every draw reads: the cameras sent (to reproject from), the settings, and which
        /// view a camera is.</summary>
        internal void Bind(CameraFeed shots, Settings settings, Func<Camera, int> viewOf)
        {
            this.shots = shots;
            this.settings = settings;
            this.viewOf = viewOf;
        }

        /// <summary>Load the shader bundle built for this game's Unity line and platform.</summary>
        public static Compositor Create()
        {
            Failure = null;
            string platform = Application.platform == RuntimePlatform.WindowsPlayer ? "windows"
                : Application.platform == RuntimePlatform.LinuxPlayer ? "linux" : null;
            if (platform == null) return Fail($"no shaders for {Application.platform} yet");
            int major = Major(Application.unityVersion);
            // Every bundle not newer than the game's Unity line, newest first: a bundle loads in its
            // own line and later ones — but within a line, a NEWER minor's bundle may be refused by
            // an older player (6000.3's in a 6000.0 game), so on a refusal the next older one is tried.
            var asm = Assembly.GetExecutingAssembly();
            var candidates = new System.Collections.Generic.List<KeyValuePair<int, string>>();
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("astra-", StringComparison.Ordinal) || !res.EndsWith("-" + platform + ".bundle", StringComparison.Ordinal)) continue;
                int m = Major(res.Substring(6));
                if (m <= major) candidates.Add(new KeyValuePair<int, string>(m, res));
            }
            if (candidates.Count == 0) return Fail($"no shader bundle for Unity {Application.unityVersion} on {platform}");
            candidates.Sort((a, b) => b.Key.CompareTo(a.Key));
            Shader shader = null;
            AssetBundle bundle = null;
            var refusals = new System.Collections.Generic.List<string>();
            foreach (var candidate in candidates)
            {
                byte[] bytes;
                using (var s = asm.GetManifestResourceStream(candidate.Value))
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    bytes = m.ToArray();
                }
                shader = Compat.LoadShader(bytes, ShaderName, out bundle, out string why);
                if (shader != null) break;
                if (bundle != null) bundle.Unload(true);
                bundle = null;
                refusals.Add($"{candidate.Value}: {why}");
            }
            if (shader == null) return Fail($"no shader bundle loads in Unity {Application.unityVersion}: {string.Join("; ", refusals)}");
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

        /// <summary>A new session: forget the pictures shown, and take only frames published after
        /// <paramref name="floor"/> (the ring's counter now) — the ring outlives an engine, and the
        /// newest frames still in it are the previous session's: shown again, she would stand frozen
        /// where she was until the engine draws her anew.</summary>
        public void Reset(long floor = 0)
        {
            this.floor = floor;
            foreach (var v in views.Values)
            {
                v.Have = false;
                v.Seen = floor;
            }
        }

        /// <summary><paramref name="view"/> was given to another camera: what it showed was another
        /// camera's.</summary>
        public void ForgetView(int view)
        {
            if (!views.TryGetValue(view, out var v)) return;
            v.Have = false;
            v.Seen = Math.Max(v.Seen, Ring?.Counter ?? floor);
        }

        /// <summary>Take the newest frame of <paramref name="view"/> from the ring, if there is one, into
        /// that view's textures.</summary>
        View Pull(int view)
        {
            if (!views.TryGetValue(view, out var v)) views[view] = v = new View { Seen = floor };
            if (Ring == null || !Ring.TryLatestOf(view, ref v.Seen, out var f)) return v;
            if (f.Width < 1 || f.Height < 1)
            {
                v.Have = false;
                return v;
            }
            if (view == 0 && Time.unscaledTime >= nextCoverage)
            {
                nextCoverage = Time.unscaledTime + 5f;
                // A cropped frame's planes are her rectangle: its share, scaled to the whole view.
                float share = f.PlaneBytes > 0 ? AlphaShare(f.Colour, f.PlaneBytes) : 0;
                Coverage = f.Cropped ? share * f.CropW * f.CropH / ((float)f.Width * f.Height) : share;
            }
            if (!v.Picture.Load(Ring, ref f, view, out bool fresh))
            {
                // The engine reused the slot while we copied: this copy is torn and is never applied.
                // The GPU still holds the last whole picture, so it goes on showing — unless the
                // textures were made anew just now, and hold nothing yet.
                if (fresh) v.Have = false;
                return v;
            }
            Pictures++;
            v.Shown = f;
            v.Have = true;
            return v;
        }

        /// <summary>The picture to draw into <paramref name="cam"/>: its own view's newest, else the main
        /// view's reprojected (an engine that draws one view only, or the first frames).</summary>
        bool Pick(Camera cam, out View v, out Shot then)
        {
            int view = viewOf(cam);
            v = Pull(view);
            if (v.Have && shots.Find(v.Shown.HostFrame, out then)) return true;
            if (view != 0)
            {
                v = Pull(0);
                if (v.Have && shots.Find(v.Shown.HostFrame, out then)) return true;
            }
            Say(!v.Have ? $"no picture of view {view} yet (newest seen: publish number {v.Seen})"
                : $"the picture of view {view} was drawn for camera {v.Shown.HostFrame}, which is not among the cameras sent");
            then = default;
            return false;
        }

        /// <summary>
        /// SRP: draw her over <paramref name="cam"/>'s finished image, now (call when the camera has
        /// finished rendering — its post-processing included).
        /// </summary>
        /// <param name="cam">The camera that has just finished.</param>
        /// <param name="keptDepth">Test against the depth <see cref="KeepDepth"/> kept, never the one bound now.</param>
        public void DrawNow(Camera cam, bool keptDepth = false)
        {
            if (!Show) return;
            // At the end of the whole context the depth bound is the LAST camera's (a monitor's, a
            // minimap's) or URP's placeholder: only the copy kept at this camera's own end is its
            // depth, and with no copy she is not tested at all — never against whatever is bound.
            // Right after the camera, what is bound is tested only if it can be this camera's.
            bool test;
            if (keptDepth) test = haveKeptDepth; // KeepDepth said what was kept, or why nothing was
            else
            {
                var bound = DepthTest ? Shader.GetGlobalTexture(IdGameDepth) : null;
                test = DepthTest && IsDepthOf(cam, bound, out _);
                if (test) SetDepthState(DepthWhy.Bound, cam);
                else if (!DepthTest) SetDepthState(DepthWhy.NoTest, cam);
                else SetDepthState(NoDepth(bound, out var rt), cam, rt);
            }
            DepthTested = test;
            if (!Prepare(cam, srp: true, cam.targetTexture != null, test, block)) return;
            if (test && keptDepth) block.SetTexture(IdGameDepth, this.keptDepth);
            cmd.Clear();
            // Each target below is the camera's final target, which its pixelRect is a part of; setting a
            // target resets the viewport to the whole of it. Without the viewport a split-screen camera or
            // a letterboxed one got her picture stretched over the whole target instead of over its own part.
            // A camera that covers its whole target gets the same viewport as before.
            if (cam.targetTexture != null)
            {
                cmd.SetRenderTarget(cam.targetTexture);
                cmd.SetViewport(cam.pixelRect);
            }
            // At the end of the whole context no camera is rendering, and "the camera's target" can
            // be the LAST camera's — a game's monitor or minimap camera drawing into its own texture
            // (Extermination Ship has eight beside the player's): her picture went into that texture
            // and never reached the screen. A screen camera's target is named: its display.
            else if (keptDepth)
            {
                bool screen = ActivateScreenOf(cam);
                cmd.SetRenderTarget(screen ? BuiltinRenderTextureType.CurrentActive : BuiltinRenderTextureType.CameraTarget);
                cmd.SetViewport(cam.pixelRect);
                if (!targetSaid)
                {
                    targetSaid = true;
                    Note?.Invoke(screen ? $"drawn at the end of the frame onto display {cam.targetDisplay}'s back buffer"
                        : "drawn at the end of the frame onto the camera's target (this build cannot name the display's back buffer)");
                }
            }
            else
            {
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                cmd.SetViewport(cam.pixelRect);
            }
            // Said once per camera and part, looked up before any text is built (this runs every frame);
            // a game that animates a camera's part (a letterbox tween) stops being told after 64.
            var part = cam.rect;
            if (part != new Rect(0, 0, 1, 1) && partsSaid.Count < 64
                && partsSaid.Add((cam.GetInstanceID(), part.x, part.y, part.width, part.height)))
                SayOnce($"'{cam.name}' covers {part} of its target: she is drawn inside that part only");
            cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3, 1, block);
            Graphics.ExecuteCommandBuffer(cmd);
        }

        static bool noScreenTarget;
        bool targetSaid;
        readonly HashSet<(int cam, float x, float y, float w, float h)> partsSaid =
            new HashSet<(int cam, float x, float y, float w, float h)>();

        /// <summary>Make the back buffer of the display <paramref name="cam"/> renders to the active
        /// target; false (and the camera's target is used, as before) where the game's build removed
        /// what that takes.</summary>
        static bool ActivateScreenOf(Camera cam)
        {
            if (noScreenTarget) return false;
            try
            {
                ActivateDisplay(cam.targetDisplay);
                return true;
            }
            catch (Exception e) when (Compat.Stripped(e))
            {
                noScreenTarget = true;
                return false;
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void ActivateDisplay(int i)
        {
            var displays = Display.displays;
            var display = displays != null && i >= 0 && i < displays.Length ? displays[i] : Display.main;
            Graphics.SetRenderTarget(display.colorBuffer, display.depthBuffer);
        }

        /// <summary>
        /// SRP, drawing at the end of the whole context (a camera stack's final blit would cover her
        /// at her camera's end): keep a copy of the depth texture bound NOW, at the end of
        /// <paramref name="cam"/> — by the context's end it is the depth of whichever camera with one
        /// rendered last (a video camera, a mirror), and her main picture would be hidden by that
        /// camera's walls. Kept only when what is bound can be <paramref name="cam"/>'s own
        /// (<see cref="IsDepthOf"/>); otherwise nothing is kept, she is not tested, and
        /// <see cref="DepthState"/> says why.
        /// </summary>
        public void KeepDepth(Camera cam)
        {
            haveKeptDepth = false;
            if (!Show || disposed) return;
            if (!DepthTest)
            {
                SetDepthState(DepthWhy.NoTest, cam);
                return;
            }
            if (SystemInfo.copyTextureSupport == CopyTextureSupport.None)
            {
                SetDepthState(DepthWhy.NoCopy, cam);
                return;
            }
            var bound = Shader.GetGlobalTexture(IdGameDepth);
            if (!IsDepthOf(cam, bound, out var src))
            {
                SetDepthState(NoDepth(bound, out var rt), cam, rt);
                return;
            }
            if (keptDepth == null || keptDepth.width != src.width || keptDepth.height != src.height || keptDepth.format != src.format
                || keptDepth.depth != src.depth || keptDepth.antiAliasing != src.antiAliasing || keptDepth.dimension != src.dimension
                || keptDepth.volumeDepth != src.volumeDepth)
            {
                if (keptDepth != null) UnityEngine.Object.Destroy(keptDepth);
                keptDepth = new RenderTexture(src.descriptor) { name = "Astra kept depth", hideFlags = HideFlags.HideAndDontSave };
                keptDepth.Create();
            }
            Graphics.CopyTexture(src, keptDepth);
            haveKeptDepth = true;
            SetDepthState(DepthWhy.Kept, cam);
        }

        /// <summary>
        /// Can <paramref name="t"/> be <paramref name="cam"/>'s own depth texture: a created render
        /// texture (<paramref name="rt"/>) of the camera's aspect, within 2 %? URP's render scale
        /// changes its size, never its aspect. Where URP made no depth texture for the camera it binds
        /// a constant placeholder (a tiny black or white texture, no render texture at all), and
        /// another camera's left bound (a monitor's, a minimap's) is told apart by its shape — by its
        /// shape only: a camera of the same aspect passes. A depth texture of another aspect that IS
        /// the camera's (XR, a custom upscaler) is refused: no test, never a wrong one.
        /// </summary>
        static bool IsDepthOf(Camera cam, Texture t, out RenderTexture rt)
        {
            rt = Compat.AsRenderTexture(t);
            if (rt == null || !rt.IsCreated()) return false;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (w < 1 || h < 1 || rt.width < 1 || rt.height < 1) return false;
            float want = (float)w / h, have = (float)rt.width / rt.height;
            return Math.Abs(have - want) <= 0.02f * want;
        }

        const string NoDepthTest = "none: this pipeline's depth texture cannot be read by her pass";

        /// <summary>What <see cref="DepthState"/> says, as data: its text is built once per kind, camera
        /// and shape — a draw every frame (the main camera's and an extra camera's, in turn) must not
        /// build a string (an icall for the camera's name under IL2CPP) every frame.</summary>
        enum DepthWhy { Bound, Kept, NoTest, NoCopy, NoTexture, Placeholder, Released, OtherShape }

        // Keyed by the kind as a number: an enum in a key may be boxed by an older runtime's comparer.
        readonly Dictionary<(int why, int cam, int w, int h, int cw, int ch), string> depthTexts =
            new Dictionary<(int why, int cam, int w, int h, int cw, int ch), string>();

        /// <summary>Set <see cref="DepthState"/> to <paramref name="why"/> for <paramref name="cam"/>
        /// (and, for a texture of another shape, <paramref name="rt"/>), from the texts already built.</summary>
        void SetDepthState(DepthWhy why, Camera cam, RenderTexture rt = null)
        {
            int w = 0, h = 0, cw = 0, ch = 0;
            if (why == DepthWhy.OtherShape && rt != null)
            {
                w = rt.width;
                h = rt.height;
                cw = cam.pixelWidth;
                ch = cam.pixelHeight;
            }
            var key = ((int)why, cam.GetInstanceID(), w, h, cw, ch);
            if (!depthTexts.TryGetValue(key, out var text))
            {
                if (depthTexts.Count >= 32) depthTexts.Clear(); // cameras and sizes churning: start again
                text = DepthText(why, cam, w, h, cw, ch);
                depthTexts[key] = text;
            }
            DepthState = text;
        }

        static string DepthText(DepthWhy why, Camera cam, int w, int h, int cw, int ch)
        {
            switch (why)
            {
                case DepthWhy.Bound: return $"bound at the end of '{cam.name}'";
                case DepthWhy.Kept: return $"kept from '{cam.name}'";
                case DepthWhy.NoTest: return NoDepthTest;
                case DepthWhy.NoCopy:
                    return $"none: this platform cannot copy textures, so the depth of '{cam.name}' cannot be kept for the frame's end";
                case DepthWhy.NoTexture: return $"none: no depth texture is bound at the end of '{cam.name}'";
                case DepthWhy.Placeholder: return $"none: the pipeline made no depth texture for '{cam.name}' (a constant placeholder)";
                case DepthWhy.Released: return $"none: the depth texture bound at the end of '{cam.name}' is released";
                default:
                    return $"none: the depth texture bound at the end of '{cam.name}' is {w}x{h}, "
                        + $"not of the camera's {cw}x{ch} shape (another camera's)";
            }
        }

        /// <summary>Why <paramref name="bound"/>, bound at the end of a camera, failed
        /// <see cref="IsDepthOf"/> — the "none: …" kinds of <see cref="DepthState"/>;
        /// <paramref name="rt"/> is the texture, for one of another shape.</summary>
        static DepthWhy NoDepth(Texture bound, out RenderTexture rt)
        {
            rt = null;
            if (bound == null) return DepthWhy.NoTexture;
            rt = Compat.AsRenderTexture(bound);
            if (rt == null) return DepthWhy.Placeholder;
            return rt.IsCreated() ? DepthWhy.OtherShape : DepthWhy.Released;
        }

        /// <summary>
        /// The Built-in pipeline: <paramref name="cam"/> draws her itself, from a command buffer kept
        /// FIRST at one of its camera events. With <c>Picture.BeforePostProcessing</c> (the default) that
        /// is right after its transparent objects, INSIDE its render: into whatever it drew them into,
        /// tested by the GPU against its depth BUFFER (shader pass 1) — every wall that writes depth
        /// hides her, whatever its shader — and the game's image effects then apply to her too.
        /// Otherwise, or when the camera renders into a texture with no depth buffer, after its image
        /// effects, tested against its depth texture (switched on for it), which holds only what casts
        /// shadows. Call before it renders (pre-cull): this records what that buffer draws this frame.
        /// </summary>
        public void PrepareBuiltIn(Camera cam)
        {
            // A target with no depth buffer leaves nothing to test her against inside the render.
            bool inside = settings.BeforePostProcessing.Value && !(cam.targetTexture != null && cam.targetTexture.depth == 0);
            var evt = inside ? CameraEvent.AfterForwardAlpha : CameraEvent.AfterImageEffects;
            if (attached != cam || attachedEvt != evt)
            {
                DetachBuiltIn();
                if (!inside)
                {
                    addedDepthBit = (cam.depthTextureMode & DepthTextureMode.Depth) == 0;
                    cam.depthTextureMode |= DepthTextureMode.Depth;
                }
                attached = cam;
                attachedEvt = evt;
                seenBuffers = -1; // put on the camera just below
                SayOnce(inside
                    ? $"Built-in: drawn inside '{cam.name}' right after its transparent objects, tested against its depth buffer (the game's image effects apply to her)"
                    : $"Built-in: drawn into '{cam.name}' after its image effects, tested against its depth texture (walls whose shaders cast no shadows do not hide her)");
            }
            else if (!inside && (cam.depthTextureMode & DepthTextureMode.Depth) == 0)
            {
                // The game set the camera's mode over ours (its own settings code): without the depth
                // texture she would be tested against nothing, or against another camera's depth.
                cam.depthTextureMode |= DepthTextureMode.Depth;
                addedDepthBit = true;
                SayOnce($"'{cam.name}' had its depth texture switched off: switched on again for her");
            }
            // Motion blur and TAA (the post-processing stack's, or a game's own) read the camera's motion
            // vectors, which hold what is BEHIND her (she writes none, and they are drawn before her):
            // inside the render they move her with that — a smear when the background moves and she
            // does not. Said once, by the camera's own mode bit and never by a game's name, so a live
            // check can tell which games it is; BeforePostProcessing off keeps her out of them.
            if (inside && !motionSaid && (cam.depthTextureMode & DepthTextureMode.MotionVectors) != 0)
            {
                motionSaid = true;
                SayOnce($"'{cam.name}' renders motion vectors (the game's motion blur or TAA): they move her with what is "
                    + "behind her — if she smears, set [Picture] BeforePostProcessing = false");
            }
            // Kept first whenever the camera's buffers change — a game may take every buffer off an
            // event when it rebuilds its own (ULTRAKILL does at AfterForwardAlpha on a resolution
            // change), and hers with them — and once a second anyway: a buffer swapped for another
            // leaves the count as it was.
            if (cam.commandBufferCount != seenBuffers || Time.frameCount >= nextOrderCheck)
            {
                bool first = Compat.KeepFirst(cam, evt, cmd, out bool moved, out string why);
                if (!first) SayOnce($"cannot put her first at {evt} ({why}): appended");
                else if (moved && seenBuffers >= 0) SayOnce($"the buffers '{cam.name}' runs at {evt} changed: she was put first again");
                seenBuffers = cam.commandBufferCount;
                nextOrderCheck = Time.frameCount + 60;
            }
            cmd.Clear();
            // After the image effects, Built-in binds the depth texture switched on above for the
            // camera's render; inside, pass 1 reads no texture at all.
            if (!Show || !Prepare(cam, srp: false, cam.targetTexture != null, depthTest: true, block)) return;
            // Inside: NO target of her own. The buffer inherits what the camera drew its transparent
            // objects into — the back buffer, an HDR, MSAA or deferred intermediate, or a game's own
            // SetTargetBuffers — with its depth buffer and its viewport, and the flip follows it
            // (_ProjectionParams, set by Unity for this render).
            // After the image effects: into what the camera is rendering into RIGHT NOW, not "its
            // target": a game that points its camera at its own buffers with SetTargetBuffers
            // (ULTRAKILL: colour, outline data and normals, then a second camera puts the colour on the
            // screen through its palette and pixelation) leaves targetTexture null, and CameraTarget
            // then drew her on the back buffer — under that second camera's full-screen picture, every
            // frame. The active target is the camera's own (a screen camera's back buffer is the same
            // thing as before).
            if (!inside) cmd.SetRenderTarget(BuiltinRenderTextureType.CurrentActive);
            cmd.DrawProcedural(Matrix4x4.identity, mat, inside ? DepthBufferPass : 0, MeshTopology.Triangles, 3, 1, block);
        }

        /// <summary>Undo <see cref="PrepareBuiltIn"/>'s camera changes (M6): the command buffer, and
        /// the depth texture bit — only if it switched that on — never left permanently on a camera
        /// that is not being drawn into (every desktop session with no engine, before this).</summary>
        internal void DetachBuiltIn()
        {
            if (attached != null)
            {
                attached.RemoveCommandBuffer(attachedEvt, cmd);
                if (addedDepthBit) attached.depthTextureMode &= ~DepthTextureMode.Depth;
            }
            attached = null;
            addedDepthBit = false;
        }

        /// <summary>
        /// For a pass a pipeline adapter records INSIDE the camera's render: take the newest frame of
        /// the camera's view and put everything the pass reads into <paramref name="into"/> (copied by
        /// the draw that uses it). The adapter adds the game's depth texture where its pipeline needs
        /// it bound. False when there is nothing to draw.
        /// </summary>
        /// <param name="cam">The camera being rendered.</param>
        /// <param name="intoTexture">The pass draws into a render texture (not the back buffer).</param>
        /// <param name="into">The draw's property block.</param>
        public bool PrepareForPass(Camera cam, bool intoTexture, MaterialPropertyBlock into)
        {
            PassFrame = Time.frameCount;
            PassCamera = cam != null ? cam.GetInstanceID() : 0;
            // An adapter's pass may outlive the foundation (stood down after a fault): draw nothing then.
            return !disposed && Show && Prepare(cam, srp: true, intoTexture, depthTest: true, into);
        }

        /// <summary>The frame and the camera an adapter's pass last ran for — so a queued pass the
        /// pipeline never ran (a reflection camera's render emptied the queue, a render graph that calls
        /// no Execute) is noticed and she is composited the old way instead of not at all.</summary>
        public int PassFrame = -1, PassCamera;

        /// <summary>Put everything the pass reads for <paramref name="cam"/> in <paramref name="b"/>;
        /// false when there is nothing to show.</summary>
        bool Prepare(Camera cam, bool srp, bool intoTexture, bool depthTest, MaterialPropertyBlock b)
        {
            var s = settings;
            // An orthographic camera (a menu's, picked while the game shows it) is never sent, so the
            // newest picture is a perspective camera's: reprojected through this camera's matrix it
            // would stand frozen as a cut-out over the menu. Asked before Pick, so no frame is uploaded.
            if (cam.orthographic) return false;
            // A cropped frame with nothing of her in this view: no draw at all.
            if (disposed || Ring == null || !Pick(cam, out var v, out var then) || v.Picture.Empty) return false;
            b.Clear(); // nothing an earlier draw set (a kept depth) may leak into this one

            // The present view's axes. A mirror's camera renders the proper view flipped left to
            // right: its x axis is the proper camera's left.
            var now = CameraPose.Of(cam);
            Vector3 right = now.Mirrored ? -now.Right : now.Right, up = now.Up, fwd = now.Fwd;
            // Rows of the rotation from the present view to the one she was drawn in, and her eye then.
            var eye = then.Pos - now.Pos;
            b.SetVector(IdRot0, new Vector4(Vector3.Dot(then.Right, right), Vector3.Dot(then.Right, up), Vector3.Dot(then.Right, fwd), 0));
            b.SetVector(IdRot1, new Vector4(Vector3.Dot(then.Up, right), Vector3.Dot(then.Up, up), Vector3.Dot(then.Up, fwd), 0));
            b.SetVector(IdRot2, new Vector4(Vector3.Dot(then.Fwd, right), Vector3.Dot(then.Fwd, up), Vector3.Dot(then.Fwd, fwd), 0));
            b.SetVector(IdEyeThen, new Vector4(Vector3.Dot(eye, right), Vector3.Dot(eye, up), Vector3.Dot(eye, fwd), 0));
            // The camera's OWN projection, not its field of view: a mirror's camera is given an
            // explicit (oblique, other-aspect) matrix. Its x/y rows give the rays; the GPU form of
            // the whole matrix turns the game's depth into metres and her metres into depth.
            var p = cam.projectionMatrix;
            b.SetVector(IdProjNow, new Vector4(p.m00, p.m11, p.m02, p.m12));
            var g = GL.GetGPUProjectionMatrix(p, false); // the platform's depth range; y is never flipped here
            b.SetMatrix(IdGpuProj, g);
            b.SetMatrix(IdInvGpuProj, g.inverse);
            bool gl = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLCore || SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
            b.SetVector(IdZNdc, gl ? new Vector4(2, -1, 0, 0) : new Vector4(1, 0, 0, 0)); // depth texel → NDC z
            b.SetVector(IdTanThen, new Vector4(then.TanX, then.TanY, 0, 0));
            var pic = v.Picture;
            b.SetVector(IdSize, new Vector4(pic.Colour.width, pic.Colour.height, 1f / pic.Colour.width, 1f / pic.Colour.height));
            b.SetTexture(IdColour, pic.Colour);
            b.SetTexture(IdDepth, pic.Depth);
            b.SetVector(IdTest, new Vector4(depthTest ? 1 : 0, s.DepthBias.Value, s.DepthSoftness.Value, 0));
            // Which way up the target is: an SRP's pass gets it worked out here; inside a Built-in
            // camera, 0 = "ask _ProjectionParams", which Unity sets for that target.
            float flip = !srp ? 0 : GL.GetGPUProjectionMatrix(Matrix4x4.identity, intoTexture).m11 < 0 ? -1 : 1;
            b.SetVector(IdOut, new Vector4(flip, WriteLinear(s) ? 1 : 0, 1, 0));
            Drawn++;
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

        static void Release(View v)
        {
            v.Picture.Release();
        }

        public void Dispose()
        {
            disposed = true;
            Show = false;
            DetachBuiltIn();
            if (keptDepth != null) UnityEngine.Object.Destroy(keptDepth);
            foreach (var v in views.Values) Release(v);
            cmd.Release();
            if (mat != null) UnityEngine.Object.Destroy(mat);
            if (bundle != null) bundle.Unload(true);
        }
    }
}
