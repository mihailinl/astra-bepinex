// SPDX-License-Identifier: MIT
// A readability shard of Compat: render-event subscription and command-buffer order.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
#if IL2CPP
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
// The SRP's render-event delegates: an IL2CPP game takes its own delegate types.
using CameraHandler = Il2CppSystem.Action<UnityEngine.Rendering.ScriptableRenderContext, UnityEngine.Camera>;
using ContextHandler = Il2CppSystem.Action<UnityEngine.Rendering.ScriptableRenderContext,
    Il2CppSystem.Collections.Generic.List<UnityEngine.Camera>>;
#else
using CameraHandler = System.Action<UnityEngine.Rendering.ScriptableRenderContext, UnityEngine.Camera>;
using ContextHandler = System.Action<UnityEngine.Rendering.ScriptableRenderContext,
    System.Collections.Generic.List<UnityEngine.Camera>>;
#endif

namespace Astra.Unity
{
    static partial class Compat
    {
        static readonly List<Action> unhook = new List<Action>();

        /// <summary>
        /// Called for each camera as the SRP starts / finishes rendering it. Both or neither: when
        /// either event cannot be subscribed, the other is undone and the answer is false, so a
        /// camera is never prepared for a draw that never comes. <paramref name="how"/> says which
        /// route each took ("accessors" where nothing was stripped) or, on false, why neither worked.
        /// </summary>
        public static bool HookSrp(Action<Camera> begin, Action<Camera> end, out string how)
        {
#if IL2CPP
            var b = DelegateSupport.ConvertDelegate<CameraHandler>(
                new Action<ScriptableRenderContext, Camera>((_, cam) => begin(cam)));
            var e = DelegateSupport.ConvertDelegate<CameraHandler>(
                new Action<ScriptableRenderContext, Camera>((_, cam) => end(cam)));
            Action<CameraHandler, bool> beginField = BeginByField, endField = EndByField;
#else
            CameraHandler b = (_, cam) => begin(cam);
            CameraHandler e = (_, cam) => end(cam);
            // On Mono an event's backing field is private: the event itself is the only route.
            Action<CameraHandler, bool> beginField = null, endField = null;
#endif
            var undoBegin = Subscribe(b, BeginByAccessor, beginField, out bool beginByField, out string whyBegin);
            if (undoBegin == null)
            {
                how = "beginCameraRendering: " + whyBegin;
                return false;
            }
            var undoEnd = Subscribe(e, EndByAccessor, endField, out bool endByField, out string whyEnd);
            if (undoEnd == null)
            {
                TryUndo(undoBegin);
                how = "endCameraRendering: " + whyEnd;
                return false;
            }
            unhook.Add(undoBegin);
            unhook.Add(undoEnd);
            how = !beginByField && !endByField ? "accessors"
                : beginByField && endByField ? $"fields: this build stripped their accessors ({whyBegin})"
                : beginByField
                    ? $"beginCameraRendering's field (this build stripped its accessor: {whyBegin}), endCameraRendering's accessor"
                : $"beginCameraRendering's accessor, endCameraRendering's field (this build stripped its accessor: {whyEnd})";
            return true;
        }

        /// <summary>
        /// Called when the SRP has finished a whole render CONTEXT — every camera, the stack's final
        /// blit included, before the overlay UI. False on a Unity without the event (before 2021.1),
        /// or where neither route to it works: the caller then draws at the end of its camera
        /// instead. <paramref name="how"/> says which route it took ("its accessor" / "its field")
        /// or, on false, why there is none.
        /// </summary>
        public static bool HookSrpContextEnd(Action end, out string how)
        {
#if IL2CPP
            ContextHandler e;
            try
            {
                e = DelegateSupport.ConvertDelegate<ContextHandler>(
                    new Action<ScriptableRenderContext, Il2CppSystem.Collections.Generic.List<Camera>>((_, __) => end()));
            }
            catch (Exception x)
            {
                // The delegate type is the one the event's field declares: a build without the event
                // may not have that type either (an ArgumentException from the conversion, not a
                // stripped member). The event is optional, so this is a "no", never a stop.
                how = "no delegate of its type: " + Why(x);
                return false;
            }
            Action<ContextHandler, bool> field = ContextEndByField;
#else
            ContextHandler e = (_, __) => end();
            Action<ContextHandler, bool> field = null;
#endif
            var undo = Subscribe(e, ContextEndByAccessor, field, out bool byField, out string why);
            if (undo == null)
            {
                how = why;
                return false;
            }
            unhook.Add(undo);
            how = byField ? $"its field (this build stripped its accessor: {why})" : "its accessor";
            return true;
        }

        /// <summary>
        /// Subscribe <paramref name="d"/> to one render event through the first route that works: the
        /// event's own <paramref name="accessor"/> (add/remove; on Mono, <c>+=</c>/<c>-=</c>), then
        /// its backing <paramref name="field"/> — the one Unity's own invoker reads, so writing it
        /// works whatever the build stripped from the accessors (null on Mono, where that field is
        /// private). A route the build removed fails when its
        /// method compiles, and a body Il2CppInterop could not restore when it runs — both inside the
        /// tries here, as <see cref="Stripped"/>. Returns the undo, or null when no route works;
        /// <paramref name="byField"/> says the field took it, and <paramref name="why"/> why the
        /// accessor did not (and, on null, the field too).
        /// </summary>
        static Action Subscribe<T>(T d, Action<T, bool> accessor, Action<T, bool> field, out bool byField, out string why)
        {
            byField = false;
            try
            {
                accessor(d, true);
                why = null;
                return () => accessor(d, false);
            }
            catch (Exception x) when (Stripped(x))
            {
                why = Why(x);
            }
            if (field == null) return null;
            try
            {
                field(d, true);
                byField = true;
                return () => field(d, false);
            }
            catch (Exception x) when (Stripped(x))
            {
                why = $"accessor: {why}; field: {Why(x)}";
                return null;
            }
        }

        // The routes, one method each that never inlines: what the build stripped must fail when THAT
        // method compiles — inside Subscribe's try — never when its caller does. Each route holds its
        // own way off too, so a route whose remove is missing is never taken: nothing could undo it.

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void BeginByAccessor(CameraHandler d, bool on)
        {
#if IL2CPP
            if (on) RenderPipelineManager.add_beginCameraRendering(d);
            else RenderPipelineManager.remove_beginCameraRendering(d);
#else
            if (on) RenderPipelineManager.beginCameraRendering += d;
            else RenderPipelineManager.beginCameraRendering -= d;
#endif
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EndByAccessor(CameraHandler d, bool on)
        {
#if IL2CPP
            if (on) RenderPipelineManager.add_endCameraRendering(d);
            else RenderPipelineManager.remove_endCameraRendering(d);
#else
            if (on) RenderPipelineManager.endCameraRendering += d;
            else RenderPipelineManager.endCameraRendering -= d;
#endif
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ContextEndByAccessor(ContextHandler d, bool on)
        {
#if IL2CPP
            if (on) RenderPipelineManager.add_endContextRendering(d);
            else RenderPipelineManager.remove_endContextRendering(d);
#else
            if (on) RenderPipelineManager.endContextRendering += d;
            else RenderPipelineManager.endContextRendering -= d;
#endif
        }

#if IL2CPP
        // The field routes, exactly as HookBuiltIn subscribes Camera.onPreCull (proven live in MiSide).
        // A field write skips the event's thread-safe add: every subscription here runs on the main
        // thread, as onPreCull's does.

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void BeginByField(CameraHandler d, bool on) =>
            RenderPipelineManager.beginCameraRendering = on
                ? Il2CppSystem.Delegate.Combine(RenderPipelineManager.beginCameraRendering, d).Cast<CameraHandler>()
                : Il2CppSystem.Delegate.Remove(RenderPipelineManager.beginCameraRendering, d)?.TryCast<CameraHandler>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void EndByField(CameraHandler d, bool on) =>
            RenderPipelineManager.endCameraRendering = on
                ? Il2CppSystem.Delegate.Combine(RenderPipelineManager.endCameraRendering, d).Cast<CameraHandler>()
                : Il2CppSystem.Delegate.Remove(RenderPipelineManager.endCameraRendering, d)?.TryCast<CameraHandler>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ContextEndByField(ContextHandler d, bool on) =>
            RenderPipelineManager.endContextRendering = on
                ? Il2CppSystem.Delegate.Combine(RenderPipelineManager.endContextRendering, d).Cast<ContextHandler>()
                : Il2CppSystem.Delegate.Remove(RenderPipelineManager.endContextRendering, d)?.TryCast<ContextHandler>();
#endif

        /// <summary>The Built-in pipeline's per-camera callback before culling.</summary>
        public static void HookBuiltIn(Action<Camera> preCull)
        {
#if IL2CPP
            var d = DelegateSupport.ConvertDelegate<Camera.CameraCallback>(new Action<Camera>(preCull));
            Camera.onPreCull = Il2CppSystem.Delegate.Combine(Camera.onPreCull, d).Cast<Camera.CameraCallback>();
            unhook.Add(() => Camera.onPreCull = Il2CppSystem.Delegate.Remove(Camera.onPreCull, d)?.TryCast<Camera.CameraCallback>());
#else
            Camera.CameraCallback d = cam => preCull(cam);
            Camera.onPreCull += d;
            unhook.Add(() => Camera.onPreCull -= d);
#endif
        }

        /// <summary>
        /// Keep <paramref name="ours"/> the FIRST command buffer <paramref name="cam"/> runs at
        /// <paramref name="evt"/> (adding it when it is not there): at the start of a camera event the
        /// target bound is the camera's own, and a game's buffer at the same event may bind another —
        /// a colour-only texture it blits into — which a buffer after it would draw into instead. The
        /// game's buffers keep their order among themselves; only ours moves to the front. Buffers are
        /// told apart by NAME (give ours a unique one): Mono's GetCommandBuffers hands back new wrapper
        /// objects, never the ones that were added. True when ours is first; <paramref name="moved"/>
        /// says whether it had to be put there. False where this build stripped what reordering takes:
        /// ours is then removed and appended (where it was before this existed) and
        /// <paramref name="why"/> says what failed.
        /// </summary>
        public static bool KeepFirst(Camera cam, CameraEvent evt, CommandBuffer ours, out bool moved, out string why)
        {
            moved = false;
            why = null;
            try
            {
                moved = PutFirst(cam, evt, ours);
                return true;
            }
            catch (Exception e) when (Stripped(e))
            {
                why = Why(e);
                cam.RemoveCommandBuffer(evt, ours);
                cam.AddCommandBuffer(evt, ours);
                return false;
            }
        }

        /// <summary><see cref="KeepFirst"/>'s work, in a method of its own so a stripped member fails as
        /// it compiles, inside KeepFirst's catch. Everything is READ before anything is removed: a member
        /// that fails when called ("Method unstripping failed") must never leave the game's buffers
        /// taken off. True when it reordered.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool PutFirst(Camera cam, CameraEvent evt, CommandBuffer ours)
        {
#if IL2CPP
            // An il2cpp array: copied out, so each buffer's wrapper is held until it is added back.
            Il2CppReferenceArray<CommandBuffer> there = cam.GetCommandBuffers(evt);
            var list = new CommandBuffer[there == null ? 0 : there.Length];
            for (int i = 0; i < list.Length; i++) list[i] = there[i];
#else
            var list = cam.GetCommandBuffers(evt) ?? new CommandBuffer[0];
#endif
            string name = ours.name;
            if (list.Length > 0 && list[0] != null && list[0].name == name) return false;
            var theirs = new List<CommandBuffer>(list.Length);
            foreach (var b in list)
                if (b != null && b.name != name) theirs.Add(b);
            cam.RemoveCommandBuffers(evt);
            cam.AddCommandBuffer(evt, ours);
            foreach (var b in theirs) cam.AddCommandBuffer(evt, b);
            return true;
        }

        public static void UnhookAll()
        {
            foreach (var u in unhook) TryUndo(u);
            unhook.Clear();
        }

        /// <summary>Run one unsubscribe, whatever it throws: the game may be shutting down, or this
        /// build could not restore the event's remove (its handler then finds nothing to draw).</summary>
        static void TryUndo(Action undo)
        {
            try { undo(); }
            catch (Exception) { /* nothing more to do */ }
        }
    }
}
