// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Astra.Bridge;
using Xunit;

/// <summary>
/// The guard the Unity foundation runs before it puts a pipeline adapter's pass into a game's render
/// (<see cref="AdapterProof"/>), and the one-shot latch a pass inside the render keeps
/// (<see cref="Feature.Fail"/>) — away from UnityEngine. The shapes mirror Unity 6's render graph:
/// 6000.0 declares <c>SetRenderAttachment</c> on <c>IRasterRenderGraphBuilder</c>, 6000.3 moved it into
/// a new base interface, and foundation 0.8.0, compiled against the first, black-screened the second.
/// </summary>
public class AdapterProofTests
{
    // ---- the 6000.0 shape: the method on the builder's own interface ----
    public interface IBuilderOld : IDisposable
    {
        void SetAttachment(int tex, int index, int flags);
        void SetAttachment(int tex, int index, int flags, int mip, int slice);
    }

    // ---- the 6000.3 shape: moved into a base interface the builder now extends ----
    public interface IAttachmentBuilder
    {
        void SetAttachment(int tex, int index, int flags);
        void SetAttachment(int tex, int index, int flags, int mip, int slice);
    }

    public interface IBuilderNew : IAttachmentBuilder, IDisposable { }

    sealed class OldBuilder : IBuilderOld
    {
        public readonly List<string> Calls = new List<string>();
        public void SetAttachment(int tex, int index, int flags) => Calls.Add($"3:{tex},{index},{flags}");
        public void SetAttachment(int tex, int index, int flags, int mip, int slice) => Calls.Add($"5:{tex},{index},{flags},{mip},{slice}");
        public void Dispose() { }
    }

    sealed class NewBuilder : IBuilderNew
    {
        public readonly List<string> Calls = new List<string>();
        public void SetAttachment(int tex, int index, int flags) => Calls.Add($"3:{tex},{index},{flags}");
        public void SetAttachment(int tex, int index, int flags, int mip, int slice) => Calls.Add($"5:{tex},{index},{flags},{mip},{slice}");
        public void Dispose() { }
    }

    [Fact]
    public void the_method_is_found_where_each_line_declares_it()
    {
        var five = new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) };
        Assert.Equal(typeof(IBuilderOld), AdapterProof.FindInterfaceMethod(typeof(IBuilderOld), "SetAttachment", five).DeclaringType);
        Assert.Equal(typeof(IAttachmentBuilder), AdapterProof.FindInterfaceMethod(typeof(IBuilderNew), "SetAttachment", five).DeclaringType);
    }

    [Fact]
    public void an_overload_is_matched_by_its_whole_parameter_list()
    {
        var three = AdapterProof.FindInterfaceMethod(typeof(IBuilderNew), "SetAttachment", typeof(int), typeof(int), typeof(int));
        Assert.Equal(3, three.GetParameters().Length);
        Assert.Null(AdapterProof.FindInterfaceMethod(typeof(IBuilderNew), "SetAttachment", typeof(int), typeof(int)));
        Assert.Null(AdapterProof.FindInterfaceMethod(typeof(IBuilderNew), "SetAttachment", typeof(long), typeof(int), typeof(int)));
        Assert.Null(AdapterProof.FindInterfaceMethod(typeof(IBuilderNew), "Gone", typeof(int)));
    }

    [Fact]
    public void one_bound_call_reaches_the_builder_on_both_lines()
    {
        var onOld = AdapterProof.InterfaceCall<Action<IBuilderOld, int, int, int, int, int>>("SetAttachment");
        var old = new OldBuilder();
        onOld(old, 7, 0, 2, 0, -1);
        Assert.Equal(new[] { "5:7,0,2,0,-1" }, old.Calls);

        // The delegate's first parameter is the BUILDER's interface, the method its base interface's.
        var onNew = AdapterProof.InterfaceCall<Action<IBuilderNew, int, int, int, int, int>>("SetAttachment");
        var fresh = new NewBuilder();
        onNew(fresh, 7, 0, 2, 0, -1);
        onNew(fresh, 8, 1, 3, 0, -1);
        Assert.Equal(new[] { "5:7,0,2,0,-1", "5:8,1,3,0,-1" }, fresh.Calls);
    }

    [Fact]
    public void a_method_no_line_declares_is_named_at_bind_time()
    {
        var e = Assert.Throws<MissingMethodException>(() =>
            AdapterProof.InterfaceCall<Action<IBuilderNew, int, int>>("SetAttachment"));
        Assert.Contains("SetAttachment(Int32, Int32)", e.Message);
        Assert.Contains(nameof(IBuilderNew), e.Message);
        // A return type that differs is not the same method.
        Assert.Throws<MissingMethodException>(() =>
            AdapterProof.InterfaceCall<Func<IBuilderNew, int, int, int, int, int, bool>>("SetAttachment"));
    }

    // ---- the proof ----

    /// <summary>An adapter-shaped type: a pass nested in a static class, a lambda (its closure class
    /// is nested), an abstract member, a generic method, a delegate type, a type initializer.</summary>
    static class SoundAdapter
    {
        static readonly int Id = "x".Length;
        static readonly Func<int, int> twice = x => x * 2;
        static int Run(int v) => twice(v) + Id + Generic(v);
        static T Generic<T>(T v) => v;
        delegate void Callback(int v);
        abstract class Base { public abstract int Value(); }
        sealed class Pass : Base
        {
            int seen;
            public override int Value() => seen;
            public void Record(int v) { Action<int> keep = x => seen += x + v; keep(1); }
        }
    }

    [Fact]
    public void a_type_that_compiles_is_proven()
    {
        Assert.Null(AdapterProof.Prove(typeof(SoundAdapter)));
        Assert.Null(AdapterProof.Prove(typeof(AdapterProofTests)));
    }

    /// <summary>
    /// "Adapter" with a nested "Pass" whose Record calls a method of a type this process never gets
    /// (a TypeBuilder that is never created) — the shape of a member the game's engine lacks: the
    /// method fails as it is COMPILED, before any of it runs.
    /// </summary>
    static Type BrokenAdapter()
    {
        var asm = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ProofFixture" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.RunAndCollect);
        var mod = asm.DefineDynamicModule("ProofFixture");
        var absent = mod.DefineType("Absent", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var gone = absent.DefineMethod("SetRenderAttachment", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        gone.GetILGenerator().Emit(OpCodes.Ret);
        // never absent.CreateType(): the engine that would declare it is not this one

        var adapter = mod.DefineType("Adapter", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        var fine = adapter.DefineMethod("Enqueue", MethodAttributes.Public | MethodAttributes.Static, typeof(int), Type.EmptyTypes);
        var il = fine.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        var pass = adapter.DefineNestedType("Pass", TypeAttributes.NestedPrivate | TypeAttributes.Sealed);
        var record = pass.DefineMethod("Record", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        il = record.GetILGenerator();
        il.Emit(OpCodes.Call, gone);
        il.Emit(OpCodes.Ret);
        var made = adapter.CreateType();
        pass.CreateType();
        return made;
    }

    [Fact]
    public void a_pass_that_names_a_missing_member_is_refused_with_its_name()
    {
        string why = AdapterProof.Prove(BrokenAdapter());
        Assert.NotNull(why);
        // ONE line: the method that does not compile, with its nesting, and the runtime's reason.
        Assert.StartsWith("Adapter.Pass.Record: ", why);
        Assert.Contains("Absent", why);
        Assert.DoesNotContain("\n", why);
    }

    [Fact]
    public void the_proof_never_throws()
    {
        Assert.Equal("no type to prove", AdapterProof.Prove(null));
        Assert.Null(AdapterProof.Prove(typeof(List<>))); // an open generic cannot be compiled: passed over
    }

    // ---- the latch a pass inside the game's render keeps ----

    [Fact]
    public void a_pass_failure_switches_it_off_once_and_keeps_the_first_reason()
    {
        var running = new Feature("her pass inside the camera's render");
        var warnings = new List<string>();
        Assert.False(running.Off);
        running.Fail(new MissingMethodException("Method not found: void IRasterRenderGraphBuilder.SetRenderAttachment(TextureHandle,int,AccessFlags)"), warnings.Add);
        Assert.True(running.Off);
        Assert.StartsWith("MissingMethodException: Method not found: void IRasterRenderGraphBuilder.SetRenderAttachment", running.Why);
        // Every later frame's failure is ignored: never a line per frame, never a second reason.
        running.Fail(new InvalidOperationException("again"), warnings.Add);
        running.Fail(new InvalidOperationException("and again"), warnings.Add);
        Assert.Single(warnings);
        Assert.StartsWith("MissingMethodException", running.Why);
        // …and its body is never run again by Run either.
        int ran = 0;
        Assert.False(running.Run(() => ran++, warnings.Add));
        Assert.Equal(0, ran);
    }

    [Fact]
    public void a_pass_failure_inside_the_render_may_say_nothing_there()
    {
        // Inside the game's render nothing is logged (warn null): the foundation says it ONCE, as it
        // stands the adapter down at the next camera.
        var running = new Feature("her pass");
        running.Fail(new NullReferenceException("x"));
        Assert.True(running.Off);
        running.Fail(null);
        Assert.StartsWith("NullReferenceException: x", running.Why);
    }
}
