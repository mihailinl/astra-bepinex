// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Astra.Sdk
{
    /// <summary>
    /// Reach a game's own types without compiling against them. A game integration ships against
    /// the public SDK, BepInEx and Unity only (<c>GAME-INTEGRATION-MANIFEST.md</c> §6: "game types
    /// are reached through the SDK's reflection helper") — never the game's own assemblies — so it
    /// resolves types such as <c>StartOfRound</c> or <c>PlayerControllerB</c> by NAME, at run time,
    /// inside the actual game process:
    /// <code>
    /// static readonly GameType StartOfRound = GameType.Find("StartOfRound");
    /// static readonly Member&lt;bool&gt; IsCrouching = StartOfRound.Member&lt;bool&gt;("isCrouching");
    ///
    /// object instance = StartOfRound.Static&lt;object&gt;("Instance");
    /// bool crouching = IsCrouching.Get(instance);
    /// </code>
    /// Every lookup is NULL-SAFE and EXCEPTION-FREE: a missing type or member reads as
    /// <c>default(T)</c> and is recorded once in <see cref="Missing"/> — never thrown, so one field
    /// a game update renamed does not crash every frame, only reads as its default. The same call
    /// works for a field OR a property without the caller knowing which: Il2CppInterop exposes a
    /// game's FIELDS as C# PROPERTIES, so code written once reads a Mono game (BepInEx 5, real
    /// fields) and an IL2CPP game (BepInEx 6, properties) alike. No
    /// <c>System.Linq.Expressions</c>: IL2CPP's AOT compiler cannot JIT them, so every accessor
    /// calls <see cref="FieldInfo"/>/<see cref="PropertyInfo"/> directly.
    /// </summary>
    public readonly struct GameType
    {
        readonly Type type;

        GameType(Type type) => this.type = type;

        /// <summary>Whether the type was found.</summary>
        public bool Exists => type != null;

        /// <summary>The underlying reflection type, or null (check <see cref="Exists"/> first).</summary>
        public Type ClrType => type;

        /// <summary>
        /// Find a type by its short name (no namespace). Looks in the assembly named
        /// <paramref name="assembly"/> first (the default, <c>"Assembly-CSharp"</c> — every Unity
        /// game's own code), then every OTHER loaded assembly as a fallback (a type the game split
        /// into a library: Photon, Mirror, a DLC assembly). The result is cached, so a repeated
        /// call — even one that misses — costs no further scan. Not found: every member read off
        /// the result reads as <c>default</c>, and the miss is recorded once in <see cref="Missing"/>.
        /// </summary>
        public static GameType Find(string name, string assembly = "Assembly-CSharp")
        {
            if (string.IsNullOrEmpty(name)) return new GameType(null);
            lock (typeCache)
            {
                if (typeCache.TryGetValue(name, out var cached)) return new GameType(cached);

                Assembly preferred = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(SafeName(a), assembly, StringComparison.Ordinal)) { preferred = a; break; }
                }

                var hit = preferred != null ? FindInAssembly(preferred, name) : null;
                if (hit == null)
                {
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a == preferred) continue;
                        hit = FindInAssembly(a, name);
                        if (hit != null) break;
                    }
                }

                typeCache[name] = hit; // cache a miss too: one scan, not one per frame
                if (hit == null) LogMissingOnce("type " + name);
                return new GameType(hit);
            }
        }

        static string SafeName(Assembly a)
        {
            try { return a.GetName().Name; }
            catch { return null; }
        }

        static Type FindInAssembly(Assembly a, string typeName)
        {
            try
            {
                foreach (var t in a.GetTypes())
                    if (t.Name == typeName) return t;
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Some assemblies (an IL2CPP interop DLL missing a dependency, a partial DLC)
                // throw here; the types that DID load are still worth searching.
                foreach (var t in ex.Types)
                    if (t != null && t.Name == typeName) return t;
            }
            catch
            {
                // Never let one hostile assembly stop every other lookup.
            }
            return null;
        }

        /// <summary>
        /// A STATIC member's current value — a field or a property, searched once and cached, then
        /// read fresh every call (a static such as <c>Instance</c> changes over the game's life).
        /// Missing, <see cref="Exists"/> false, or a value not of type <typeparamref name="T"/>:
        /// <c>default(T)</c>, recorded once in <see cref="Missing"/>.
        /// </summary>
        public T Static<T>(string name)
        {
            var accessor = Accessor(name, isStatic: true);
            return accessor != null ? accessor.Get<T>(null) : default;
        }

        /// <summary>
        /// An INSTANCE member accessor: its reflection metadata is looked up and cached once here;
        /// store the result (typically a <c>static readonly</c> field next to the owning
        /// <see cref="GameType"/>) and call <see cref="Member{T}.Get"/> every frame with no further
        /// lookup. <see cref="Exists"/> false (on this type or the member): every
        /// <see cref="Member{T}.Get"/> reads as its fallback.
        /// </summary>
        public Member<T> Member<T>(string name) => new Member<T>(Accessor(name, isStatic: false));

        MemberAccessor Accessor(string name, bool isStatic)
        {
            if (type == null)
            {
                LogMissingOnce((isStatic ? "static " : "member ") + "<missing type>." + name);
                return null;
            }
            var key = (type, name, isStatic);
            lock (memberCache)
            {
                if (memberCache.TryGetValue(key, out var found)) return found;
                var a = MemberAccessor.Find(type, name, isStatic);
                memberCache[key] = a;
                if (a == null) LogMissingOnce((isStatic ? "static " : "member ") + type.Name + "." + name);
                return a;
            }
        }

        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();
        static readonly Dictionary<(Type, string, bool), MemberAccessor> memberCache =
            new Dictionary<(Type, string, bool), MemberAccessor>();

        static void LogMissingOnce(string what)
        {
            lock (missingSeen)
            {
                if (missingSeen.Add(what)) missingList.Add(what);
            }
        }

        static readonly HashSet<string> missingSeen = new HashSet<string>();
        static readonly List<string> missingList = new List<string>();

        /// <summary>
        /// Every type or member name not found so far, each listed once, in the order first missed.
        /// Nothing throws when a lookup misses — this is how the SDK makes a miss CALLER-VISIBLE: an
        /// integration logs it once at startup (or a debug command dumps it), rather than reading it
        /// per frame.
        /// </summary>
        public static IReadOnlyList<string> Missing => missingList;
    }

    /// <summary>
    /// A cached accessor for one INSTANCE member (a field or a property), returned by
    /// <see cref="GameType.Member{T}"/>. Its reflection lookup already happened; store it once and
    /// call <see cref="Get"/> every frame.
    /// </summary>
    public readonly struct Member<T>
    {
        readonly MemberAccessor accessor;
        internal Member(MemberAccessor accessor) => this.accessor = accessor;

        /// <summary>Whether a field or a property of this name was found on the owning type (or a
        /// base of it).</summary>
        public bool Exists => accessor != null;

        /// <summary>
        /// The member's current value on <paramref name="instance"/>. A null instance, a missing
        /// member, or a value that is not (or cannot be unboxed as) <typeparamref name="T"/>:
        /// <paramref name="fallback"/> — never throws.
        /// </summary>
        public T Get(object instance, T fallback = default) =>
            instance != null && accessor != null ? accessor.Get(instance, fallback) : fallback;
    }

    /// <summary>
    /// Internal: one field or property, resolved once and read through a typed getter with plain
    /// reflection calls (no <c>System.Linq.Expressions</c> — IL2CPP's AOT compiler cannot JIT them).
    /// </summary>
    abstract class MemberAccessor
    {
        /// <summary>
        /// Find a field or a property named <paramref name="name"/> on <paramref name="type"/> or
        /// any of its base types (a base-declared private member is invisible to
        /// <c>GetField</c>/<c>GetProperty</c> called on the derived type directly). Fields are
        /// preferred (the common Mono case); a property is accepted too (the IL2CPP case, and any
        /// ordinary C# property) as long as it can be read.
        /// </summary>
        public static MemberAccessor Find(Type type, string name, bool isStatic)
        {
            var flags = (isStatic ? BindingFlags.Static : BindingFlags.Instance)
                        | BindingFlags.Public | BindingFlags.NonPublic;
            for (var t = type; t != null; t = t.BaseType)
            {
                var f = t.GetField(name, flags);
                if (f != null) return new FieldAccessor(f);
                var p = t.GetProperty(name, flags);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0) return new PropertyAccessor(p);
            }
            return null;
        }

        public abstract T Get<T>(object instance, T fallback = default);
    }

    sealed class FieldAccessor : MemberAccessor
    {
        readonly FieldInfo field;
        public FieldAccessor(FieldInfo field) => this.field = field;

        public override T Get<T>(object instance, T fallback = default)
        {
            try
            {
                var v = field.GetValue(instance);
                return v is T t ? t : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }

    sealed class PropertyAccessor : MemberAccessor
    {
        readonly PropertyInfo prop;
        public PropertyAccessor(PropertyInfo prop) => this.prop = prop;

        public override T Get<T>(object instance, T fallback = default)
        {
            try
            {
                var v = prop.GetValue(instance);
                return v is T t ? t : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
