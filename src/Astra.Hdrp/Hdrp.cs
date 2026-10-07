// SPDX-License-Identifier: MIT
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Astra.Unity
{
    /// <summary>
    /// HDRP keeps the camera's depth as a mip-pyramid ATLAS (a texture array on most APIs) that a
    /// plain shader cannot sample, and frees it when the camera is done. So in HDRP she is drawn by a
    /// CUSTOM PASS on the main camera, after post-processing, with the camera's depth BUFFER bound
    /// beside its colour: she writes her own depth and the GPU tests it (the shader's pass 1).
    /// <para>
    /// Its own assembly, compiled against HDRP (10+: <c>CustomPass.Execute(CustomPassContext)</c>), loaded
    /// by reflection only in an HDRP game (<c>Adapters</c>), so the plugin itself never depends on HDRP.
    /// </para>
    /// </summary>
    static class Hdrp
    {
        static CustomPassVolume volume;
        static AstraPass pass;

        /// <summary>Put her pass on <paramref name="main"/>'s render (once; again when the main camera
        /// changes). Called by reflection.</summary>
        static bool Attach(Camera main, Compositor compositor)
        {
            if (volume == null)
            {
                var host = new GameObject("Astra (HDRP pass)") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(host);
                volume = host.AddComponent<CustomPassVolume>();
                volume.isGlobal = true;
                volume.injectionPoint = CustomPassInjectionPoint.AfterPostProcess;
                volume.priority = 1000;
                pass = new AstraPass { name = "Astra" };
                volume.customPasses.Add(pass);
            }
            pass.Compositor = compositor;
            volume.targetCamera = main; // only the camera she is composited for
            return true;
        }

        static float exposure = -1;
        static bool reading;
        static float nextRead;
        static MethodInfo exposureTexture;
        static bool noExposure;

        /// <summary>
        /// The camera's current EXPOSURE multiplier: HDRP's lights are in physical units (lux,
        /// candela) and its picture is exposed, so her light is brought to the picture's scale with
        /// it. HDRP keeps it on the GPU (a 1×1 texture its shaders read, <c>GetCurrentExposureMultiplier</c>);
        /// it is read back a few times a second. 0 until known. Called by reflection.
        /// </summary>
        static float Exposure(Camera cam)
        {
            if (!reading && !noExposure && cam != null && Time.unscaledTime >= nextRead)
            {
                nextRead = Time.unscaledTime + 0.25f;
                // HDRenderPipeline.GetExposureTexture(HDCamera) is internal (HDRP 10–17): reflection.
                exposureTexture ??= typeof(HDRenderPipeline).GetMethod("GetExposureTexture",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(HDCamera) }, null);
                var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
                if (exposureTexture == null || pipeline == null)
                {
                    noExposure = exposureTexture == null;
                }
                else if (exposureTexture.Invoke(pipeline, new object[] { HDCamera.GetOrCreate(cam) }) is RTHandle h && h.rt != null)
                {
                    reading = true;
                    AsyncGPUReadback.Request(h.rt, 0, TextureFormat.RGFloat, r =>
                    {
                        reading = false;
                        if (r.hasError) return;
                        var v = r.GetData<Vector2>()[0];
                        // (1, 0) is HDRP's EMPTY exposure texture (a camera cut, a history reset):
                        // not an exposure, keep the last.
                        if (v.x > 0 && !float.IsInfinity(v.x) && !(v.x == 1 && v.y == 0)) exposure = v.x;
                    });
                }
            }
            return exposure > 0 ? exposure : 0;
        }

        sealed class AstraPass : CustomPass
        {
            public Compositor Compositor;
            readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

            protected override void Execute(CustomPassContext ctx)
            {
                var c = Compositor;
                if (c == null || !c.PrepareForPass(ctx.hdCamera.camera, intoTexture: true, block)) return;
                CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, c.Material, Compositor.DepthBufferPass, MeshTopology.Triangles, 3, 1, block);
            }
        }
    }
}
