// SPDX-License-Identifier: MIT
// A readability shard of Driver: the camera-render handlers.
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    public sealed partial class Driver
    {
        bool inGraph;      // this frame's composite was queued into the camera's render graph
        bool mainRendered; // the main camera rendered in the current context
        /// <summary>Her light message (<see cref="SendLight"/>).</summary>
        readonly Feature lighting = new Feature("her light");
        /// <summary>Her shadow caster (<see cref="UpdateShadow"/>).</summary>
        readonly Feature casting = new Feature("her shadow");
        /// <summary>The main camera's depth, kept for the context's end (<see cref="KeepDepthOf"/>).</summary>
        readonly Feature keepingDepth = new Feature("the depth kept for the frame's end");
        // The bodies the per-frame optional parts run, made once (Feature.Run's one-argument form):
        // a lambda per call was a closure per frame.
        Action<Camera> sendLight, updateShadow, keepDepthOf;
        Action dropShadow;
        bool contextEnd => hooks.ContextEnd; // the SRP reports the end of a whole render context (draw there)
        readonly HashSet<int> extraInGraph = new HashSet<int>();
        readonly TextureStack textureStack = new TextureStack(m => log.LogInfo(m));
        // The last texture she was said to be drawn into: its camera and its size.
        int texSaidCam, texSaidW, texSaidH;
        int passMisses;
        string keepingOff;
        // The depth states said: looked up by the text itself, which the compositor builds once per state,
        // so a frame that says the same builds no key and no line.
        readonly HashSet<string> depthsSaid = new HashSet<string>();
        Action<Camera> prepareBuiltIn;

        /// <summary>Is <paramref name="cam"/> one the integration wants her in too? (Its answer is kept
        /// per camera; its depth texture is switched on once.)</summary>
        bool IsExtra(Camera cam)
        {
            var pick = integration?.ExtraCamera;
            if (pick == null || cam == null) return false;
            int id = cam.GetInstanceID();
            if (!extraCameras.TryGetValue(id, out bool yes))
            {
                yes = Guarded("extra camera", () => pick(cam), () => false);
                extraCameras[id] = yes;
                if (yes) log.LogInfo($"also drawing her into the camera '{cam.name}'");
            }
            return yes;
        }
        /// <summary>Hold the URP depth-texture request on <paramref name="cam"/> — the main camera while
        /// she is drawn into it, null while she is not — and let go of the one held before: a camera
        /// that is no longer hers gets its own option back (DepthRequests). With null, every request
        /// goes, an extra camera's too.</summary>
        void HoldDepth(Camera cam)
        {
            if (cam == depthHeld && (cam != null || ReferenceEquals(depthHeld, null))) return;
            if (cam == null) depth.ReleaseAll();
            else depth.Release(depthHeld);
            depth.Hold(cam);
            depthHeld = cam;
        }
        void OnBeginCamera(Camera cam)
        {
            defaultCamera.Rendered(cam);
            if (textureStack.Watching && textureStack.Begins(cam, main, srp && !hdrp && defaultCamera.IsUrpOverlay(cam)))
                DrawStacked();
            // Only where she can be drawn INTO another camera (URP — Built-in and HDRP composite into
            // the main one only) and while she is shown: else a view would be drawn for nobody.
            if (!faulted && cam != main && compositor != null && srp && !hdrp && compositor.Show && her.Placed && link != null && link.Ready && IsExtra(cam))
            {
                // Another camera of the game (a video camera, a mirror): its own view — the engine draws
                // her for it (an engine without views leaves it the main picture, reprojected). Its depth
                // texture is held while she is drawn into it (let go with the main camera's).
                depth.Hold(cam);
                int view = views.Assign(cam, out int taken);
                if (taken != 0)
                {
                    compositor.ForgetView(taken);
                    log.LogInfo($"the camera '{cam.name}' is her view {taken}");
                }
                if (view != 0) Fenced("send", () => SendCamera(cam, view));
                if (srp && !hdrp && UrpHook.Enqueue(cam, compositor)) extraInGraph.Add(cam.GetInstanceID());
                else extraInGraph.Remove(cam.GetInstanceID());
                return;
            }
            // Nothing is queued into the main camera this frame until the end of this method says so:
            // a frame that returns early (no link) must not leave the last frame's "queued" standing.
            if (cam == main) inGraph = false;
            if (faulted || cam != main || link == null || !link.Ready) return;
            // Her light first, as an optional part outside the fence. An engine that named its caps and
            // left "light" off cannot read the message at all; one that never said caps (today's
            // behaviour) gets it as always.
            if (her.Placed && settings.SendLight.Value && link.Supports("light")) lighting.Run(sendLight ??= SendLight, cam, warn);
            Fenced("send", () =>
            {
                if (!her.Placed) return;
                SendCues(frame.Cues);
                if (integration != null) SendCues(integration.Cues);
                parameters.Clear();
                parameters.Set("speed", new Vector3(velocity.x, 0, velocity.z).magnitude).Set("airborne", her.Airborne);
                parameters.MergeFrom(frame.Params);
                Send(Messages.Avatar(new Placement
                {
                    Pos = Wire.Of(her.Position),
                    Fwd = Wire.Of(her.Facing),
                    Up = her.Up.HasValue ? Wire.Of(her.Up.Value) : (Vec3?)null,
                    Vel = Wire.Of(velocity),
                    Anchor = her.Anchor,
                    Scale = scale,
                }, parameters));
                SendCamera(cam); // LAST: it commits the frame
                lastSent = Time.unscaledTime;
            });
            // Her shadow, before this camera draws its shadow maps: the game's own lights cast it. Off,
            // she has none: the caster is taken out of the scene.
            casting.Run(updateShadow ??= UpdateShadow, cam, warn, dropShadow ??= DropShadow);
            if (compositor == null || faulted) return;
            // URP 17: her pass goes INTO this camera's render graph (the only place its depth exists).
            // HDRP: her custom pass rides on this camera (attached once per main camera).
            inGraph = srp && (hdrp ? HdrpHook.Ensure(cam, compositor) : UrpHook.Enqueue(cam, compositor));
            string missing = hdrp ? HdrpHook.Missing : UrpHook.Missing;
            // Guarded (M5): this clause holds for a whole session without the pipeline's adapter, so
            // without the membership check first, the interpolated string was built EVERY camera begin
            // even though Once() only ever logs it the first time.
            if (srp && !inGraph && missing != null && !logged.Contains("adapter-fallback"))
                Once("adapter-fallback", $"compositing after the camera: {missing}");
        }
        void OnEndCamera(Camera cam)
        {
            if (!faulted && cam != main && compositor != null && !hdrp && !(extraInGraph.Contains(cam.GetInstanceID()) && PassRan(cam)) && IsExtraKnown(cam))
            {
                // Right after the camera, against its own depth texture (no stack to blit over her).
                DrawAfterCamera(cam);
                return;
            }
            if (faulted || cam != main || compositor == null || RanInGraph(cam)) return;
            // Where the SRP reports the end of the whole context, draw THERE for a camera that draws to
            // a display: a camera stack's final blit (the last overlay camera's) would otherwise copy
            // over her.
            if (contextEnd && cam.targetTexture == null)
            {
                // …but test against THIS camera's depth: by then another camera's may be bound. With the
                // keeping off, nothing is kept and DrawNow draws her untested there.
                keepingDepth.Run(keepDepthOf ??= KeepDepthOf, cam, warn);
                mainRendered = true;
                return;
            }
            // A main camera that renders into a texture the game then shows (a retro game's low-resolution
            // screen, put on the display by a later camera or a canvas) is drawn now, against its own fresh
            // depth: that is before anything presents the texture, and before this camera clears it again
            // next frame. At the context's end a later camera or canvas would already have shown it without her.
            // Unless it heads a URP camera stack, whose last Overlay camera copies over her (TextureStack):
            // then its depth is kept now, and she is drawn when the stack is done (DrawStacked).
            if (textureStack.Defer(cam, contextEnd && cam.targetTexture != null))
            {
                keepingDepth.Run(keepDepthOf ??= KeepDepthOf, cam, warn);
                return;
            }
            DrawIntoTarget(cam, keptDepth: false);
        }
        /// <summary>Her draw owed at the end of the main camera's URP stack (<see cref="TextureStack"/>),
        /// into its texture, against the depth kept at its own end.</summary>
        void DrawStacked()
        {
            if (faulted || main == null || compositor == null || main.targetTexture == null) return;
            DrawIntoTarget(main, keptDepth: true);
        }
        /// <summary>Draw her into the texture <paramref name="cam"/> renders into (or its target), and
        /// say once per camera and size that she is there.</summary>
        void DrawIntoTarget(Camera cam, bool keptDepth)
        {
            int drawn = compositor.Drawn;
            DrawAfterCamera(cam, keptDepth);
            var rt = cam.targetTexture;
            if (rt == null || compositor == null || compositor.Drawn == drawn) return;
            // Keyed by size, not by the texture: a game that makes its texture anew each frame must not
            // fill the log, and one that resizes it says the new size. Compared first, so no key is built
            // per frame.
            int id = cam.GetInstanceID(), w = rt.width, h = rt.height;
            if (id == texSaidCam && w == texSaidW && h == texSaidH) return;
            texSaidCam = id;
            texSaidW = w;
            texSaidH = h;
            Once($"texture-target:{id}:{w}x{h}",
                $"drawn into '{rt.name}' {w}x{h} at the end of '{cam.name}'{(keptDepth ? "'s camera stack" : "")} (the game shows that texture)");
        }
        /// <summary>
        /// Was her composite drawn inside <paramref name="cam"/>'s own render this frame? Queued is not
        /// drawn for the URP 2022–2023 adapter: a reflection camera rendered inside the main camera's
        /// begin can empty the renderer's queue, and a URP 16 render graph never calls a plain pass —
        /// then she is composited after the camera this frame, and after 120 frames running the
        /// adapter stands down for good (logged once). The other adapters ran when queued.
        /// </summary>
        bool RanInGraph(Camera cam)
        {
            if (!inGraph) return false;
            if (PassRan(cam))
            {
                passMisses = 0;
                return true;
            }
            if (++passMisses > 120 && UrpHook.Active)
            {
                UrpHook.StandDown("her pass was queued but never ran in the camera for 120 frames");
                log.LogWarning($"compositing after the camera: {UrpHook.Missing}");
            }
            return false;
        }
        /// <summary>Did her queued pass run for <paramref name="cam"/> this frame? Always, for the
        /// adapters whose queued pass runs (HDRP, URP 17); for the URP 2022–2023 one, only if it
        /// stamped this frame and this camera (an extra camera's end comes right after its own render,
        /// before another camera's pass could stamp over it).</summary>
        bool PassRan(Camera cam) =>
            hdrp || !UrpHook.BeforePostAdapter
            || (compositor.PassFrame == Time.frameCount && compositor.PassCamera == cam.GetInstanceID());
        void OnEndContext()
        {
            if (textureStack.Done()) DrawStacked();
            if (!mainRendered) return;
            mainRendered = false;
            if (faulted || main == null || compositor == null) return;
            DrawAfterCamera(main, keptDepth: true);
        }
        void DrawNow((Camera cam, bool keptDepth) a)
        {
            compositor.DrawNow(a.cam, a.keptDepth);
            // What the draw was tested against, once per state: a camera whose depth comes and goes
            // (asked for a frame late, a resolution change) shows each state it went through.
            string state = a.keptDepth && keepingDepth.Off
                ? (keepingOff ??= $"none: {keepingDepth.Name} is off")
                : compositor.DepthState;
            if (state == null || !depthsSaid.Add(state)) return;
            log.LogInfo($"depth: {state}: " + (compositor.DepthTested ? "walls hide her" : "she is drawn over everything"));
        }
        /// <summary>The Built-in pipeline: send at pre-cull, then set up what the camera's own
        /// command buffer draws inside its render, after its transparent objects — or after its image
        /// effects (Picture.BeforePostProcessing off) — the picture newest by then.</summary>
        void OnBuiltInPreCull(Camera cam)
        {
            OnBeginCamera(cam);
            if (faulted || cam != main || compositor == null) return;
            // M6: her command buffer only while she is actually connected and drawn — never on a camera
            // nobody is compositing her into (every desktop session with no engine, before this) — and
            // with it, in the after-image-effects mode only, the depth texture's pre-pass she is tested
            // against there. Detaching switches that depth texture off again if PrepareBuiltIn switched it on.
            if (compositor.Show) Fenced("draw", prepareBuiltIn ??= PrepareBuiltIn, cam);
            else compositor.DetachBuiltIn();
        }
    }
}
