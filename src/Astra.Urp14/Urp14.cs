// SPDX-License-Identifier: MIT
using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Astra.Unity
{
    /// <summary>
    /// URP 13–16 (Unity 2022.1–2023): she is drawn INSIDE the camera's render, right after its
    /// opaque objects and sky and BEFORE its post-processing — so the game's own look is laid over her
    /// as over everything else in its world: its colour grading, and the renderer features a stylised
    /// game builds its picture with (Extermination Ship's pixelation, dithering and posterisation —
    /// composited over the finished frame she stood out sharp and clean among them, the owner's
    /// first look, 2026-10-08).
    /// <para>
    /// She is depth-tested against the camera's depth BUFFER and writes her own depth into it (the
    /// composite shader's pass 1, as in HDRP), not against the depth TEXTURE, which URP copies at
    /// this point or later — after her, so she is in it too, and a depth-based effect (an outline, a
    /// fog, a depth of field) sees her like any other object. (With a depth PREPASS the depth texture
    /// is made before any of this, without her; URP's TAA has no motion vectors for her.)
    /// </para>
    /// <para>
    /// Its own assembly, compiled against URP 14, so the plugin itself keeps compiling against Unity
    /// 2021.3 and loading in every game: <c>UrpHook</c> loads this one only in a URP game on Unity
    /// 2022–2023 and falls back (her picture composited over the finished frame) if anything here
    /// does not bind.
    /// </para>
    /// </summary>
    static class Urp14
    {
        static AstraPass pass;

        /// <summary>What a pass threw inside URP's own loop (API drift on URP 13/15/16 — this is
        /// compiled against 14 — or anything else): caught there, so it never breaks the game's
        /// render, and surfaced by the next <see cref="Enqueue"/>, which the foundation fences.</summary>
        static Exception failed;

        /// <summary>Queue her pass on <paramref name="cam"/>'s renderer for this frame. False when this
        /// camera has no URP renderer (the caller then composites the old way). Throws once the pass
        /// has failed, which drops the adapter. Called by reflection (<c>UrpHook</c>).</summary>
        static bool Enqueue(Camera cam, Compositor compositor)
        {
            if (failed != null) throw new InvalidOperationException($"her pass failed: {failed.GetType().Name}: {failed.Message}");
            var data = cam.GetUniversalAdditionalCameraData();
            var renderer = data != null ? data.scriptableRenderer : null;
            if (renderer == null) return false;
            if (pass == null || pass.Compositor != compositor) pass = new AstraPass(compositor);
            renderer.EnqueuePass(pass);
            return true;
        }

        sealed class AstraPass : ScriptableRenderPass
        {
            public readonly Compositor Compositor;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
            bool intoTexture;

            public AstraPass(Compositor compositor)
            {
                Compositor = compositor;
                // After the world's opaque objects and its sky, before its transparent ones: glass,
                // particles and haze in front of her are drawn over her, those behind her are hidden by
                // the depth she writes. Enqueued at the camera's start, before the renderer's own passes
                // and its features', so at this same event she is drawn first — before URP copies the
                // depth (its "after opaques" mode) and the colour (for refraction): both hold her.
                renderPassEvent = RenderPassEvent.AfterRenderingSkybox;
            }

            /// <summary>No target configured: URP draws the pass into the camera's own colour and depth
            /// targets. Whether that colour target is a texture (it is whenever the camera post-processes
            /// or a feature needs it) or the back buffer decides the picture's vertical flip.</summary>
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                try
                {
                    var target = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    intoTexture = target == null || target.nameID != new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
                }
                catch (Exception e)
                {
                    failed ??= e;
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (failed != null) return;
                try
                {
                    var cam = renderingData.cameraData.camera;
                    if (!Compositor.PrepareForPass(cam, intoTexture, block)) return;
                    var cmd = CommandBufferPool.Get("Astra");
                    cmd.DrawProcedural(Matrix4x4.identity, Compositor.Material, Compositor.DepthBufferPass, MeshTopology.Triangles, 3, 1, block);
                    context.ExecuteCommandBuffer(cmd);
                    CommandBufferPool.Release(cmd);
                }
                catch (Exception e)
                {
                    failed ??= e;
                }
            }
        }
    }
}
