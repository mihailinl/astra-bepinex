// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The stock CAMERA locator, when no integration names one: <c>Camera.main</c> while it
    /// renders to the screen and draws the world (not a UI camera, not an overlay stacked on another
    /// camera; an orthographic one only while no perspective camera draws the world); else the
    /// enabled camera that ranks best — one that draws the world over a UI or overlay camera, then
    /// the highest depth, ties broken by whichever moved most recently (an idle security camera loses
    /// to the one the player is steering), then by the larger viewport. Re-scored every ~2 s and
    /// kept while it still renders to the screen, so a momentary <c>Camera.main</c> hiccup never
    /// flickers her view.
    /// </summary>
    sealed class DefaultCamera
    {
        struct Seen { public Vector3 Pos; public Quaternion Rot; public float Since; }

        Camera current;
        float nextSearch;
        readonly Dictionary<int, Seen> moved = new Dictionary<int, Seen>();

        /// <summary>The last pick, for the one-line log when it changes (Driver owns the logging).</summary>
        public string Description = "none";

        /// <summary>The search as an optional part: its first failure switches it off, said once.</summary>
        readonly Feature search = new Feature("the default camera search");

        /// <summary>The camera to draw her for: this locator's pick — or, once a failure has switched its
        /// search off (<see cref="Feature"/>, warned through <paramref name="warn"/>),
        /// <paramref name="previous"/>, the camera picked before (none, if there never was one).</summary>
        public Camera Pick(Camera previous, Action<string> warn)
        {
            this.warn = warn;
            // Run every frame: the answer goes through a field and the body is made once, so no
            // closure is made per frame.
            picked = previous;
            search.Run(locate ??= LocateInto, warn);
            return picked;
        }

        Camera picked;
        Action locate;

        void LocateInto() => picked = Locate();

        /// <summary>The Driver's one-time warning, from the latest <see cref="Pick"/>: for the optional
        /// checks the search makes on its way (<see cref="UrpOverlay"/>).</summary>
        Action<string> warn;

        [MethodImpl(MethodImplOptions.NoInlining)]
        Camera Locate()
        {
            if (Usable(current) && Time.unscaledTime < nextSearch) return current;
            nextSearch = Time.unscaledTime + 2f;
            Camera found;
            string why;
            // Camera.main, unless it is plainly not the world's camera: a UI camera tagged MainCamera,
            // one stacked on another camera (a HUD's or a weapon's overlay: the camera beneath draws the
            // world), one not rendering. An orthographic Camera.main (an isometric game's) is taken only
            // while no perspective camera draws the world: a menu's camera over a world still being
            // drawn loses to that world's camera.
            var tagged = Camera.main;
            int taggedRank = ToScreen(tagged) && (tagged.cullingMask & ~LocatorScoring.UiLayerBit) != 0 && !OverlayOf(tagged)
                ? Rank(tagged) : 0;
            if (taggedRank == 2) { found = tagged; why = "Camera.main"; }
            else
            {
                found = Best();
                if (taggedRank == 1 && (found == null || Rank(found) < 2))
                {
                    found = tagged;
                    why = "Camera.main, orthographic, and no perspective camera draws the world";
                }
                else
                {
                    why = found == null ? "none" : Rank(found) < 2 ? "highest-depth camera with no render target" : ToScreen(found) ? "the camera that draws the world" : "the camera that draws the world into the game's screen texture";
                    // Why Camera.main lost, where the reason is its place in a stack or its projection.
                    if (tagged != null && found != null && found != tagged)
                    {
                        if (OverlayOf(tagged)) why += $"; Camera.main '{tagged.name}' is {Relation(tagged)}";
                        else if (taggedRank == 1) why += $"; Camera.main '{tagged.name}' is orthographic";
                    }
                }
            }
            if (found != current)
            {
                current = found;
                Description = found != null ? $"'{found.name}' ({why})" : "none";
                Inventory = Describe();
            }
            return current;
        }

        /// <summary>Every camera this pick weighed, one per line — logged with a pick, so a tester's log
        /// shows what the scene offered when the pick is wrong.</summary>
        public string Inventory = "";

        /// <summary>A camera began rendering (any camera, every frame): what <see cref="Rank"/> calls
        /// "seen rendering".</summary>
        public void Rendered(Camera c)
        {
            if (c == null) return;
            renderedAt[c.GetInstanceID()] = Time.frameCount;
            Prune();
        }

        struct Weighed { public int FirstFrame; public float LastTime; }

        readonly Dictionary<int, int> renderedAt = new Dictionary<int, int>();
        readonly Dictionary<int, Weighed> weighed = new Dictionary<int, Weighed>();
        readonly Dictionary<int, bool> overlay = new Dictionary<int, bool>();
        int prunedAt;

        /// <summary>
        /// <see cref="LocatorScoring.CameraRank"/> for <paramref name="c"/>. "Rendering" is given the
        /// benefit of the doubt: a camera rendered within 30 frames, or one weighed FRESH within 30
        /// frames — first seen, or back after more than 3 s unweighed (a disabled camera is never
        /// listed). A scene's new Camera.main, or a gameplay camera switched back on after a cutscene,
        /// is picked in the LateUpdate BEFORE its first render, and must not lose to a UI camera.
        /// A camera stacked on another (<see cref="OverlayOf"/>) ranks at most 1: it renders, but only
        /// adds to the picture of the camera beneath it, which draws the world and so wins.
        /// </summary>
        int Rank(Camera c)
        {
            int id = c.GetInstanceID(), now = Time.frameCount;
            float t = Time.unscaledTime;
            if (!weighed.TryGetValue(id, out var w) || t - w.LastTime > 3f) w.FirstFrame = now;
            w.LastTime = t;
            weighed[id] = w;
            bool rendering = (renderedAt.TryGetValue(id, out int at) && now - at <= 30) || now - w.FirstFrame < 30;
            int rank = LocatorScoring.CameraRank(rendering, false, c.orthographic, c.cullingMask);
            return OverlayOf(c) ? Math.Min(rank, 1) : rank;
        }

        /// <summary>Forget cameras long gone — not rendered for 300 frames, not weighed for 10 s — at
        /// most once a second (by frames), never the live ones.</summary>
        void Prune()
        {
            int now = Time.frameCount;
            if (renderedAt.Count + weighed.Count <= 128 || now - prunedAt < 60) return;
            prunedAt = now;
            float t = Time.unscaledTime;
            var stale = new List<int>();
            foreach (var kv in renderedAt)
                if (now - kv.Value > 300) stale.Add(kv.Key);
            foreach (var id in stale) renderedAt.Remove(id);
            stale.Clear();
            foreach (var kv in weighed)
                if (t - kv.Value.LastTime > 10f) stale.Add(kv.Key);
            foreach (var id in stale) weighed.Remove(id);
        }

        /// <summary>A URP Overlay camera by URP's own camera type, read once per camera (<see cref="UrpOverlay"/>):
        /// for the Driver, which watches the cameras of the main camera's stack begin.</summary>
        public bool IsUrpOverlay(Camera c) => c != null && UrpOverlay(c);

        /// <summary>A URP Overlay camera by URP's own camera type (<see cref="Compat.IsUrpOverlay"/>, read
        /// once per camera; false where there is no URP or the check is off).</summary>
        bool UrpOverlay(Camera c)
        {
            int id = c.GetInstanceID();
            if (overlay.TryGetValue(id, out bool yes)) return yes;
            yes = Compat.IsUrpOverlay(c, warn);
            if (overlay.Count > 64) overlay.Clear();
            overlay[id] = yes;
            return yes;
        }

        /// <summary>A camera's place in its frame's camera stack: whether it is an overlay, and the
        /// camera at the bottom of the stack it shares a view with (null when none does).</summary>
        struct Stacked { public int Frame; public bool Overlay; public Camera Base; }

        readonly Dictionary<int, Stacked> stacked = new Dictionary<int, Stacked>();

        /// <summary>
        /// Is <paramref name="c"/> an overlay (<see cref="LocatorScoring.IsOverlay"/>): a camera that only
        /// adds to another camera's picture, a HUD's or a weapon's? URP's camera type says so; otherwise
        /// Unity's own stacking: a usable camera of a LOWER depth (rendered before it) draws into the same
        /// target texture and display from the same pose (<see cref="SameView"/>), this camera keeps
        /// that picture (<see cref="KeepsPictureBeneath"/>), and it draws fewer layers than the widest
        /// camera beneath — so the near half of a near/far split, which keeps the far half's picture but
        /// draws the same world, stays the world's camera. Worked out once per frame.
        /// </summary>
        bool OverlayOf(Camera c) => Stacking(c).Overlay;

        Stacked Stacking(Camera c)
        {
            int id = c.GetInstanceID(), now = Time.frameCount;
            if (stacked.TryGetValue(id, out var s) && s.Frame == now) return s;
            var under = Beneath(c, out int widest);
            bool yes = LocatorScoring.IsOverlay(UrpOverlay(c), under != null, under != null && KeepsPictureBeneath(c),
                c.cullingMask, widest);
            s = new Stacked { Frame = now, Base = under, Overlay = yes };
            if (stacked.Count > 64) stacked.Clear(); // a scene churning through cameras: forget and restart
            stacked[id] = s;
            return s;
        }

        /// <summary>The bottom of <paramref name="c"/>'s stack: of the usable cameras with a lower depth
        /// that draw into its target texture and display from its pose, the lowest; null when none does.
        /// <paramref name="widestMask"/>: the culling mask among them that holds the most layers (0 when
        /// there is none).</summary>
        Camera Beneath(Camera c, out int widestMask)
        {
            Camera found = null;
            widestMask = 0;
            int widest = -1;
            bool posed = false;
            var pose = default(CameraPose);
            foreach (var d in Cameras())
            {
                if (d == c || !Usable(d) || d.depth >= c.depth) continue;
                if (d.targetTexture != c.targetTexture || d.targetDisplay != c.targetDisplay) continue;
                if (!posed)
                {
                    pose = CameraPose.Of(c);
                    posed = true;
                }
                if (!SameView(pose, CameraPose.Of(d))) continue;
                if (found == null || d.depth < found.depth) found = d;
                int layers = LocatorScoring.Layers(d.cullingMask);
                if (layers > widest)
                {
                    widest = layers;
                    widestMask = d.cullingMask;
                }
            }
            return found;
        }

        /// <summary>
        /// Does <paramref name="c"/> keep the picture a camera rendered before it left in their shared
        /// target — by its pipeline's own clearing, not by what its flags look like? Built-in: a camera
        /// that clears depth only, or nothing. URP: a Base camera clears its colour for every flag but
        /// "nothing" ("depth only" counts as a solid colour there; an Overlay camera is known by its type
        /// instead). HDRP clears by its own camera data and stacks no cameras: never.
        /// </summary>
        bool KeepsPictureBeneath(Camera c)
        {
            var clear = c.clearFlags;
            switch (Clearing())
            {
                case PipelineClearing.BuiltIn: return clear == CameraClearFlags.Depth || clear == CameraClearFlags.Nothing;
                case PipelineClearing.Hdrp: return false;
                default: return clear == CameraClearFlags.Nothing;
            }
        }

        /// <summary>Two cameras' poses within 1 mm and 0.5° of each other, both mirrored or neither. The
        /// millimetre widens with the distance from the world's origin, at float precision: there a child
        /// camera's pose, worked out through its parent's matrices, can differ from its parent's by more.</summary>
        static bool SameView(CameraPose a, CameraPose b)
        {
            float reach = Mathf.Max(1e-3f, 1e-6f * a.Pos.magnitude);
            return a.Mirrored == b.Mirrored && (a.Pos - b.Pos).sqrMagnitude <= reach * reach
                && Vector3.Dot(a.Fwd, b.Fwd) >= Cos05 && Vector3.Dot(a.Up, b.Up) >= Cos05;
        }

        /// <summary>cos 0.5°: two unit directions at most half a degree apart have a dot product at least this.</summary>
        const float Cos05 = 0.99996192f;

        /// <summary><paramref name="c"/>'s place in its frame's camera stack, for the log ("an overlay of
        /// 'Main Camera' (the same view, drawn after it without clearing colour)"); empty when it is not
        /// stacked on another camera.</summary>
        string Relation(Camera c)
        {
            var s = Stacking(c);
            if (!s.Overlay) return "";
            string of = s.Base != null ? $" of '{s.Base.name}'" : "";
            string how = UrpOverlay(c) ? "URP's camera type" : "the same view, drawn after it over its picture, fewer layers";
            return $"an overlay{of} ({how})";
        }

        readonly List<Camera> cameras = new List<Camera>();
        int camerasAt = -1;

        /// <summary>How the pipeline rendering the game clears a camera's target (<see cref="KeepsPictureBeneath"/>).</summary>
        enum PipelineClearing { BuiltIn, Hdrp, OtherSrp }

        PipelineClearing clearing;
        UnityEngine.Rendering.RenderPipelineAsset clearingOf;
        bool clearingKnown;

        /// <summary>Every camera Unity lists this frame (<c>Camera.allCameras</c>): read once per frame,
        /// since the stacking test compares each camera with every other.</summary>
        List<Camera> Cameras()
        {
            Refresh();
            return cameras;
        }

        /// <summary>The pipeline drawing this frame's cameras, as far as their clearing goes: worked out
        /// again only when the pipeline asset changes (a quality level may switch it).</summary>
        PipelineClearing Clearing()
        {
            Refresh();
            return clearing;
        }

        void Refresh()
        {
            int now = Time.frameCount;
            if (camerasAt == now) return;
            camerasAt = now;
            cameras.Clear();
            foreach (var c in Camera.allCameras) cameras.Add(c);
            var asset = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (clearingKnown && asset == clearingOf) return;
            clearingKnown = true;
            clearingOf = asset;
            string kind = asset == null ? null : Compat.PipelineClass();
            clearing = asset == null ? PipelineClearing.BuiltIn
                : kind != null && kind.EndsWith(".HDRenderPipelineAsset", StringComparison.Ordinal) ? PipelineClearing.Hdrp
                : PipelineClearing.OtherSrp;
        }

        static bool ToScreen(Camera c) => c != null && c.isActiveAndEnabled && c.targetTexture == null;

        /// <summary>
        /// A camera that draws into a texture the SHAPE of the screen and at least an eighth of its
        /// width: how a game shows its world through a texture of its own (a pixelated look — the world
        /// at 320×180 on a UI RawImage or a quad). Not a monitor's, a mirror's or a minimap's: those
        /// are small or square. She is drawn into that texture, and the game shows her with its world.
        /// </summary>
        static bool ToScreenTexture(Camera c)
        {
            if (c == null || !c.isActiveAndEnabled || c.targetTexture == null) return false;
            var t = c.targetTexture;
            float screen = (float)Screen.width / Mathf.Max(1, Screen.height), tex = (float)t.width / Mathf.Max(1, t.height);
            return t.width * 8 >= Screen.width && Mathf.Abs(tex / screen - 1f) < 0.15f;
        }

        static bool Usable(Camera c) => ToScreen(c) || ToScreenTexture(c);

        Camera Best()
        {
            Camera found = null;
            int bestRank = 0;
            float bestDepth = 0, bestSince = 0, bestArea = 0;
            float now = Time.unscaledTime;
            foreach (var c in Cameras())
            {
                if (!Usable(c)) continue;
                // A camera to the screen outranks one into a screen-shaped texture of the same kind;
                // a texture camera that draws the world outranks a screen camera that does not (the
                // UI camera showing that very texture).
                int rank = Rank(c) * 2 + (ToScreen(c) ? 1 : 0);
                float since = Track(c, now);
                var r = c.pixelRect;
                float area = r.width * r.height;
                if (found != null && !LocatorScoring.BetterCamera(rank, c.depth, since, area, bestRank, bestDepth, bestSince, bestArea))
                    continue;
                found = c;
                bestRank = rank;
                bestDepth = c.depth;
                bestSince = since;
                bestArea = area;
            }
            return found;
        }

        string Describe()
        {
            var lines = new System.Text.StringBuilder();
            foreach (var c in Cameras())
            {
                if (c == null) continue;
                string stack = Relation(c);
                lines.Append($"\n    '{c.name}': rank {Rank(c)}, depth {c.depth:0.##}, {(c.orthographic ? "orthographic" : $"fov {c.fieldOfView:0}")}, " +
                    $"mask 0x{c.cullingMask:x}, {(c.targetTexture != null ? "into a texture" : "to the screen")}{(stack.Length > 0 ? ", " + stack : "")}" +
                    $"{(c == Camera.main ? ", MainCamera" : "")}");
            }
            return lines.ToString();
        }

        /// <summary>When <paramref name="c"/> last moved (now, the first time it is seen, so a
        /// brand-new camera is not starved behind idle ones).</summary>
        float Track(Camera c, float now)
        {
            int id = c.GetInstanceID();
            var t = c.transform;
            if (moved.TryGetValue(id, out var prev))
            {
                if (prev.Pos != t.position || prev.Rot != t.rotation)
                {
                    moved[id] = new Seen { Pos = t.position, Rot = t.rotation, Since = now };
                    return now;
                }
                return prev.Since;
            }
            if (moved.Count > 64) moved.Clear(); // a scene churning through cameras: forget and restart
            moved[id] = new Seen { Pos = t.position, Rot = t.rotation, Since = now };
            return now;
        }
    }
}
