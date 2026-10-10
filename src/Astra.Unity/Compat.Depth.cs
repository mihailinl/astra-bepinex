// SPDX-License-Identifier: MIT
// A readability shard of Compat: the URP camera data and depth-request family.
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using UnityEngine;
#if IL2CPP
using Il2CppInterop.Runtime;
#endif

namespace Astra.Unity
{
    static partial class Compat
    {
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
    }
}
