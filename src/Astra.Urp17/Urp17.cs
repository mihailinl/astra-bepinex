// SPDX-License-Identifier: MIT
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Astra.Unity
{
    /// <summary>
    /// URP 17 (Unity 6) renders through a RENDER GRAPH, and the camera's depth texture is a graph
    /// TRANSIENT: it exists only while the graph runs, and is back in the pool — reused, overwritten —
    /// by the time the camera has finished. So in URP 17 she is composited INSIDE the camera's graph,
    /// by a pass the plugin enqueues every frame, right after post-processing and before the UI, that
    /// declares the depth texture as its input (which also makes URP produce it).
    /// <para>
    /// Its own assembly, compiled against Unity 6 and URP 17, so the plugin itself keeps compiling
    /// against Unity 2021.3 and loading in every game: <c>UrpHook</c> loads this one only in a URP
    /// game on Unity 6, by reflection, and falls back if anything here does not bind.
    /// </para>
    /// </summary>
    static class Urp17
    {
        static AstraPass pass;
        static readonly int IdGameDepth = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>
        /// Does URP run its render graph? A game may switch it off ("Compatibility Mode", the
        /// URP_COMPATIBILITY_MODE define since 6000.3): URP then calls the pass's Execute, never
        /// RecordRenderGraph — the pass implements both. Called by reflection (<c>UrpHook</c>), for
        /// the log.
        /// </summary>
        static bool RenderGraphOn()
        {
            var settings = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
            return settings == null || !settings.enableRenderCompatibilityMode;
        }

        /// <summary>Queue her pass on <paramref name="cam"/>'s renderer for this frame. False when
        /// this camera has no URP renderer (the caller then composites the old way). Called by
        /// reflection (<c>UrpHook</c>).</summary>
        static bool Enqueue(Camera cam, Compositor compositor)
        {
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

            sealed class PassData
            {
                public Compositor Compositor;
                public Camera Camera;
                public TextureHandle Depth;
                public bool IntoTexture;
                public MaterialPropertyBlock Block;
            }

            // The game's depth goes in a property block, never on the material: a material value
            // would shadow the GLOBAL _CameraDepthTexture the other pipelines' paths read.
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

            public AstraPass(Compositor compositor)
            {
                Compositor = compositor;
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            /// <summary>
            /// URP in Compatibility Mode (no render graph): URP calls Execute instead. Her pass then
            /// draws into the camera's colour target right after post-processing — BEFORE the
            /// camera's final blit and anything a game captures at AfterRendering (Content Warning's
            /// video recorder) — with the camera's depth texture bound globally (ConfigureInput).
            /// </summary>
#pragma warning disable CS0618, CS0672 // the Compatibility-Mode API is obsolete, and it is what such a game runs
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cam = renderingData.cameraData.camera;
                if (!Compositor.PrepareForPass(cam, intoTexture: true, block)) return;
                var cmd = CommandBufferPool.Get("Astra");
                cmd.DrawProcedural(Matrix4x4.identity, Compositor.Material, 0, MeshTopology.Triangles, 3, 1, block);
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
#pragma warning restore CS0618, CS0672

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frame)
            {
                var res = frame.Get<UniversalResourceData>();
                var cam = frame.Get<UniversalCameraData>();
                if (!res.cameraDepthTexture.IsValid() || !res.activeColorTexture.IsValid()) return;
                using (var b = graph.AddRasterRenderPass<PassData>("Astra", out var d))
                {
                    d.Compositor = Compositor;
                    d.Camera = cam.camera;
                    d.Depth = res.cameraDepthTexture;
                    d.IntoTexture = !res.isActiveTargetBackBuffer;
                    d.Block = block;
                    b.UseTexture(res.cameraDepthTexture, AccessFlags.Read);
                    b.SetRenderAttachment(res.activeColorTexture, 0, AccessFlags.ReadWrite);
                    b.AllowPassCulling(false);
                    b.SetRenderFunc((PassData p, RasterGraphContext ctx) =>
                    {
                        if (!p.Compositor.PrepareForPass(p.Camera, p.IntoTexture, p.Block)) return;
                        p.Block.SetTexture(IdGameDepth, p.Depth);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, p.Compositor.Material, 0, MeshTopology.Triangles, 3, 1, p.Block);
                    });
                }
            }
        }
    }
}
