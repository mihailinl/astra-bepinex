// SPDX-License-Identifier: MIT
using System;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Astra.Unity
{
    /// <summary>
    /// URP 13–16 (Unity 2022.1–2023): she is drawn INSIDE the camera's render, right before its
    /// transparent objects (or after them: [Picture] BeforeTransparents) and its post-processing —
    /// so the game's own look is laid over her
    /// as over everything else in its world: its colour grading, and the renderer features a stylised
    /// game builds its picture with (Extermination Ship's pixelation, dithering and posterisation —
    /// composited over the finished frame she stood out sharp and clean among them, the owner's
    /// first look, 2026-10-08).
    /// <para>
    /// The GPU tests her depth against the camera's depth BUFFER (the composite shader's pass 1
    /// outputs it as SV_Depth, as in HDRP), not against the depth TEXTURE; then pass 2 WRITES her
    /// depth into that buffer where she is solid, before the transparent objects are drawn. URP
    /// 14's depth TEXTURE holds her only when it is copied after the transparents (the renderer's
    /// "After Transparents" copy mode, or when only post-processing asks for depth: depth of field
    /// and motion blur then see her); the usual copy (after the opaques, when the camera asks for a
    /// depth texture) and a prepass's (SSAO, depth priming) are made before her, so an effect that
    /// reads those does not see her. The opaque texture (refraction) is copied before her too: a
    /// refractive surface in front of her that writes that copy as its colour hides her. URP's TAA
    /// has no motion vectors for her.
    /// </para>
    /// <para>
    /// Its own assembly, compiled against URP 14, so the plugin itself keeps compiling against Unity
    /// 2021.3 and loading in every game: <c>UrpHook</c> loads this one only in a URP game on Unity
    /// 2022–2023 and falls back (her picture composited over the finished frame) if anything here
    /// does not bind — which it PROVES before the pass goes in (<c>AdapterProof.Prove</c> compiles all
    /// of this class at load: a URP 13, 15 or 16 that lacks a member it names gets her over the
    /// finished frame, and one line in the log, never a broken camera). Whatever still fails inside
    /// URP's render is caught there and switches the pass off (<see cref="Failed"/>).
    /// </para>
    /// </summary>
    static class Urp14
    {
        static AstraPass pass;

        /// <summary>Her pass inside URP's own loop: what it throws there (API drift on URP 13/15/16 —
        /// this is compiled against 14 — or anything else) is caught there, so it never breaks the
        /// game's render, and switches it off for the session.</summary>
        static readonly Feature running = new Feature("her pass inside the camera's URP render");

        /// <summary>Why her pass switched itself off inside URP's render; null while it runs. Read by
        /// reflection (<c>UrpHook</c>) before every <see cref="Enqueue"/>: it then stands the adapter
        /// down.</summary>
        static string Failed() => running.Why;

        /// <summary>Queue her pass on <paramref name="cam"/>'s renderer for this frame. False when this
        /// camera has no URP renderer or the pass is off (the caller then composites the old way).
        /// Called by reflection (<c>UrpHook</c>).</summary>
        static bool Enqueue(Camera cam, Compositor compositor)
        {
            if (running.Off) return false;
            var data = cam.GetUniversalAdditionalCameraData();
            var renderer = data != null ? data.scriptableRenderer : null;
            if (renderer == null) return false;
            if (pass == null || pass.Compositor != compositor) pass = new AstraPass(compositor);
            pass.Before = compositor.SrpBeforeTransparents();
            pass.renderPassEvent = pass.Before ? RenderPassEvent.BeforeRenderingTransparents : RenderPassEvent.AfterRenderingTransparents;
            renderer.EnqueuePass(pass);
            return true;
        }

        sealed class AstraPass : ScriptableRenderPass
        {
            public readonly Compositor Compositor;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            bool intoTexture;
            /// <summary>Drawn before the transparent objects, writing her depth (see the constructor).</summary>
            public bool Before = true;

            public AstraPass(Compositor compositor)
            {
                Compositor = compositor;
                // Set per frame by Enqueue: BeforeRenderingTransparents — after the opaque world, its
                // sky, its screen-space decals and the opaque-texture copy (all at AfterRenderingSkybox:
                // drawn after her there, a screen-space decal reconstructs the WALL from the depth
                // texture and paints it over her), and before the transparent objects and the
                // post-processing. Her colour is tested against the depth buffer (pass 1), then her
                // depth is written into it (pass 2): a transparent object IN FRONT of her (particles,
                // glass, water) is blended over her, one BEHIND her (haze, sky domes) fails the depth
                // test where she stands. Enqueued at the camera's start, before the renderer's own
                // passes (its transparent pass is at this same event) and its features', so she is
                // drawn first. AfterRenderingTransparents with pass 1 alone is Astra 0.6.1's point.
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
            }

            // Each entry URP calls is a FENCE around a body in its own method: a body that does not
            // compile fails at the fence's call, inside its try — never in URP's frame. NoInlining
            // keeps the body out of the fence.

            /// <summary>No target configured: URP draws the pass into the camera's own colour and depth
            /// targets. Whether that colour target is a texture (it is whenever the camera post-processes
            /// or a feature needs it) or the back buffer decides the picture's vertical flip.</summary>
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                if (running.Off) return;
                try { Setup(ref renderingData); }
                catch (Exception e) { running.Fail(e); }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Setup(ref RenderingData renderingData)
            {
                var target = renderingData.cameraData.renderer.cameraColorTargetHandle;
                intoTexture = target == null || target.nameID != new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (running.Off) return;
                try { Draw(context, ref renderingData); }
                catch (Exception e) { running.Fail(e); }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Draw(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cam = renderingData.cameraData.camera;
                if (!Compositor.PrepareForPass(cam, intoTexture, block)) return;
                var cmd = CommandBufferPool.Get("Astra");
                cmd.DrawProcedural(Matrix4x4.identity, Compositor.Material, Compositor.DepthBufferPass, MeshTopology.Triangles, 3, 1, block);
                if (Before) cmd.DrawProcedural(Matrix4x4.identity, Compositor.Material, Compositor.HerDepthPass, MeshTopology.Triangles, 3, 1, block);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
        }
    }
}
