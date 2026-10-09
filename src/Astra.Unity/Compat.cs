// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Astra.Bridge;
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
    /// <summary>
    /// EVERY place the Mono and IL2CPP builds differ, and nothing else: delegates handed to Unity
    /// (an IL2CPP game takes its own delegate types), arrays passed to and from Unity, asset loads by
    /// type. The rest of the plugin is written once against UnityEngine and compiles for both.
    /// </summary>
    static class Compat
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

        /// <summary>Load a bundle from memory and find the shader NAMED <paramref name="shaderName"/>
        /// in it (by the shader's own name, not the asset path: paths are lower-cased in a bundle).
        /// Null with <paramref name="why"/> when it cannot.</summary>
        public static Shader LoadShader(byte[] bundleBytes, string shaderName, out AssetBundle bundle, out string why)
        {
#if IL2CPP
            bundle = AssetBundle.LoadFromMemory(new Il2CppStructArray<byte>(bundleBytes));
#else
            bundle = AssetBundle.LoadFromMemory(bundleBytes);
#endif
            if (bundle == null)
            {
                why = "Unity refused the bundle (see the player log: built for another Unity line or platform?)";
                return null;
            }
            var names = new List<string>();
#if IL2CPP
            foreach (var o in bundle.LoadAllAssets(Il2CppType.Of<Shader>()))
            {
                var sh = o.TryCast<Shader>();
#else
            foreach (var sh in bundle.LoadAllAssets<Shader>())
            {
#endif
                if (sh == null) continue;
                if (sh.name == shaderName)
                {
                    why = null;
                    return sh;
                }
                names.Add(sh.name);
            }
            why = $"no shader '{shaderName}' in the bundle (it has: {string.Join(", ", names)})";
            return null;
        }

        /// <summary>
        /// Does this game's runtime have <typeparamref name="T"/> at all? An IL2CPP build strips every
        /// engine class the game never uses; Il2CppInterop may still put the C# type back in the
        /// interop (MiSide's TerrainCollider), but with no native class behind it every generic call
        /// naming it — <c>TryCast</c>, <c>GetComponent</c>, <c>Il2CppType.Of</c> — throws "is not an
        /// Il2Cpp reference type" (that stopped her in MiSide's first level). No object of a class the
        /// game lacks can exist, so a check for one is simply false. Always true under Mono.
        /// </summary>
        public static bool Exists<T>() where T : UnityEngine.Object
        {
#if IL2CPP
            try
            {
                return Il2CppClassPointerStore<T>.NativeClassPtr != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
#else
            return true;
#endif
        }

        /// <summary><c>GetComponent&lt;T&gt;</c>, null when the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static T Get<T>(GameObject go) where T : Component => go != null && Exists<T>() ? go.GetComponent<T>() : null;

        /// <summary><c>TryCast</c> / <c>as</c>, null when the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static T As<T>(UnityEngine.Object o) where T : UnityEngine.Object
        {
            if (o == null || !Exists<T>()) return null;
#if IL2CPP
            return o.TryCast<T>();
#else
            return o as T;
#endif
        }

        /// <summary>Every loaded object of a type, including inactive ones (filter yourself); none when
        /// the game has no such class (<see cref="Exists{T}"/>).</summary>
        public static List<T> FindAll<T>() where T : UnityEngine.Object
        {
            var list = new List<T>();
            if (!Exists<T>()) return list;
#if IL2CPP
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<T>()))
            {
                var t = o.TryCast<T>();
                if (t != null) list.Add(t);
            }
#else
            list.AddRange(Resources.FindObjectsOfTypeAll<T>());
#endif
            return list;
        }

        /// <summary>
        /// The vertex arrays her shadow caster fills every frame and hands to its mesh: plain arrays
        /// under Mono; under IL2CPP the il2cpp arrays themselves, made once per size, so a frame
        /// allocates nothing (a managed array would be copied into a new il2cpp one at every hand-over).
        /// Fill them by FIELD (<c>v.x = …</c>): under IL2CPP a <c>Vector3</c> constructor or operator is
        /// a call into the runtime.
        /// </summary>
        public sealed class VertexArrays
        {
#if IL2CPP
            public Il2CppStructArray<Vector3> Positions, Normals;
#else
            public Vector3[] Positions, Normals;
#endif
            public int Length => Positions == null ? 0 : Positions.Length;

            public void Resize(int n)
            {
                if (Length == n) return;
#if IL2CPP
                Positions = new Il2CppStructArray<Vector3>(n);
                Normals = new Il2CppStructArray<Vector3>(n);
#else
                Positions = new Vector3[n];
                Normals = new Vector3[n];
#endif
            }

            /// <summary>
            /// Her caster's <paramref name="v"/> vertices from the ring — glTF's right-handed world to
            /// Unity's left-handed one (z flips), each one twice (the front copy, then the back one
            /// with its normal reversed) — and their bounds. Through ONE pointer per array per frame:
            /// under IL2CPP an indexed element write is several calls into the runtime.
            /// </summary>
            public unsafe void Fill(float* p, float* q, int v, out float[] bounds)
            {
                float loX = float.MaxValue, loY = float.MaxValue, loZ = float.MaxValue;
                float hiX = float.MinValue, hiY = float.MinValue, hiZ = float.MinValue;
#if IL2CPP
                fixed (Vector3* pos = Positions.AsSpan(), nrm = Normals.AsSpan())
#else
                fixed (Vector3* pos = Positions, nrm = Normals)
#endif
                {
                    for (int i = 0; i < v; i++)
                    {
                        float x = p[3 * i], y = p[3 * i + 1], z = -p[3 * i + 2];
                        float nx = q[3 * i], ny = q[3 * i + 1], nz = -q[3 * i + 2];
                        pos[i].x = x; pos[i].y = y; pos[i].z = z;
                        pos[i + v].x = x; pos[i + v].y = y; pos[i + v].z = z;
                        nrm[i].x = nx; nrm[i].y = ny; nrm[i].z = nz;
                        nrm[i + v].x = -nx; nrm[i + v].y = -ny; nrm[i + v].z = -nz;
                        if (x < loX) loX = x;
                        if (y < loY) loY = y;
                        if (z < loZ) loZ = z;
                        if (x > hiX) hiX = x;
                        if (y > hiY) hiY = y;
                        if (z > hiZ) hiZ = z;
                    }
                }
                bounds = new[] { loX, loY, loZ, hiX, hiY, hiZ };
            }

            /// <summary>Into <paramref name="mesh"/>, with its triangles when they changed (null = the same).</summary>
            public void Upload(Mesh mesh, int[] triangles)
            {
                if (triangles != null) mesh.Clear();
                mesh.vertices = Positions;
                mesh.normals = Normals;
                if (triangles != null) mesh.triangles = triangles;
            }
        }

        /// <summary>
        /// What <see cref="RequestDepthTexture"/> changed on a camera's URP data, to be put back by
        /// <see cref="RestoreDepthTexture"/>: the member it wrote and the value that member held
        /// before. The default (<see cref="Changed"/> false) changed nothing.
        /// </summary>
        public readonly struct DepthSetting
        {
            public DepthSetting(string member, object value)
            {
                Member = member;
                Value = value;
            }

            /// <summary>The field (<c>m_RequiresDepthTextureOption</c>, the camera's own three-way
            /// option) or, where it could not be read, the property (<c>requiresDepthTexture</c>).</summary>
            public readonly string Member;

            /// <summary>What it held before.</summary>
            public readonly object Value;

            public bool Changed => Member != null;
        }

        const string DepthOption = "m_RequiresDepthTextureOption";
        const string DepthProperty = "requiresDepthTexture";
        const int OptionOn = 1; // CameraOverrideOption: Off, On, UsePipelineSettings

        /// <summary>
        /// Ask URP to render <paramref name="cam"/>'s depth texture (her depth test needs it), through
        /// its own serialized camera data (<c>UniversalAdditionalCameraData</c>), found by name at run
        /// time: the plugin does not depend on URP. True when asked, <paramref name="how"/> saying how
        /// ("property", or "field" where an IL2CPP build stripped the property's setter), or that the
        /// camera renders one already (nothing changed). <paramref name="before"/> is what was changed,
        /// for <see cref="RestoreDepthTexture"/>: the camera's own option as it was, never a guess.
        /// False where there is nothing to ask (no URP, a camera with no URP data: <paramref name="how"/>
        /// null) or the asking failed (<paramref name="how"/> says why). Never throws: the request is
        /// optional — without it she is drawn over everything, never stopped — so any failure is an answer.
        /// </summary>
        public static bool RequestDepthTexture(Camera cam, out string how, out DepthSetting before)
        {
            how = null;
            before = default;
            try
            {
                return AskForDepth(cam, out how, out before);
            }
            catch (Exception e)
            {
                how = Why(e); // the failure under a reflective call's wrapper
                return false;
            }
        }

        /// <summary>Put back what <see cref="RequestDepthTexture"/> changed on <paramref name="cam"/>
        /// (nothing for a camera since destroyed) — only while the option still holds what the request
        /// wrote: one the game has set since (its own settings) is its choice and stays. Null when done
        /// or left, else why it could not be. Never throws.</summary>
        public static string RestoreDepthTexture(Camera cam, DepthSetting before)
        {
            if (!before.Changed || cam == null) return null;
            try
            {
                PutDepthBack(cam, before);
                return null;
            }
            catch (Exception e)
            {
                return Why(e);
            }
        }

        // RequestDepthTexture's work, apart so that a type or member a build stripped fails at its caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool AskForDepth(Camera cam, out string how, out DepthSetting before)
        {
            how = null;
            before = default;
#if IL2CPP
            var data = UrpCameraData(cam);
            if (data == null) return false;
            var t = data.GetType();
            // Il2CppInterop exposes a serialized field as a property of the same name that reads and
            // writes the object's memory directly: no method of the game's runs, none can be stripped.
            var field = t.GetProperty(DepthOption);
            bool fieldUsable = field != null && field.CanRead && field.CanWrite && field.PropertyType.IsEnum;
            object was = fieldUsable ? field.GetValue(data, null) : null;
            var prop = t.GetProperty(DepthProperty);
            if (RendersDepthAlready(was, prop, data))
            {
                how = AlreadyOn;
                return true;
            }
            if (prop != null && prop.CanWrite)
            {
                try
                {
                    prop.SetValue(data, true, null);
                    how = "property";
                    before = was != null ? new DepthSetting(DepthOption, was) : new DepthSetting(DepthProperty, false);
                    return true;
                }
                catch (TargetInvocationException e) when (e.InnerException != null && Stripped(e.InnerException))
                {
                    // The setter's body is gone from this build: the field it writes is still there.
                    how = "field, the property's setter is stripped: " + Why(e.InnerException);
                }
            }
            if (!fieldUsable)
            {
                how = (how != null ? how + "; " : "") + "no settable " + DepthOption;
                return false;
            }
            field.SetValue(data, Enum.ToObject(field.PropertyType, OptionOn), null);
            before = new DepthSetting(DepthOption, was);
            how = how ?? "field, this build has no requiresDepthTexture property";
            return true;
#else
            foreach (var c in cam.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "UniversalAdditionalCameraData") continue;
                var t = c.GetType();
                var p = t.GetProperty(DepthProperty);
                if (p == null || !p.CanWrite) continue;
                var f = t.GetField(DepthOption, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object was = f != null && f.FieldType.IsEnum ? f.GetValue(c) : null;
                if (RendersDepthAlready(was, p, c))
                {
                    how = AlreadyOn;
                    return true;
                }
                p.SetValue(c, true, null);
                before = was != null ? new DepthSetting(DepthOption, was) : new DepthSetting(DepthProperty, false);
                how = "property";
                return true;
            }
            return false;
#endif
        }

        const string AlreadyOn = "it renders one already: nothing changed";

        /// <summary>Does the camera render its depth texture already: its own option says On, or its
        /// property (which reads the pipeline asset's setting through "use the pipeline's") says so?
        /// A property whose getter a build stripped answers nothing: then it is asked anyway.</summary>
        static bool RendersDepthAlready(object option, PropertyInfo prop, object data)
        {
            if (option != null && Convert.ToInt32(option) == OptionOn) return true;
            if (prop == null || !prop.CanRead) return false;
            try
            {
                return prop.GetValue(data, null) is bool on && on;
            }
            catch (TargetInvocationException e) when (e.InnerException != null && Stripped(e.InnerException))
            {
                return false;
            }
        }

        // RestoreDepthTexture's work, apart so that a type or member a build stripped fails at its caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void PutDepthBack(Camera cam, DepthSetting before)
        {
            bool option = before.Member == DepthOption;
#if IL2CPP
            var data = UrpCameraData(cam);
            if (data == null) return;
            var p = data.GetType().GetProperty(before.Member);
            if (p == null || !p.CanWrite) return;
            if (option && p.CanRead && Convert.ToInt32(p.GetValue(data, null)) != OptionOn) return; // the game's since
            p.SetValue(data, before.Value, null);
#else
            foreach (var c in cam.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "UniversalAdditionalCameraData") continue;
                var t = c.GetType();
                var f = t.GetField(before.Member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null)
                {
                    if (option && Convert.ToInt32(f.GetValue(c)) != OptionOn) return; // the game's since
                    f.SetValue(c, before.Value);
                    return;
                }
                var p = t.GetProperty(before.Member);
                if (p != null && p.CanWrite) p.SetValue(c, before.Value, null);
                return;
            }
#endif
        }

#if IL2CPP
        static Type urpCameraDataType;
        static bool urpCameraDataSought;

        /// <summary>
        /// <paramref name="cam"/>'s <c>UniversalAdditionalCameraData</c> as its interop wrapper, or null
        /// (no URP in this game, or none on this camera). The type is resolved by name from the URP
        /// interop assembly, once: the plugin is compiled against no URP. The component is fetched by
        /// its IL2CPP type and wrapped by the constructor every interop type has (from its pointer): a
        /// component handed back as a plain <c>Component</c> is never the URP type by a C# cast.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static object UrpCameraData(Camera cam)
        {
            if (cam == null) return null;
            if (!urpCameraDataSought)
            {
                urpCameraDataSought = true;
                urpCameraDataType = FindUrpCameraDataType();
            }
            var t = urpCameraDataType;
            if (t == null) return null;
            // No native class behind the interop type (a build that left URP out): null, not a throw.
            var il2cppType = Il2CppType.From(t, false);
            if (il2cppType == null) return null;
            var c = cam.GetComponent(il2cppType);
            return c == null ? null : Activator.CreateInstance(t, c.Pointer);
        }

        /// <summary>The interop type of URP's camera data, from the loaded URP interop assembly or, when
        /// nothing loaded it yet, from disk; null in a game without URP.</summary>
        static Type FindUrpCameraDataType()
        {
            const string assembly = "Unity.RenderPipelines.Universal.Runtime";
            Assembly urp = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try { name = a.GetName().Name; }
                catch (Exception) { continue; }
                if (name != assembly) continue;
                urp = a;
                break;
            }
            if (urp == null)
            {
                // Interop assemblies load on first use: URP's may be on disk with nothing loaded from it yet.
                try { urp = Assembly.Load(assembly); }
                catch (Exception) { return null; } // not there either: a game without URP
            }
            try { return urp.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData", false); }
            catch (Exception) { return null; }
        }
#endif

        /// <summary>The failure a call throws in a game whose build STRIPPED a type or member the
        /// called method names (an IL2CPP build removes every engine API the game never uses): the
        /// method fails to compile the first time it runs. Catch it at the CALLER of a method that
        /// names the API — never inside that method, which cannot even start. Il2CppInterop puts
        /// some stripped members back, and two of its stand-ins throw on use: a body it could not
        /// restore ("Method unstripping failed", NotSupportedException) and a native call the player
        /// never registered (a plain Exception: "ICall with signature … was not resolved"). The failure
        /// may come wrapped: in a type initializer's (a static field of a stripped type) or a reflective
        /// call's — the wrapped <see cref="Feature.Cause"/> is judged. And a build that left out a whole
        /// engine module (UnityEngine.PhysicsModule in a game without 3D physics) fails to LOAD its
        /// assembly: a FileNotFoundException naming a "UnityEngine." assembly is stripped too.</summary>
        public static bool Stripped(Exception e)
        {
            e = Feature.Cause(e);
            return e is TypeLoadException || e is MissingMemberException || e is NotSupportedException
                || (e is System.IO.FileNotFoundException f && f.FileName != null
                    && f.FileName.StartsWith("UnityEngine.", StringComparison.Ordinal))
                || (e.GetType() == typeof(Exception) && e.Message.IndexOf("ICall", StringComparison.Ordinal) >= 0);
        }

        /// <summary>Why a call failed, in one line for the log (<see cref="Feature.Describe"/>): the
        /// failure under its wrappers, its type and message, and the method that threw where the message
        /// names nothing ("Method unstripping failed").</summary>
        public static string Why(Exception e) => Feature.Describe(e);

        /// <summary>Reading URP's camera type, an optional part: a camera stacked on another is also known
        /// by its view (<c>DefaultCamera.OverlayOf</c>), so a game whose build cannot read the type loses
        /// only this check.</summary>
        static readonly Feature urpOverlayCheck = new Feature("the URP overlay check");

        /// <summary>
        /// A URP Overlay camera: its <c>UniversalAdditionalCameraData.renderType</c>, by reflection (the
        /// plugin does not depend on URP). Under IL2CPP it is read from the camera data's interop wrapper
        /// (<c>UrpCameraData</c>), through the serialized field <c>m_CameraType</c> where this build
        /// stripped the property's getter. False where there is no URP, no URP data on the camera, or the
        /// check is off: its first failure switches it off and is said once through
        /// <paramref name="warn"/> (<see cref="Feature"/>). Never throws.
        /// </summary>
        public static bool IsUrpOverlay(Camera cam, Action<string> warn)
        {
            bool yes = false;
            if (cam != null) urpOverlayCheck.Run(() => yes = ReadUrpOverlay(cam), warn);
            return yes;
        }

        // IsUrpOverlay's work, apart so that a type or member a build stripped fails at its caller.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool ReadUrpOverlay(Camera cam)
        {
#if IL2CPP
            var data = UrpCameraData(cam);
            if (data == null) return false;
            var t = data.GetType();
            var prop = t.GetProperty("renderType");
            if (prop != null && prop.CanRead)
            {
                try
                {
                    return prop.GetValue(data, null)?.ToString() == "Overlay";
                }
                catch (TargetInvocationException e) when (Stripped(e))
                {
                    // The getter's body is gone from this build: the field it reads is still there.
                }
            }
            // Il2CppInterop exposes a serialized field as a property of the same name that reads the
            // object's memory directly: no method of the game's runs, none can be stripped.
            var field = t.GetProperty("m_CameraType");
            return field != null && field.CanRead && field.GetValue(data, null)?.ToString() == "Overlay";
#else
            foreach (var c in cam.GetComponents<Component>())
            {
                if (c == null || c.GetType().Name != "UniversalAdditionalCameraData") continue;
                var p = c.GetType().GetProperty("renderType");
                return p != null && p.GetValue(c, null)?.ToString() == "Overlay";
            }
            return false;
#endif
        }

        public static List<GameObject> WithTag(string tag)
        {
            var list = new List<GameObject>();
            try
            {
                foreach (var g in GameObject.FindGameObjectsWithTag(tag)) list.Add(g);
            }
            catch (Exception)
            {
                // The game never defined this tag (a UnityException — under IL2CPP an Il2CppException).
            }
            return list;
        }

        public static List<Collider> CollidersUnder(GameObject root)
        {
            var list = new List<Collider>();
            if (root == null || !Exists<Collider>()) return list;
#if IL2CPP
            foreach (var c in root.GetComponentsInChildren(Il2CppType.Of<Collider>(), true))
            {
                var t = c.TryCast<Collider>();
                if (t != null) list.Add(t);
            }
#else
            list.AddRange(root.GetComponentsInChildren<Collider>(true));
#endif
            return list;
        }

        /// <summary>
        /// The one hit buffer every physics query here fills, in a class of its own: Compat's own type
        /// initializer then names no physics type, so a build without 3D physics (no RaycastHit, or no
        /// UnityEngine.PhysicsModule at all) fails only the queries that need it — at their callers,
        /// as <see cref="Stripped"/> — and never poisons HookSrp, HookBuiltIn or LoadShader.
        /// </summary>
        static class PhysicsQuery
        {
#if IL2CPP
            public static readonly Il2CppStructArray<RaycastHit> Hits = new Il2CppStructArray<RaycastHit>(32);
#else
            public static readonly RaycastHit[] Hits = new RaycastHit[32];
#endif
        }

        static readonly Dictionary<int, bool> castsShadow = new Dictionary<int, bool>();
        // When a NEGATIVE verdict was judged (unscaled time); a POSITIVE one is kept forever and
        // never appears here.
        static readonly Dictionary<int, float> castsShadowNegativeAt = new Dictionary<int, float>();

        /// <summary>
        /// Does something that casts a SHADOW lie along the ray? Only a collider that belongs to a
        /// renderer the game draws shadows from — on its object, under it, on an ancestor up to 3
        /// levels up, or on that ancestor's OTHER direct children (the collider's siblings, and its
        /// parent's siblings, and so on — never a whole subtree past this object's own, and never
        /// past a scene root) — counts, and terrain: a game's invisible colliders — map bounds,
        /// zones, blockers — stop no light. (Lethal Company is full of them: her sun read "hidden"
        /// in open daylight.) A verdict is cached; a NEGATIVE one expires after 5 s (a renderer can
        /// start casting shadows after the game first judges it), a positive one never does.
        /// </summary>
        public static bool Shadowed(Vector3 origin, Vector3 dir, float distance, HashSet<int> ignore)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, PhysicsQuery.Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = PhysicsQuery.Hits[i];
                var c = h.collider;
                if (c == null || h.distance <= 0) continue;
                int id = c.GetInstanceID();
                if (ignore.Contains(id)) continue;
                if (!castsShadow.TryGetValue(id, out bool casts) ||
                    (!casts && Time.unscaledTime - castsShadowNegativeAt[id] > 5f))
                {
                    if (castsShadow.Count > 4096) { castsShadow.Clear(); castsShadowNegativeAt.Clear(); }
                    castsShadow[id] = casts = CastsShadow(c);
                    if (!casts) castsShadowNegativeAt[id] = Time.unscaledTime;
                }
                if (casts) return true;
            }
            return false;
        }

        /// <summary>Forgets every shadow-caster verdict, positive and negative: a new session may
        /// find different casters where the same collider ids sat before.</summary>
        public static void ClearShadowCache()
        {
            castsShadow.Clear();
            castsShadowNegativeAt.Clear();
        }

        static bool CastsShadow(Collider c)
        {
            if (As<TerrainCollider>(c) != null) return true;
            var go = c.gameObject;
            if (Casts(Get<Renderer>(go))) return true;
#if IL2CPP
            if (Exists<Renderer>())
                foreach (var o in go.GetComponentsInChildren(Il2CppType.Of<Renderer>(), false))
                    if (Casts(o.TryCast<Renderer>())) return true;
#else
            foreach (var r in go.GetComponentsInChildren<Renderer>(false))
                if (Casts(r)) return true;
#endif
            // Up to 3 ancestors (parent, grandparent, great-grandparent): each one's own renderer,
            // and each one's OTHER direct children (this object's siblings at that level) — never
            // past a scene root (an ancestor with no parent of its own ends the walk).
            var at = c.transform.parent;
            for (int level = 0; level < 3 && at != null; level++)
            {
                if (Casts(Get<Renderer>(at.gameObject))) return true;
                for (int i = 0; i < at.childCount; i++)
                {
                    var sibling = at.GetChild(i);
                    if (sibling != c.transform && Casts(Get<Renderer>(sibling.gameObject))) return true;
                }
                at = at.parent;
            }
            return false;
        }

        static bool Casts(Renderer r) => r != null && r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

#if !IL2CPP
        static readonly Dictionary<int, Component> dimmerComponent = new Dictionary<int, Component>();
        static readonly Dictionary<int, PropertyInfo> dimmerProperty = new Dictionary<int, PropertyInfo>();
#endif

        /// <summary>HDRP's own per-light multiplier (<c>HDAdditionalLightData.lightDimmer</c>), read
        /// by reflection so this assembly never references the HDRP package; 1 where there is none (no
        /// HDRP, or the component is absent). Mono only: under IL2CPP a component's native type is
        /// never reachable as this C# property by reflection, so it is always 1 there.</summary>
        public static float LightDimmer(Light l)
        {
#if IL2CPP
            return 1f;
#else
            if (l == null) return 1f;
            int id = l.GetInstanceID();
            if (!dimmerComponent.TryGetValue(id, out var comp))
            {
                foreach (var c in l.GetComponents<Component>())
                {
                    if (c == null || c.GetType().Name != "HDAdditionalLightData") continue;
                    comp = c;
                    break;
                }
                if (dimmerComponent.Count > 4096) { dimmerComponent.Clear(); dimmerProperty.Clear(); }
                dimmerComponent[id] = comp; // null cached too: no HDAdditionalLightData on this light
            }
            if (comp == null) return 1f;
            if (!dimmerProperty.TryGetValue(id, out var prop))
            {
                prop = comp.GetType().GetProperty("lightDimmer");
                dimmerProperty[id] = prop;
            }
            return prop != null && prop.PropertyType == typeof(float) ? (float)prop.GetValue(comp, null) : 1f;
#endif
        }

        /// <summary>The nearest hit along a ray, skipping colliders in <paramref name="ignore"/> and triggers.</summary>
        public static bool Raycast(Vector3 origin, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.RaycastNonAlloc(origin, dir, PhysicsQuery.Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        /// <summary>The nearest hit of a capsule swept along <paramref name="dir"/>, skipping <paramref name="ignore"/>.</summary>
        public static bool CapsuleCast(Vector3 p1, Vector3 p2, float radius, Vector3 dir, float distance, int mask, HashSet<int> ignore, out RaycastHit nearest)
        {
            int n = Physics.CapsuleCastNonAlloc(p1, p2, radius, dir, PhysicsQuery.Hits, distance, mask, QueryTriggerInteraction.Ignore);
            return Nearest(n, ignore, out nearest);
        }

        static bool Nearest(int n, HashSet<int> ignore, out RaycastHit nearest)
        {
            nearest = default;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = PhysicsQuery.Hits[i];
                var c = h.collider;
                if (c == null || ignore.Contains(c.GetInstanceID())) continue;
                // A cast that starts inside a collider reports it at distance 0 with no point: that is
                // her standing in the geometry, not something ahead of her.
                if (h.distance <= 0f && h.point == Vector3.zero) continue;
                if (found && h.distance >= nearest.distance) continue;
                nearest = h;
                found = true;
            }
            return found;
        }

        /// <summary>The active render pipeline asset's class, by its NATIVE type: under IL2CPP a wrapper's
        /// C# type is the property's declared one (<c>RenderPipelineAsset</c>), never HDRP's or URP's.
        /// Null in the Built-in pipeline.</summary>
        public static string PipelineClass()
        {
            var asset = GraphicsSettings.currentRenderPipeline;
            if (asset == null) return null;
#if IL2CPP
            return asset.GetIl2CppType().FullName;
#else
            return asset.GetType().FullName;
#endif
        }

        /// <summary><paramref name="t"/> as a render texture, or null (an IL2CPP wrapper is never a
        /// C# <c>RenderTexture</c> by a type test).</summary>
        public static RenderTexture AsRenderTexture(Texture t)
        {
#if IL2CPP
            return t != null && Exists<RenderTexture>() ? t.TryCast<RenderTexture>() : null;
#else
            return t as RenderTexture;
#endif
        }

        /// <summary>Let go of every texture <paramref name="m"/> holds (a copy of a game's material that
        /// samples none of them, so a scene's unload is not held up by it).</summary>
        public static void ClearTextures(Material m)
        {
            foreach (int id in m.GetTexturePropertyNameIDs()) m.SetTexture(id, null);
        }
    }
}
