// SPDX-License-Identifier: MIT
using System;
using System.Runtime.CompilerServices;
using Astra.Bridge.Capture;
using UnityEngine;
#if !IL2CPP
using UnityEngine.Rendering;
#endif

namespace Astra.Unity
{
    /// <summary>
    /// A test aid's diagnostic (<c>ASTRA_CAPTURE_TEST</c> only): are the bones a capture reads in the
    /// driver's <c>LateUpdate</c> still the bones the game DRAWS? A game that moves them after that (its
    /// own late IK, a LateUpdate after ours) would be captured a step early. For the first 120 sampled
    /// frames of the test take, each bone's rotation at our LateUpdate is compared with the same bone's
    /// when the next camera starts to render, and one line says the largest difference. Mono only;
    /// it never touches the capture (its own failure is caught here).
    /// </summary>
    public sealed partial class Driver
    {
        Quaternion[] probeAt;
        PoseRig probeRig;
        float probeMax;
        string probeBone;
        int probeFrames;
        bool probeHooked, probeDone;
#if !IL2CPP
        Action<ScriptableRenderContext, Camera> probeSrp;
        Camera.CameraCallback probeBuiltIn;
#endif

        void ProbeOrder(Capturer c)
        {
            if (probeDone || c.Rig == null || c.Recorder.State != CaptureState.Recording) return;
            try
            {
                ProbeHook();
                probeRig = c.Rig;
                probeRig.Rotations(ref probeAt);
            }
            catch (Exception e)
            {
                probeDone = true;
                log.LogInfo($"capture: the draw-order check could not run here ({e.GetType().Name}): not measured");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void ProbeHook()
        {
            if (probeHooked) return;
            probeHooked = true;
#if IL2CPP
            probeDone = true;
            log.LogInfo("capture: the draw-order check is Mono only: not measured");
#else
            probeSrp = (_, cam) => ProbeRender();
            probeBuiltIn = cam => ProbeRender();
            RenderPipelineManager.beginCameraRendering += probeSrp;
            Camera.onPreCull += probeBuiltIn;
#endif
        }

        void ProbeRender()
        {
            if (probeDone || probeAt == null || probeRig == null) return;
            try
            {
                var now = new Quaternion[probeAt.Length];
                probeRig.Rotations(ref now);
                for (int i = 0; i < now.Length; i++)
                {
                    float a = Quaternion.Angle(probeAt[i], now[i]);
                    if (a > probeMax)
                    {
                        probeMax = a;
                        probeBone = probeRig.Skeleton.Bones[i];
                    }
                }
                probeAt = null; // once per sampled frame: the first camera after it
                if (++probeFrames >= 120)
                {
                    probeDone = true;
                    log.LogInfo(probeMax < 0.5f
                        ? $"capture: draw-order check: over {probeFrames} frames the bones read in LateUpdate are the bones drawn (largest change before the camera: {probeMax:0.00}°)"
                        : $"capture: draw-order check: something moves the bones AFTER the capture reads them — {probeBone} by up to {probeMax:0.0}° over {probeFrames} frames (the clip is a step early there)");
                    StopProbe();
                }
            }
            catch (Exception e)
            {
                probeDone = true;
                log.LogInfo($"capture: the draw-order check failed ({e.GetType().Name}): not measured");
            }
        }

        void StopProbe()
        {
#if !IL2CPP
            try
            {
                if (probeSrp != null) RenderPipelineManager.beginCameraRendering -= probeSrp;
                if (probeBuiltIn != null) Camera.onPreCull -= probeBuiltIn;
            }
            catch (Exception)
            {
                // A diagnostic's unhook: nothing to undo when the hook never took.
            }
            probeSrp = null;
            probeBuiltIn = null;
#endif
            probeAt = null;
            probeRig = null;
        }
    }
}
