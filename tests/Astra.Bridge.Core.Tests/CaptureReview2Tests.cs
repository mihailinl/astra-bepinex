// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Xunit;

/// <summary>
/// The capture review's round 2 (runner): a bone the game aims held still (R2-1), two captures of
/// one clip name both kept (R2-2), a flickering parameter neither splitting a take nor flooding the
/// log (R2-3) — and the reviewer's own repros (ReviewRepro2), kept as the behaviour they now show.
/// </summary>
public class CaptureReview2Tests : IDisposable
{
    readonly string inbox = Directory.CreateTempSubdirectory("astra-capture-r2-").FullName;
    readonly CaptureRig.Sampler rig = new CaptureRig.Sampler();
    readonly List<Action> pending = new List<Action>();
    readonly List<string> log = new List<string>();
    readonly List<string> sent = new List<string>();
    double now = 100;

    public void Dispose() => Directory.Delete(inbox, true);
    string[] Files() => Directory.GetFiles(inbox).Select(Path.GetFileName).OrderBy(f => f).ToArray();

    void RunWrites()
    {
        foreach (var a in pending.ToArray()) a();
        pending.Clear();
    }

    CaptureRecorder Deferred(string clip, string param, OfferGate g) =>
        new CaptureRecorder(clip, param, l => log.Add(l), a => pending.Add(a), g);

    CaptureRecorder AtOnce(string clip = "climb") => new CaptureRecorder(clip, "climbing", l => log.Add(l), a => a());

    void Play(CaptureRecorder[] rs, double seconds, bool want, string dir)
    {
        for (int i = 0; i < (int)Math.Round(seconds * 60); i++)
        {
            now += 1 / 60.0;
            foreach (var r in rs) r.Step(now, dir, want, () => rig, j => { sent.Add(j); return true; });
        }
    }

    /// <summary>R2-2 (repro <c>same_clip_two_recorders_one_take_lost</c>): two captures of the SAME
    /// clip name that finish on one frame both keep their takes — the game's <see cref="OfferGate"/>
    /// reserves each take's second, so the second writer never meets the first one's file.</summary>
    [Fact]
    public void two_captures_of_one_clip_name_both_keep_their_takes()
    {
        var g = new OfferGate();
        var a = Deferred("climb", "climbing", g);
        var b = Deferred("climb", "roping", g);
        Play(new[] { a, b }, 2.5, true, inbox);
        Play(new[] { a, b }, 0.25, false, inbox); // both end this frame; their writers wait
        Assert.Equal(2, pending.Count);
        RunWrites();
        Play(new[] { a, b }, 2, false, inbox);
        Assert.Equal(2, Files().Length);
        Assert.DoesNotContain(log, l => l.StartsWith("could not write"));
        Assert.Equal(2, sent.Count);
    }

    /// <summary>Repro <c>null_inbox_mid_write_leaves_nothing</c>: capture off while the writer is
    /// mid-write leaves nothing, and nothing is offered.</summary>
    [Fact]
    public void null_inbox_mid_write_leaves_nothing()
    {
        var a = Deferred("climb", "climbing", new OfferGate());
        Play(new[] { a }, 2.5, true, inbox);
        Play(new[] { a }, 0.25, false, inbox);
        Assert.Equal(CaptureState.Writing, a.State);
        Play(new[] { a }, 0.1, false, null);
        RunWrites();
        Assert.Empty(Files());
        Assert.Empty(sent);
    }

    /// <summary>Repro <c>dispose_mid_write_leaves_nothing</c>.</summary>
    [Fact]
    public void dispose_mid_write_leaves_nothing()
    {
        var a = Deferred("climb", "climbing", new OfferGate());
        Play(new[] { a }, 2.5, true, inbox);
        Play(new[] { a }, 0.25, false, inbox);
        a.Dispose();
        RunWrites();
        Assert.Empty(Files());
    }

    /// <summary>R2-3 (repro <c>flicker_logs</c>, which counted 500+ lines in 10 s): a parameter that
    /// flips every frame for 10 s makes no take and NOT ONE log line — each flicker ends inside the
    /// lead-in. One that stays on just long enough to sample, then off, again and again for 60 s,
    /// says "not kept" at most once per 10 s.</summary>
    [Fact]
    public void a_flickering_parameter_neither_takes_nor_floods_the_log()
    {
        var a = AtOnce();
        for (int i = 0; i < 600; i++)
        {
            now += 1 / 60.0;
            a.Step(now, inbox, i % 2 == 0, () => rig, j => { sent.Add(j); return true; });
        }
        Assert.Empty(sent);
        Assert.Empty(log);

        var b = AtOnce("rope");
        for (int k = 0; k < 60; k++)
        {
            Play(new[] { b }, 0.6, true, inbox);  // 0.3 s sampled: too short to keep
            Play(new[] { b }, 0.4, false, inbox);
        }
        Assert.Empty(sent);
        int notKept = log.Count(l => l.Contains("not kept"));
        Assert.InRange(notKept, 1, 7);
        Assert.Contains(log, l => l.Contains("more like it before"));
    }

    /// <summary>R2-3: a false shorter than <see cref="CaptureRecorder.EndAfter"/> mid-climb (a
    /// one-frame hiccup of the game's flag) does not split the take: one file, the whole climb.</summary>
    [Fact]
    public void a_one_frame_false_does_not_split_a_take()
    {
        var a = AtOnce();
        Play(new[] { a }, 1.5, true, inbox);
        Play(new[] { a }, 1 / 60.0, false, inbox);
        Play(new[] { a }, 1.5, true, inbox);
        Assert.Equal(CaptureState.Recording, a.State);
        Play(new[] { a }, 0.5, false, inbox);
        var file = Assert.Single(Files());
        Assert.Single(sent);
        var (json, _) = VrmaWriterTests.ReadGlb(File.ReadAllBytes(Path.Combine(inbox, file)));
        double max = json.GetProperty("accessors")[0].GetProperty("max")[0].GetDouble();
        Assert.InRange(max, 3.0 - CaptureRecorder.LeadIn - 0.1, 3.0 - CaptureRecorder.LeadIn + 0.05);
    }

    /// <summary>
    /// R2-1: a bone held still. An "aim" bone between the spine and the chest turns with the camera
    /// (pitch 0 → 40°) while the chest's own pose under it holds: taken out, the chest's world
    /// rotation stays the spine's · the aim's BIND rotation · the chest's own, every frame, and the
    /// chest's position turns back about the aim's pivot. The largest turn taken out is said.
    /// </summary>
    [Fact]
    public void a_held_bone_takes_the_cameras_turn_out_of_everything_below_it()
    {
        var spine = CaptureRig.Arbitrary(1, 30);
        var aimBind = Quat.AngleAxis(5, new Vec3(1, 0, 0));
        var chestOwn = CaptureRig.Arbitrary(2, 25);
        var pivot = new Vec3(0, 1.3, 0);
        var held = new HeldBone("AimJoint", aimBind, new[] { 0, 1 });
        Quat expected = (spine * aimBind * chestOwn).Normalized;
        foreach (double pitch in new[] { 0.0, 15, 40, -20 })
        {
            var aimLive = Quat.AngleAxis(pitch, new Vec3(1, 0, 0));
            var aimWorld = spine * aimLive;
            var chestWorld = aimWorld * chestOwn;
            var world = new[] { chestWorld, chestWorld };
            var bindChestPos = pivot + (spine * aimBind).Rotate(new Vec3(0, 0.3, 0));
            var pos = new[] { pivot + aimWorld.Rotate(new Vec3(0, 0.3, 0)), pivot };
            held.Apply(spine, aimWorld, pivot, world, pos);
            Assert.True(CaptureRig.Near(world[0], expected, 0.01), $"pitch {pitch}: {world[0]} vs {expected}");
            Assert.True(CaptureRig.Near(pos[0], bindChestPos, 1e-9), $"pitch {pitch}: the chest turned back about the aim's pivot");
            Assert.True(CaptureRig.Near(pos[1], pivot, 1e-9), "the pivot itself stays");
        }
        Assert.InRange(held.Most, 34.99, 35.01); // 40° less the bind's 5°
        held.ResetMost();
        Assert.Equal(0, held.Most);
    }
}
