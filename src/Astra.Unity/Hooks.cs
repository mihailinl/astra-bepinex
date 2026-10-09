// SPDX-License-Identifier: MIT
using System;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// Where the foundation meets the game's render PIPELINE, settled once at the Driver's start: which
    /// pipeline renders the game (Built-in, URP or HDRP), the adapter that draws her inside its render
    /// where one loads, and the render events the Driver's callbacks are subscribed to — each through
    /// whichever route the game's build left (<see cref="Compat"/>, whose <see cref="Compat.UnhookAll"/>
    /// undoes them). It is part of her ESSENTIAL chain: a game whose cameras cannot be hooked cannot
    /// show her — and says so in an answer, never a throw.
    /// </summary>
    sealed class Hooks
    {
        /// <summary>A Scriptable Render Pipeline (URP or HDRP) renders the game; false: the Built-in one.</summary>
        public bool Srp { get; private set; }

        /// <summary>That Scriptable Render Pipeline is HDRP.</summary>
        public bool Hdrp { get; private set; }

        /// <summary>The SRP reports the end of a whole render context: she is drawn there, after every
        /// camera of the frame, rather than right after the main one.</summary>
        public bool ContextEnd { get; private set; }

        /// <summary>
        /// Find the pipeline, load its adapter and subscribe the Driver's callbacks: the SRP's camera
        /// begin and end (and its context end, where the build has it), or the Built-in pipeline's
        /// pre-cull. False when this build cannot tell when its cameras render: nothing is left
        /// subscribed then, and the caller lets its compositor go — there is no moment to draw her in.
        /// </summary>
        public bool Attach(Settings settings, Compositor compositor, ManualLogSource log, Action<Camera> beginCamera,
            Action<Camera> endCamera, Action endContext, Action<Camera> preCull)
        {
            Srp = GraphicsSettings.currentRenderPipeline != null;
            string pipeline = Compat.PipelineClass();
            Hdrp = Srp && pipeline != null && pipeline.EndsWith(".HDRenderPipelineAsset", StringComparison.Ordinal);
            UrpHook.BeforePost = settings.BeforePostProcessing.Value;
            UrpHook.TryLoad();
            HdrpHook.TryLoad();
            if (Hdrp && !HdrpHook.Active && compositor != null)
            {
                // Without its adapter, HDRP's depth (a mip-pyramid atlas array) cannot be read.
                compositor.DepthTest = false;
                log.LogWarning($"HDRP without its adapter ({HdrpHook.Missing}): she is drawn over everything");
            }
            if (!Srp)
            {
                Compat.HookBuiltIn(preCull);
                return true;
            }
            if (!Compat.HookSrp(beginCamera, endCamera, out string how))
            {
                log.LogError($"this game's build cannot tell when its cameras render ({how}): Astra cannot be drawn here");
                return false;
            }
            log.LogInfo($"camera events through {how}");
            ContextEnd = Compat.HookSrpContextEnd(endContext, out string howEnd);
            log.LogInfo(ContextEnd ? $"context end through {howEnd}"
                : $"no context end ({howEnd}): she is drawn right after the main camera");
            return true;
        }

        /// <summary>Where she is composited in this game, in one line for the log (after <see cref="Attach"/>).</summary>
        public string Compositing(bool beforePostProcessing) =>
            !Srp
                ? beforePostProcessing
                    ? "compositing inside the main camera's Built-in render, right after its transparent objects and before its image effects (the game's look is on her too)"
                    : "compositing after the main camera's image effects (Built-in), against its depth texture"
                : UrpHook.Active && !Application.unityVersion.StartsWith("6000.", StringComparison.Ordinal)
                ? "compositing inside the camera's URP render, after its transparent objects, before post-processing (the game's look is on her too)"
                : UrpHook.Active ? $"compositing inside the camera's URP 17 render ({(UrpHook.RenderGraph ? "render graph" : "Compatibility Mode")}), after post-processing"
                : HdrpHook.Active ? "compositing in an HDRP custom pass, against the camera's depth buffer"
                : $"compositing after {(ContextEnd ? "the frame's cameras" : "the camera")} ({(Hdrp ? HdrpHook.Missing : UrpHook.Missing)})";
    }
}
