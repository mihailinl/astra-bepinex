// SPDX-License-Identifier: MIT
using System;
using System.Runtime.CompilerServices;
using Astra.Bridge;
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
    /// <para>
    /// ONE build for every Unity 6 line (6000.0 to 6000.3 and on). Its IL names a member together with
    /// the type that declared it when this was compiled, and a member that later MOVES is "not found"
    /// in the line it moved in, as the method that names it is compiled: inside URP's own recording,
    /// which then drops the camera's whole render graph, every frame — a black screen (foundation 0.8.0,
    /// compiled against 6000.0, in PEAK on 6000.3; and 0.5–0.7, compiled against 6000.3, in any 6000.0–
    /// 6000.2 game that runs the render graph). The one member that moved — <c>SetRenderAttachment</c>,
    /// from <c>IRasterRenderGraphBuilder</c> (6000.0–6000.2) into the new
    /// <c>IRenderAttachmentRenderGraphBuilder</c> it extends since 6000.3 — is therefore never named
    /// here: <see cref="Bind"/> finds it wherever this game declares it, once, and the pass calls it
    /// through a bound delegate. Everything else this class names is the same in every line; the
    /// foundation PROVES that before the pass goes in (<c>AdapterProof.Prove</c> compiles all of this
    /// class at load), so a line that lacks anything else gets her after the camera instead, and one
    /// line in the log. Whatever still fails inside the render is caught there and switches the pass
    /// off (<see cref="Failed"/>): URP never sees it, and it is never repeated.
    /// </para>
    /// <para>
    /// What is OPTIONAL (the light reading, the log's mode) is in <see cref="Urp17Light"/>, outside the
    /// proof: a line without it loses only that.
    /// </para>
    /// </summary>
    static class Urp17
    {
        static AstraPass pass;
        static readonly int IdGameDepth = Shader.PropertyToID("_CameraDepthTexture");

        /// <summary>Her pass inside URP's render: its first failure there switches it off for the
        /// session (said once, by the foundation, as it stands the adapter down).</summary>
        static readonly Feature running = new Feature("her pass inside the camera's URP 17 render");

        /// <summary><c>SetRenderAttachment(texture, index, flags, mipLevel, depthSlice)</c> — the
        /// overload every line declares abstract — on whichever interface this game declares it.</summary>
        static Action<IRasterRenderGraphBuilder, TextureHandle, int, AccessFlags, int, int> setRenderAttachment;

        /// <summary>Bind what differs between Unity 6 lines, once, before the first <see cref="Enqueue"/>.
        /// Throws <see cref="MissingMethodException"/> naming the member when this game declares none.
        /// Called by reflection (<c>UrpHook</c>), after the proof.</summary>
        static void Bind()
        {
            setRenderAttachment = AdapterProof.InterfaceCall<Action<IRasterRenderGraphBuilder, TextureHandle, int, AccessFlags, int, int>>(
                "SetRenderAttachment");
        }

        /// <summary>Why her pass switched itself off inside URP's render; null while it runs. Read by
        /// reflection (<c>UrpHook</c>) before every <see cref="Enqueue"/>: it then stands the adapter
        /// down, and she is composited after the camera.</summary>
        static string Failed() => running.Why;

        /// <summary>Queue her pass on <paramref name="cam"/>'s renderer for this frame. False when
        /// this camera has no URP renderer, or the pass is off or unbound (the caller then composites
        /// the old way). Called by reflection (<c>UrpHook</c>).</summary>
        static bool Enqueue(Camera cam, Compositor compositor)
        {
            if (running.Off || setRenderAttachment == null) return false;
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

            // Made once: the render function URP keeps for the pass (no delegate per frame).
            static readonly BaseRenderFunc<PassData, RasterGraphContext> drawFunc = DrawGuarded;

            public AstraPass(Compositor compositor)
            {
                Compositor = compositor;
                renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth);
            }

            // Each entry URP calls is a FENCE around a body in its own method: a body that does not
            // compile fails at the fence's call, inside its try — never in URP's frame, which would
            // drop the camera's graph. NoInlining keeps the body out of the fence.

#pragma warning disable CS0618, CS0672 // the Compatibility-Mode API is obsolete, and it is what such a game runs
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (running.Off) return;
                try { ExecuteCompat(context, ref renderingData); }
                catch (Exception e) { running.Fail(e); }
            }

            /// <summary>
            /// URP in Compatibility Mode (no render graph): URP calls Execute instead. Her pass then
            /// draws into the camera's colour target right after post-processing — BEFORE the
            /// camera's final blit and anything a game captures at AfterRendering (Content Warning's
            /// video recorder) — with the camera's depth texture bound globally (ConfigureInput).
            /// </summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            void ExecuteCompat(ScriptableRenderContext context, ref RenderingData renderingData)
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
                if (running.Off) return;
                try { Record(graph, frame); }
                catch (Exception e) { running.Fail(e); }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            void Record(RenderGraph graph, ContextContainer frame)
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
                    setRenderAttachment(b, res.activeColorTexture, 0, AccessFlags.ReadWrite, 0, -1);
                    b.AllowPassCulling(false);
                    b.SetRenderFunc(drawFunc);
                }
            }

            static void DrawGuarded(PassData p, RasterGraphContext ctx)
            {
                if (running.Off) return;
                try { Draw(p, ctx); }
                catch (Exception e) { running.Fail(e); }
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            static void Draw(PassData p, RasterGraphContext ctx)
            {
                if (!p.Compositor.PrepareForPass(p.Camera, p.IntoTexture, p.Block)) return;
                p.Block.SetTexture(IdGameDepth, p.Depth);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, p.Compositor.Material, 0, MeshTopology.Triangles, 3, 1, p.Block);
            }
        }
    }

    /// <summary>
    /// The URP 17 adapter's OPTIONAL readings, outside the pass's proof (<see cref="Urp17"/>): a line
    /// that lacks what one of them names loses that reading only. Each is bound and fenced on its own
    /// by <c>UrpHook</c>.
    /// </summary>
    static class Urp17Light
    {
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

        /// <summary>
        /// URP's post-exposure multiplier (<c>2^postExposure</c>) from the active volume stack's
        /// <c>ColorAdjustments</c> override — URP applies it to the WHOLE picture after grading, so a
        /// light fact read before that stage (the sun, the ambient cube, a lamp, the fog colour) must
        /// be scaled by it too, or she (and her fog) read dim against a game that pushed exposure up
        /// in its post volume. Null when no such override is active (1 = no change): a scene with no
        /// volume, or one whose <c>postExposure.overrideState</c> is off. Called by reflection
        /// (<c>UrpHook</c>), read once per light message.
        /// </summary>
        static float? PostExposure()
        {
            var stack = VolumeManager.instance.stack;
            var ca = stack?.GetComponent<ColorAdjustments>();
            if (ca == null || !ca.active || !ca.postExposure.overrideState) return null;
            float v = ca.postExposure.value;
            return !float.IsNaN(v) && !float.IsInfinity(v) ? Mathf.Pow(2f, v) : (float?)null;
        }
    }
}
