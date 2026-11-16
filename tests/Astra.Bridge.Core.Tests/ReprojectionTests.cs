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

    /// <summary>The rows and the eye exactly as <c>Compositor.Prepare</c> builds them.</summary>
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
}
