// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Reflection;
using Astra.Bridge;
using Xunit;

/// <summary>
/// The rule behind the Unity foundation's optional parts (<see cref="Feature"/>): a failure switches
/// one part off, once and loudly, and never stops the rest — away from UnityEngine.
/// </summary>
public class FeatureTests
{
    [Fact]
    public void a_body_that_finishes_answers_true_and_leaves_it_on()
    {
        var f = new Feature("her light");
        var warnings = new List<string>();
        int ran = 0;
        Assert.True(f.Run(() => ran++, warnings.Add));
        Assert.True(f.Run(() => ran++, warnings.Add));
        Assert.Equal(2, ran);
        Assert.False(f.Off);
        Assert.Null(f.Why);
        Assert.Empty(warnings);
    }

    [Fact]
    public void a_throw_never_escapes_and_switches_it_off()
    {
        var f = new Feature("her shadow");
        bool answer = f.Run(() => throw new NotSupportedException("Method unstripping failed"), _ => { });
        Assert.False(answer);
        Assert.True(f.Off);
        Assert.StartsWith("NotSupportedException: Method unstripping failed", f.Why);
    }

    [Fact]
    public void the_warning_fires_once_and_names_the_feature_and_the_reason()
    {
        var f = new Feature("her shadow");
        var warnings = new List<string>();
        f.Run(() => throw new InvalidOperationException("no physics"), warnings.Add);
        f.Run(() => throw new InvalidOperationException("again"), warnings.Add);
        Assert.Single(warnings);
        Assert.StartsWith("her shadow is off in this game: InvalidOperationException: no physics", warnings[0]);
    }

    [Fact]
    public void once_off_its_body_is_never_run_again()
    {
        var f = new Feature("the 'her:' log line");
        f.Run(() => throw new Exception("ICall with signature x was not resolved"), _ => { });
        int ran = 0;
        Assert.False(f.Run(() => ran++, _ => { }));
        Assert.False(f.Run(() => ran++, _ => { }));
        Assert.Equal(0, ran);
    }

    [Fact]
    public void the_undo_runs_once_on_the_failure_and_never_on_success()
    {
        var f = new Feature("her shadow");
        int undone = 0;
        f.Run(() => { }, _ => { }, () => undone++);
        Assert.Equal(0, undone);
        f.Run(() => throw new Exception("broken"), _ => { }, () => undone++);
        f.Run(() => throw new Exception("broken again"), _ => { }, () => undone++);
        Assert.Equal(1, undone);
    }

    [Fact]
    public void a_throw_from_the_undo_is_swallowed()
    {
        var f = new Feature("her shadow");
        var warnings = new List<string>();
        bool answer = f.Run(() => throw new Exception("broken"), warnings.Add, () => throw new ObjectDisposedException("caster"));
        Assert.False(answer);
        Assert.True(f.Off);
        Assert.Single(warnings);
    }

    [Fact]
    public void a_throw_from_the_warning_neither_escapes_nor_skips_the_undo()
    {
        var f = new Feature("her light");
        bool undone = false;
        bool answer = f.Run(() => throw new Exception("broken"), _ => throw new Exception("no log"), () => undone = true);
        Assert.False(answer);
        Assert.True(undone);
        Assert.True(f.Off);
    }

    [Fact]
    public void the_reason_is_the_failure_under_its_wrappers()
    {
        var stripped = new MissingMethodException("UnityEngine.Physics.RaycastNonAlloc");
        var wrapped = new TargetInvocationException(new TypeInitializationException("PhysicsQuery", stripped));
        Assert.Same(stripped, Feature.Cause(wrapped));
        Assert.Equal("MissingMethodException: UnityEngine.Physics.RaycastNonAlloc", Feature.Describe(wrapped));
        // A wrapper with nothing inside is its own cause.
        var empty = new TargetInvocationException(null);
        Assert.Same(empty, Feature.Cause(empty));
    }

    [Fact]
    public void a_reason_thrown_by_a_member_names_the_member()
    {
        Exception thrown = null;
        try { Throws(); }
        catch (Exception e) { thrown = e; }
        Assert.EndsWith($"(in {nameof(FeatureTests)}.{nameof(Throws)})", Feature.Describe(thrown));
    }

    [Fact]
    public void the_one_argument_form_passes_its_argument_and_keeps_the_same_rule()
    {
        var f = new Feature("her shadow");
        var warnings = new List<string>();
        var seen = new List<int>();
        int undone = 0;
        Action<int> body = x =>
        {
            if (x < 0) throw new NotSupportedException("Method unstripping failed");
            seen.Add(x);
        };
        Assert.True(f.Run(body, 3, warnings.Add, () => undone++));
        Assert.False(f.Run(body, -1, warnings.Add, () => undone++));
        Assert.False(f.Run(body, 4, warnings.Add, () => undone++)); // off: never run again
        Assert.Equal(new[] { 3 }, seen);
        Assert.Equal(1, undone);
        Assert.Single(warnings);
        Assert.StartsWith("her shadow is off in this game: NotSupportedException", warnings[0]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Throws() => throw new NotSupportedException("Method unstripping failed");
}
