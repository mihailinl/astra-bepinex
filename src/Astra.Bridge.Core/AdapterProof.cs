// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Astra.Bridge
{
    /// <summary>
    /// What a pipeline ADAPTER (the URP and HDRP passes the Unity foundation puts INTO a game's
    /// render) must pass before its pass goes in, and how it binds what differs between engine
    /// lines. A pass runs inside the game's own render: a member it names that this game's engine
    /// lacks fails as the runtime COMPILES the method that names it, and the pipeline then drops the
    /// camera's whole render, every frame (foundation 0.8.0 in a Unity 6000.3 game: a black screen).
    /// So the foundation compiles the adapter's whole pass at load, in its own try/catch, and leaves
    /// the pass out when anything does not bind. No Unity type crosses this boundary, so the rules are
    /// unit-tested here.
    /// </summary>
    public static class AdapterProof
    {
        // Mono compiles a method when it is asked for its code (RuntimeMethodHandle.GetFunctionPointer:
        // mono_jit_compile_method), which is what every BepInEx 5 game runs; CoreCLR (the tests) when
        // it is prepared, and hands out a stub for the other.
        static readonly bool Mono = Type.GetType("Mono.Runtime") != null;

        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                                      | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Compile, now, every method, constructor and type initializer of <paramref name="type"/> and of
        /// every type nested in it (the C# compiler puts a lambda's body and its closure there). Null
        /// when all of them compile in this process; otherwise ONE line naming the first that does not
        /// and why — a member the game lacks names itself ("Type.Method: MissingMethodException: Method
        /// not found: void …SetRenderAttachment(…)"). Nothing is run: compiling runs no code (a type
        /// initializer is compiled, not run). An open generic type or method cannot be compiled before
        /// it is instantiated and is passed over: an adapter's pass path has none.
        /// </summary>
        public static string Prove(Type type)
        {
            if (type == null) return "no type to prove";
            if (type.ContainsGenericParameters) return null;
            MethodBase[] methods;
            Type[] nested;
            try
            {
                methods = type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)).ToArray();
                nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception e)
            {
                // The type itself does not load here (a base type or a signature names something missing).
                return $"{Name(type)}: {Feature.Describe(e)}";
            }
            foreach (var m in methods)
            {
                string why = Compile(m);
                if (why != null) return why;
            }
            foreach (var n in nested)
            {
                string why = Prove(n);
                if (why != null) return why;
            }
            return null;
        }

        /// <summary>Compile one method; null when it compiles (or has no IL to compile).</summary>
        static string Compile(MethodBase m)
        {
            if (m.IsAbstract || m.ContainsGenericParameters) return null;
            // No IL: a delegate type's Invoke (Runtime), an internal call, a P/Invoke.
            if ((m.MethodImplementationFlags & (MethodImplAttributes.Runtime | MethodImplAttributes.InternalCall)) != 0) return null;
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0) return null;
            try
            {
                var handle = m.MethodHandle;
                if (Mono) handle.GetFunctionPointer();
                else RuntimeHelpers.PrepareMethod(handle);
                return null;
            }
            catch (Exception e)
            {
                return $"{Name(m.DeclaringType)}.{m.Name}: {Feature.Describe(e)}";
            }
        }

        /// <summary>A type's name with its declaring types ("Urp17.AstraPass"), no namespace.</summary>
        static string Name(Type t) => t.DeclaringType != null ? Name(t.DeclaringType) + "." + t.Name : t.Name;

        /// <summary>
        /// The public instance method <paramref name="name"/>(<paramref name="parameters"/>) of the
        /// interface <paramref name="iface"/>: declared on it, or on any interface it extends. An engine
        /// update can MOVE a method into a new base interface — Unity 6000.3 moved
        /// <c>IRasterRenderGraphBuilder.SetRenderAttachment</c> into
        /// <c>IRenderAttachmentRenderGraphBuilder</c> — and IL names the interface that declared it when
        /// it was compiled, so a call compiled against one line is "not found" on the other. Looked up
        /// by the exact parameter list (overloads never mix); the interface itself first, then its
        /// bases. Null when none has it.
        /// </summary>
        public static MethodInfo FindInterfaceMethod(Type iface, string name, params Type[] parameters)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var m = iface.GetMethod(name, flags, null, parameters, null);
            if (m != null) return m;
            foreach (var b in iface.GetInterfaces())
            {
                m = b.GetMethod(name, flags, null, parameters, null);
                if (m != null) return m;
            }
            return null;
        }

        /// <summary>
        /// An OPEN delegate to an interface method, bound once and called like the method itself: the
        /// delegate's first parameter is the instance (an interface type), the rest are the method's
        /// parameters, and the method is found by <see cref="FindInterfaceMethod"/> on that interface or
        /// any it extends — wherever this game's engine declares it. Throws
        /// <see cref="MissingMethodException"/> naming the method when none declares it, so a caller at
        /// load says what is missing and leaves its pass out.
        /// </summary>
        public static TDelegate InterfaceCall<TDelegate>(string name) where TDelegate : Delegate
        {
            var invoke = typeof(TDelegate).GetMethod("Invoke");
            var ps = invoke.GetParameters();
            if (ps.Length == 0 || !ps[0].ParameterType.IsInterface)
                throw new ArgumentException($"{typeof(TDelegate).Name}'s first parameter must be the interface the method is called on");
            var iface = ps[0].ParameterType;
            var types = ps.Skip(1).Select(p => p.ParameterType).ToArray();
            var m = FindInterfaceMethod(iface, name, types);
            if (m == null || m.ReturnType != invoke.ReturnType)
                throw new MissingMethodException(
                    $"{iface.FullName} (or an interface it extends) has no {invoke.ReturnType.Name} {name}({string.Join(", ", types.Select(t => t.Name))})");
            return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), m);
        }
    }
}
