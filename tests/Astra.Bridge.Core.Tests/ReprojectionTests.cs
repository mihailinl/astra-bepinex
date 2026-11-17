// SPDX-License-Identifier: MIT
using System;
using Astra.Bridge;
using Xunit;

/// <summary>
/// <see cref="Reprojection.ThroughHer"/>, checked with the composite shader's own arithmetic
/// (<c>AstraComposite.shader</c>, <c>Her</c>): a present ray r is looked up at M·r, and the point p found
/// there goes back into the present view as Mᵀ·p + e. The cameras are a third-person game's — one that
/// orbits the player, as Valheim's does — and a first-person one that turns where it stands.
/// </summary>
public class ReprojectionTests
{
    struct Cam
    {
        public Vec3 Pos, Right, Up, Fwd;
    }

    static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    static Vec3 Cross(Vec3 a, Vec3 b) => new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    static Vec3 Norm(Vec3 a) => a * (1 / a.Length);

    static Cam LookAt(Vec3 pos, Vec3 target)
    {
        var f = Norm(target - pos);
        var r = Norm(Cross(new Vec3(0, 1, 0), f));
        return new Cam { Pos = pos, Right = r, Up = Cross(f, r), Fwd = f };
    }

    /// <summary>A world point in a camera's view (x right, y up, z forward).</summary>
    static Vec3 In(Cam c, Vec3 x)
    {
        var d = x - c.Pos;
        return new Vec3(Dot(d, c.Right), Dot(d, c.Up), Dot(d, c.Fwd));
    }

    /// <summary>The rotation rows and the eye, before the plane through her (what
    /// <see cref="Reprojection.Rows"/> gives while her centre is unknown).</summary>
    static (Vec3 r0, Vec3 r1, Vec3 r2, Vec3 eye) Rows(Cam then, Cam now)
    {
        Vec3 Row(Vec3 a) => new Vec3(Dot(a, now.Right), Dot(a, now.Up), Dot(a, now.Fwd));
        return (Row(then.Right), Row(then.Up), Row(then.Fwd), Row(then.Pos - now.Pos));
    }

    /// <summary>The shader's lookup: where in the earlier picture (as x/z, y/z of its view) the present
    /// ray through <paramref name="pNow"/> lands.</summary>
    static (double x, double y) Lookup(Vec3 r0, Vec3 r1, Vec3 r2, Vec3 pNow)
    {
        var r = pNow * (1 / pNow.Z);
        var t = new Vec3(Dot(r0, r), Dot(r1, r), Dot(r2, r));
        return (t.X / t.Z, t.Y / t.Z);
    }

    static double Miss((double x, double y) a, Vec3 truthThen) =>
        Math.Max(Math.Abs(a.x - truthThen.X / truthThen.Z), Math.Abs(a.y - truthThen.Y / truthThen.Z));

    /// <summary>The shader's way back: Mᵀ·p + e.</summary>
    static Vec3 Back(Vec3 r0, Vec3 r1, Vec3 r2, Vec3 eye, Vec3 pThen) => r0 * pThen.X + r1 * pThen.Y + r2 * pThen.Z + eye;

    // The player's head, the camera 4 m behind and 1 m above it, and her beside the player.
    static readonly Vec3 Pivot = new Vec3(0, 1.6, 0);
    static readonly Vec3 Her = new Vec3(1.2, 1.0, 0.5);

    static Cam Orbit(double degrees)
    {
        double a = degrees * Math.PI / 180;
        return LookAt(Pivot + new Vec3(4 * Math.Sin(a), 1, -4 * Math.Cos(a)), Pivot);
    }

    [Fact]
    public void A_camera_turning_where_it_stands_keeps_the_rotation()
    {
        var then = LookAt(new Vec3(0, 1.7, 0), new Vec3(0.3, 1.5, 5));
        var now = LookAt(new Vec3(0, 1.7, 0), new Vec3(-1.0, 1.9, 5));
        var (r0, r1, r2, eye) = Rows(then, now);
        var (h0, h1, h2, e) = (r0, r1, r2, eye);
        Assert.True(Reprojection.ThroughHer(ref h0, ref h1, ref h2, ref e, In(then, Her + new Vec3(0, 0, 3))));
        foreach (var (a, b) in new[] { (h0, r0), (h1, r1), (h2, r2), (e, eye) })
            Assert.True((a - b).Length < 1e-12, $"{a} vs {b}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(60)]
    public void An_orbiting_camera_finds_every_point_at_her_depth_where_the_rotation_alone_missed(double turn)
    {
        Cam then = Orbit(0), now = Orbit(turn);
        var (r0, r1, r2, eye) = Rows(then, now);
        var (h0, h1, h2, e) = (r0, r1, r2, eye);
        Assert.True(Reprojection.ThroughHer(ref h0, ref h1, ref h2, ref e, In(then, Her)));
        double worstNew = 0, oldAtHer = Miss(Lookup(r0, r1, r2, In(now, Her)), In(then, Her));
        // Points on the plane through her, facing the camera her picture was drawn for.
        for (double dx = -0.4; dx <= 0.4; dx += 0.2)
            for (double dy = -0.8; dy <= 0.8; dy += 0.4)
            {
                var x = Her + then.Right * dx + then.Up * dy;
                worstNew = Math.Max(worstNew, Miss(Lookup(h0, h1, h2, In(now, x)), In(then, x)));
            }
        Assert.True(worstNew < 1e-9, $"missed by {worstNew}");
        // The rotation alone missed her by about the angle the camera turned (she is near the pivot).
        Assert.True(oldAtHer > 0.4 * turn * Math.PI / 180, $"the rotation missed by {oldAtHer} only");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void The_rest_of_her_body_is_off_only_by_the_parallax_of_her_own_depth(double turn)
    {
        Cam then = Orbit(0), now = Orbit(turn);
        var (r0, r1, r2, eye) = Rows(then, now);
        var (h0, h1, h2, e) = (r0, r1, r2, eye);
        Assert.True(Reprojection.ThroughHer(ref h0, ref h1, ref h2, ref e, In(then, Her)));
        double worstNew = 0, worstOld = 0;
        foreach (double dz in new[] { -0.25, 0.25 })
            foreach (double dx in new[] { -0.3, 0.3 })
            {
                var x = Her + then.Fwd * dz + then.Right * dx;
                worstNew = Math.Max(worstNew, Miss(Lookup(h0, h1, h2, In(now, x)), In(then, x)));
                worstOld = Math.Max(worstOld, Miss(Lookup(r0, r1, r2, In(now, x)), In(then, x)));
            }
        Assert.True(worstNew < 0.1 * worstOld, $"new {worstNew} vs rotation alone {worstOld}");
        Assert.True(worstNew < 0.002 * turn, $"new {worstNew} at a {turn}° turn");
    }

    [Fact]
    public void Her_depth_back_in_the_present_view_is_exact_at_her_centre_and_close_over_her_body()
    {
        // About a 60 FPS frame of a fast turn.
        Cam then = Orbit(0), now = Orbit(2);
        var (r0, r1, r2, eye) = Rows(then, now);
        var (h0, h1, h2, e) = (r0, r1, r2, eye);
        Assert.True(Reprojection.ThroughHer(ref h0, ref h1, ref h2, ref e, In(then, Her)));
        Assert.True(Math.Abs(Back(h0, h1, h2, e, In(then, Her)).Z - In(now, Her).Z) < 1e-9);
        double worst = 0;
        foreach (double dz in new[] { -0.25, 0, 0.25 })
            foreach (double dx in new[] { -0.3, 0.3 })
                foreach (double dy in new[] { -0.8, 0.8 })
                {
                    var x = Her + then.Fwd * dz + then.Right * dx + then.Up * dy;
                    worst = Math.Max(worst, Math.Abs(Back(h0, h1, h2, e, In(then, x)).Z - In(now, x).Z));
                }
        // Under the composite's own depth bias (3 cm by default).
        Assert.True(worst < 0.015, $"depth off by {worst} m");
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void Her_centre_not_ahead_of_the_camera_changes_nothing(double depth)
    {
        Cam then = Orbit(0), now = Orbit(5);
        var (r0, r1, r2, eye) = Rows(then, now);
        var (h0, h1, h2, e) = (r0, r1, r2, eye);
        Assert.False(Reprojection.ThroughHer(ref h0, ref h1, ref h2, ref e, new Vec3(0.2, 0.1, depth)));
        Assert.Equal((r0.X, r0.Y, r0.Z, r1.X, r2.Z, eye.X, eye.Z), (h0.X, h0.Y, h0.Z, h1.X, h2.Z, e.X, e.Z));
    }

    static Reprojection.View V(Cam c) => new Reprojection.View(c.Pos, c.Right, c.Up, c.Fwd);

    static Cam Moved(Cam c, Vec3 by) => new Cam { Pos = c.Pos + by, Right = c.Right, Up = c.Up, Fwd = c.Fwd };

    [Fact]
    public void Without_her_centre_the_rows_are_the_rotation_and_the_eye_the_cameras_move()
    {
        Cam then = Orbit(0), now = Orbit(7);
        var (r0, r1, r2, eye) = Rows(then, now);
        Assert.True(Reprojection.Rows(V(then), Her, V(now), null, out var h0, out var h1, out var h2, out var e));
        foreach (var (a, b) in new[] { (h0, r0), (h1, r1), (h2, r2), (e, eye) })
            Assert.True((a - b).Length < 1e-12, $"{a} vs {b}");
    }

    [Fact]
    public void Her_centre_unknown_then_takes_the_world_as_standing_still()
    {
        Cam then = Orbit(0), now = Orbit(12);
        var (r0, r1, r2, eye) = Rows(then, now);
        Assert.True(Reprojection.ThroughHer(ref r0, ref r1, ref r2, ref eye, In(then, Her)));
        Assert.True(Reprojection.Rows(V(then), null, V(now), Her, out var h0, out var h1, out var h2, out var e));
        foreach (var (a, b) in new[] { (h0, r0), (h1, r1), (h2, r2), (e, eye) })
            Assert.True((a - b).Length < 1e-12, $"{a} vs {b}");
    }

    [Theory]
    [InlineData(0.15, 0.0)] // 5 m/s for 30 ms, sideways
    [InlineData(0.0, 0.15)] // the same, straight ahead
    [InlineData(0.6, 0.3)]  // a picture 15 frames old at 60 FPS
    public void She_runs_with_the_camera_and_stays_where_she_was_on_screen(double side, double ahead)
    {
        // Third person behind the player, she runs beside it: the camera and she move by the same.
        Cam then = Orbit(0);
        var run = then.Right * side + new Vec3(then.Fwd.X, 0, then.Fwd.Z) * ahead;
        Cam now = Moved(then, run);
        Vec3 herThen = Her, herNow = Her + run;
        Assert.True(Reprojection.Rows(V(then), herThen, V(now), herNow, out var h0, out var h1, out var h2, out var e));
        Assert.True(Reprojection.Rows(V(then), null, V(now), herNow, out var w0, out var w1, out var w2, out _));
        double worstNew = 0, worstWorld = 0;
        foreach (double dz in new[] { -0.25, 0, 0.25 })
            foreach (double dx in new[] { -0.3, 0.3 })
                foreach (double dy in new[] { -0.8, 0.8 })
                {
                    // A point of her body then, and the same point now (she moved with the run).
                    var offset = then.Fwd * dz + then.Right * dx + then.Up * dy;
                    var truthThen = In(then, herThen + offset);
                    worstNew = Math.Max(worstNew, Miss(Lookup(h0, h1, h2, In(now, herNow + offset)), truthThen));
                    worstWorld = Math.Max(worstWorld, Miss(Lookup(w0, w1, w2, In(now, herNow + offset)), truthThen));
                    // Back in the present view, the point is where her body is now.
                    var back = Back(h0, h1, h2, e, truthThen);
                    Assert.True((back - In(now, herNow + offset)).Length < 1e-9, $"back {back} vs {In(now, herNow + offset)}");
                }
        Assert.True(worstNew < 1e-9, $"missed by {worstNew}");
        // Taking the world as standing still missed her by the run seen from her depth (15 cm at ~4 m).
        Assert.True(worstWorld > 0.5 * Math.Max(side, ahead * 0.1) / 4.5, $"the world-still lookup missed by {worstWorld} only");
    }

    static Cam Pitch(double degrees)
    {
        // The camera swings up and down around the player's head, 4 m behind it.
        double a = degrees * Math.PI / 180;
        return LookAt(Pivot + new Vec3(0, 4 * Math.Sin(a), -4 * Math.Cos(a)), Pivot);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(30)]
    public void A_camera_swinging_up_and_down_finds_every_point_at_her_depth(double swing)
    {
        Cam then = Pitch(10), now = Pitch(10 + swing);
        Assert.True(Reprojection.Rows(V(then), Her, V(now), Her, out var h0, out var h1, out var h2, out _));
        double worst = 0;
        for (double dx = -0.4; dx <= 0.4; dx += 0.2)
            for (double dy = -0.8; dy <= 0.8; dy += 0.4)
            {
                var x = Her + then.Right * dx + then.Up * dy;
                worst = Math.Max(worst, Miss(Lookup(h0, h1, h2, In(now, x)), In(then, x)));
            }
        Assert.True(worst < 1e-9, $"missed by {worst}");
    }

    [Theory]
    [InlineData(2, 0.03)]
    [InlineData(10, 0.14)]
    public void Her_depth_over_her_body_is_off_by_what_the_shared_rows_allow(double swing, double bound)
    {
        // Known limit (RENDER-CASES 17, B2): the shader moves a found point back with the SAME rows it
        // looked it up with, so her depth is exact at her centre and drifts towards her head and feet as
        // the camera swings. Separate rows for depth are a shader change; until then this pins the drift.
        Cam then = Pitch(10), now = Pitch(10 + swing);
        Assert.True(Reprojection.Rows(V(then), Her, V(now), Her, out var h0, out var h1, out var h2, out var e));
        Assert.True(Math.Abs(Back(h0, h1, h2, e, In(then, Her)).Z - In(now, Her).Z) < 1e-9);
        double worst = 0;
        foreach (double dy in new[] { -0.8, 0.8 })
            foreach (double dz in new[] { -0.25, 0.25 })
            {
                var x = Her + then.Up * dy + then.Fwd * dz;
                worst = Math.Max(worst, Math.Abs(Back(h0, h1, h2, e, In(then, x)).Z - In(now, x).Z));
            }
        Assert.True(worst < bound, $"depth off by {worst} m at a {swing}° swing");
    }

    [Theory]
    [InlineData(0.0)]   // her centre right at the present eye
    [InlineData(-0.5)]  // behind it
    [InlineData(0.03)]  // nearer than MinDepth
    public void Her_centre_at_or_behind_the_present_eye_is_not_drawn(double ahead)
    {
        Cam then = Orbit(0);
        // The present camera has come up to her (a teleport, a cut): her centre is `ahead` metres in front.
        Cam now = Moved(then, (Her - then.Pos) - then.Fwd * ahead);
        Assert.False(Reprojection.Rows(V(then), Her, V(now), Her, out _, out _, out _, out _));
    }
}
