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
        static float log2Exposure;
        static bool hasLog2;
        static float lastReadbackAt = -1;
        static Camera lastCamera;
        static float cameraChangedAt = -100;

        /// <summary>
        /// The camera's current EXPOSURE multiplier: HDRP's lights are in physical units (lux,
        /// candela) and its picture is exposed, so her light is brought to the picture's scale with
        /// it. HDRP keeps it on the GPU (a 1×1 texture its shaders read, <c>GetCurrentExposureMultiplier</c>);
        /// it is read back a few times a second, smoothed in LOG2 with a 3 s time constant (a jump
        /// cut's exposure should ease like the game's own auto-exposure, not snap). A reading taken
        /// within 1 s of a camera CHANGE is dropped: HDRP's texture (and its history) briefly still
        /// reflects the old camera, and folding that in would yank her light toward it. 0 until the
        /// first valid reading. Called by reflection.
        /// </summary>
        static float Exposure(Camera cam)
        {
            if (cam != lastCamera)
            {
                lastCamera = cam;
                cameraChangedAt = Time.unscaledTime;
            }
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
                        if (!(v.x > 0 && !float.IsInfinity(v.x) && !(v.x == 1 && v.y == 0))) return;
                        if (Time.unscaledTime - cameraChangedAt < 1f) return; // still the old camera's history
                        float target = Mathf.Log(v.x, 2);
                        float now = Time.unscaledTime;
                        if (!hasLog2)
                        {
                            log2Exposure = target;
                            hasLog2 = true;
                        }
                        else
                        {
                            float dt = Mathf.Max(0, now - lastReadbackAt);
                            log2Exposure += (target - log2Exposure) * (1 - Mathf.Exp(-dt / 3f));
                        }
                        lastReadbackAt = now;
                        exposure = Mathf.Pow(2, log2Exposure);
                    });
                }
            }
            return exposure > 0 ? exposure : 0;
        }

        static FieldInfo skyManager;
        static MethodInfo ambientProbe;

        /// <summary>
        /// HDRP's AMBIENT light for <paramref name="cam"/> — the sky's probe it lights every moving thing
        /// with where no light probe reaches. HDRP does not keep <c>RenderSettings.ambientProbe</c>, so
        /// that one reads black, and she went black wherever the sun did not reach her. Physical units,
        /// as the game's own lights. All zero when it cannot be read. Called by reflection
        /// (<c>SkyManager.GetAmbientProbe(HDCamera)</c> is internal, HDRP 10–17).
        /// </summary>
        static SphericalHarmonicsL2 Ambient(Camera cam)
        {
            var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
            if (pipeline == null || cam == null) return default;
            skyManager ??= typeof(HDRenderPipeline).GetField("m_SkyManager", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var sky = skyManager?.GetValue(pipeline);
            if (sky == null) return default;
            ambientProbe ??= sky.GetType().GetMethod("GetAmbientProbe",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(HDCamera) }, null);
            return ambientProbe?.Invoke(sky, new object[] { HDCamera.GetOrCreate(cam) }) is SphericalHarmonicsL2 sh ? sh : default;
        }

        /// <summary>
        /// HDRP's FOG VOLUME — the density + colour her picture, composited after HDRP's own fog,
        /// never sees. <c>density = 1/meanFreePath</c> (HDRP's own unit: the distance at which 63% of
        /// the light behind a surface is lost); the colour is the fog's authored albedo, tinted by
        /// the sky's ambient (the light the fog itself scatters INTO the view, same as a real haze
        /// picks up the sky's colour) and brought to the picture's scale by the camera's exposure — the
        /// same two numbers <see cref="Ambient"/> and <see cref="Exposure"/> already read, so this adds
        /// no new readback. <c>(false, …)</c> when there is no active Fog override. Called by
        /// reflection.
        /// </summary>
        static (bool enabled, float density, Color color) ReadFog(Camera cam)
        {
            // The scene's global Fog override, and every Local Volumetric Fog box the camera stands
            // in (how many games build their haze — Lethal Company's indoor and weather fog is only
            // local boxes): the densest wins.
            float meanFreePath = float.PositiveInfinity;
            Color albedo = Color.black;
            var stack = VolumeManager.instance?.stack;
            var fog = stack?.GetComponent<UnityEngine.Rendering.HighDefinition.Fog>();
            if (fog != null && fog.active && fog.enabled.value)
            {
                meanFreePath = fog.meanFreePath.value;
                albedo = fog.albedo.value;
            }
            if (cam != null)
            {
                RefreshLocalFogs();
                var at = cam.transform.position;
                foreach (var box in localFogs)
                {
                    if (box == null || !box.isActiveAndEnabled) continue;
                    var p = box.parameters;
                    var local = Quaternion.Inverse(box.transform.rotation) * (at - box.transform.position);
                    if (Mathf.Abs(local.x) > p.size.x * 0.5f || Mathf.Abs(local.y) > p.size.y * 0.5f || Mathf.Abs(local.z) > p.size.z * 0.5f) continue;
                    if (p.meanFreePath < meanFreePath)
                    {
                        meanFreePath = p.meanFreePath;
                        albedo = p.albedo;
                    }
                }
            }
            if (float.IsInfinity(meanFreePath)) return (false, 0f, Color.black);
            float density = 1f / Mathf.Max(meanFreePath, 0.01f);
            // The fog scatters the light around it into the view: the sky's, where the scene has one;
            // with none readable (a lamp-lit interior), its albedo at the picture's exposure, which the
            // engine then holds under her own light.
            float exposure = Exposure(cam);
            float ambientMean = MeanLuma(Ambient(cam));
            float scale = (exposure > 0 ? exposure : 1f) * (ambientMean > 1e-4f ? ambientMean : 1f);
            Color color = new Color(albedo.r * scale, albedo.g * scale, albedo.b * scale, 1f);
            return (true, density, color);
        }

        static readonly System.Collections.Generic.List<LocalVolumetricFog> localFogs = new System.Collections.Generic.List<LocalVolumetricFog>();
        static float nextFogScan;

        /// <summary>The scene's Local Volumetric Fog boxes, rescanned every 2 s (a box can appear with a
        /// weather change or a level load).</summary>
        static void RefreshLocalFogs()
        {
            if (Time.unscaledTime < nextFogScan) return;
            nextFogScan = Time.unscaledTime + 2f;
            localFogs.Clear();
            localFogs.AddRange(Object.FindObjectsOfType<LocalVolumetricFog>());
        }

        /// <summary>The mean luminance of a spherical-harmonics probe's DC term (band 0) — the
        /// ambient's overall level, used to tint HDRP's fog with the sky it scatters.</summary>
        static float MeanLuma(SphericalHarmonicsL2 sh)
        {
            // Band 0 (l=0) is a constant term per channel: sh[c, 0] * k0 is that channel's share of a
            // uniform ambient (Ramamoorthi & Hanrahan); k0 = 1/(2*sqrt(PI)) for the normalisation
            // Unity's SphericalHarmonicsL2 uses.
            const float k0 = 0.282095f;
            float r = Mathf.Max(0, sh[0, 0] * k0), g = Mathf.Max(0, sh[1, 0] * k0), b = Mathf.Max(0, sh[2, 0] * k0);
            return 0.2126f * r + 0.7152f * g + 0.0722f * b;
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
