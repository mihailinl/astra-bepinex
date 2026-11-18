// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Astra.Bridge;
using Xunit;

/// <summary>A ring written by hand to the protocol's layout, read back through <see cref="FrameRing"/>.</summary>
public class FrameRingTests : IDisposable
{
    const int MaxW = 8, MaxH = 4, Slots = 3, Header = 4096;
    const long Stride = 8L * MaxW * MaxH + 128 + 4L * 16 * 16; // colour + depth + a 16² sun view
    readonly string path = Path.Combine(Path.GetTempPath(), $"astra-ring-test-{Guid.NewGuid():N}");
    readonly byte[] mem = new byte[Header + Slots * Stride];

    public FrameRingTests()
    {
        Put(0, FrameRing.Magic);
        Put(4, 1u);
        Put(8, (uint)Header);
        Put(12, (uint)Slots);
        Put(16, Stride);
        Put(24, (uint)MaxW);
        Put(28, (uint)MaxH);
        Put(40, -1);
        Flush();
    }

    public void Dispose() => File.Delete(path);

    void Put(long at, uint v) => BitConverter.GetBytes(v).CopyTo(mem, at);
    void Put(long at, int v) => BitConverter.GetBytes(v).CopyTo(mem, at);
    void Put(long at, long v) => BitConverter.GetBytes(v).CopyTo(mem, at);
    void Put(long at, float v) => BitConverter.GetBytes(v).CopyTo(mem, at);
    void Put(long at, double v) => BitConverter.GetBytes(v).CopyTo(mem, at);
    void Flush() => File.WriteAllBytes(path, mem);

    /// <summary>Publish a w×h frame into <paramref name="slot"/> the way the engine does.</summary>
    /// <paramref name="ns"/> is the engine's publish clock (default: later publishes, later times);
    /// <paramref name="numbered"/> = false writes no publish number, as an engine from before views.
    void Publish(int slot, long counter, long host, int w, int h, long seq = 2, uint flags = FrameRing.FlagLinearDepth | FrameRing.FlagNoPlane3, int view = 0, long? ns = null, bool numbered = true)
    {
        long d = 256 + 128 * slot;
        Put(d, seq);
        Put(d + 84, view);
        Put(d + 8, 100 + counter);
        Put(d + 16, host);
        Put(d + 24, (uint)w);
        Put(d + 28, (uint)h);
        Put(d + 32, 0.05f);
        Put(d + 36, 1000f);
        Put(d + 40, 60f);
        Put(d + 44, flags);
        Put(d + 48, 1.5);
        Put(d + 56, 2.5);
        Put(d + 64, -3.5);
        Put(d + 72, 7f);
        Put(d + 96, ns ?? 5000 + 1000 * counter);
        Put(d + 104, numbered ? counter : 0L);
        long data = Header + slot * Stride;
        for (int i = 0; i < w * h; i++)
        {
            mem[data + 4 * i + 3] = (byte)(i + 1); // alpha = pixel index + 1
            Put(data + 4L * w * h + 4 * i, 2f + i);
        }
        Put(40, slot);
        Put(32, counter);
        Flush();
    }

    [Fact]
    public void a_published_frame_is_read_in_place()
    {
        Publish(1, counter: 5, host: 1234, w: 4, h: 2);
        using var ring = FrameRing.Open(path);
        Assert.Equal((Slots, MaxW, MaxH), (ring.Slots, ring.MaxWidth, ring.MaxHeight));
        long seen = 0;
        Assert.True(ring.TryLatest(ref seen, out var f));
        Assert.Equal(5, seen);
        Assert.Equal((1, 1234L, 4, 2), (f.Slot, f.HostFrame, f.Width, f.Height));
        Assert.Equal(new Vec3(1.5, 2.5, -3.5), f.CamPos);
        Assert.Equal((60f, 7f), (f.FovY, f.Echo0));
        Assert.Equal(32, f.PlaneBytes);
        unsafe
        {
            Assert.Equal(8, ((byte*)f.Colour)[4 * 7 + 3]); // the 8th pixel's alpha
            Assert.Equal(9f, ((float*)f.Depth)[7]);
        }
        Assert.False(f.HasSun);
        Assert.True(ring.StillValid(ref f));
        Assert.False(ring.TryLatest(ref seen, out _), "nothing new since");
    }

    [Fact]
    public void a_slot_being_written_is_not_read_and_a_reused_slot_invalidates_the_copy()
    {
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Publish(0, counter: 1, host: 1, w: 2, h: 2, seq: 3); // odd: mid-write
        Assert.False(ring.TryLatest(ref seen, out _));
        Assert.Equal(0, seen); // so the next call tries again

        Publish(0, counter: 1, host: 1, w: 2, h: 2, seq: 4);
        Assert.True(ring.TryLatest(ref seen, out var f));
        Publish(0, counter: 2, host: 2, w: 2, h: 2, seq: 6); // the engine came round to it again
        Assert.False(ring.StillValid(ref f));
    }

    /// <summary>Several views share the ring: each reader finds the newest frame of ITS view, whatever
    /// the latest-slot pointer says.</summary>
    [Fact]
    public void each_view_finds_its_own_newest_frame()
    {
        Publish(0, counter: 1, host: 10, w: 2, h: 2, view: 0);   // engine frame 101
        Publish(1, counter: 2, host: 11, w: 2, h: 2, view: 1);   // 102
        Publish(2, counter: 3, host: 12, w: 2, h: 2, view: 0);   // 103, the latest slot
        using var ring = FrameRing.Open(path);
        long seen0 = 0, seen1 = 0, seen2 = 0;
        Assert.True(ring.TryLatestOf(0, ref seen0, out var f0));
        Assert.Equal((12L, 0, 3L), (f0.HostFrame, f0.View, seen0));
        Assert.True(ring.TryLatestOf(1, ref seen1, out var f1));
        Assert.Equal((11L, 1), (f1.HostFrame, f1.View));
        Assert.False(ring.TryLatestOf(2, ref seen2, out _), "no frame of view 2");
        Assert.False(ring.TryLatestOf(0, ref seen0, out _), "nothing newer of view 0");
        Publish(1, counter: 4, host: 13, w: 2, h: 2, seq: 3, view: 1); // view 1's slot being rewritten
        Assert.False(ring.TryLatestOf(1, ref seen1, out _));
    }

    /// <summary>The ring file outlives an engine: a restarted one counts its frames AND its clock from
    /// zero while the slots still hold the old one's. Only the publish number keeps counting.</summary>
    [Fact]
    public void a_restarted_engines_frames_win_over_the_old_ones_still_in_the_ring()
    {
        Publish(0, counter: 900, host: 900, w: 2, h: 2, ns: 3_600_000_000_000); // an hour into the old run
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Assert.True(ring.TryLatestOf(0, ref seen, out var old));
        Assert.Equal(900L, old.HostFrame);
        Publish(1, counter: 901, host: 1, w: 2, h: 2, ns: 40_000_000);  // the new engine's first frame…
        Put(256 + 128 + 8, 1L);                                           // …numbered 1 by the engine
        Flush();
        Assert.True(ring.TryLatestOf(0, ref seen, out var fresh), "a later publish is newer, whatever its frame or its time");
        Assert.Equal(1L, fresh.HostFrame);
    }

    /// <summary>An engine from before views writes no publish number: view 0 is read through the
    /// latest-slot pointer, and nothing is found for another view.</summary>
    [Fact]
    public void an_engine_without_views_is_read_through_its_latest_slot()
    {
        Publish(2, counter: 4, host: 40, w: 2, h: 2, numbered: false);
        Publish(0, counter: 5, host: 41, w: 2, h: 2, numbered: false);
        using var ring = FrameRing.Open(path);
        long seen = 0, seen1 = 0;
        Assert.True(ring.TryLatestOf(0, ref seen, out var f));
        Assert.Equal((41L, 5L), (f.HostFrame, seen));
        Assert.False(ring.TryLatestOf(0, ref seen, out _), "nothing new since");
        Assert.False(ring.TryLatestOf(1, ref seen1, out _));
        Publish(1, counter: 6, host: 42, w: 2, h: 2, numbered: false);
        Assert.True(ring.TryLatestOf(0, ref seen, out f));
        Assert.Equal(42L, f.HostFrame);
    }

    [Fact]
    public void a_descriptor_larger_than_the_ring_is_refused()
    {
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Publish(2, counter: 1, host: 1, w: MaxW + 1, h: 1);
        Assert.False(ring.TryLatest(ref seen, out _));
    }

    [Fact]
    public void the_sun_view_is_found_behind_the_picture()
    {
        int w = 4, h = 2, n = 16;
        long sun = Header + 2 * Stride + 8L * w * h;
        Put(sun, 0x564E5553u);
        Put(sun + 4, (uint)n);
        Put(sun + 8, 10.0);
        Put(sun + 56, 0f);
        Put(sun + 60, -1f);
        Put(sun + 64, 0f);
        Put(sun + 68, 1.25f);
        Put(sun + 72, 3f);
        Put(sun + 128 + 4 * 5, 0.75f);
        Publish(2, counter: 9, host: 9, w: w, h: h, flags: FrameRing.FlagLinearDepth | FrameRing.FlagSunView);
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Assert.True(ring.TryLatest(ref seen, out var f));
        Assert.True(f.HasSun);
        Assert.Equal((16, 10.0, -1.0, 1.25f, 3f), (f.Sun.N, f.Sun.Centre.X, f.Sun.Dir.Y, f.Sun.HalfExtent, f.Sun.Near));
        unsafe { Assert.Equal(0.75f, ((float*)f.Sun.Depth)[5]); }
    }

    [Fact]
    public void a_sun_view_that_does_not_fit_its_slot_is_ignored()
    {
        long sun = Header + 2 * Stride + 8L * 4 * 2;
        Put(sun, 0x564E5553u);
        Put(sun + 4, 4096u); // 64 MiB: far past the slot
        Publish(2, counter: 3, host: 3, w: 4, h: 2, flags: FrameRing.FlagLinearDepth | FrameRing.FlagSunView);
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Assert.True(ring.TryLatest(ref seen, out var f));
        Assert.False(f.HasSun);
    }

    /// <summary>Her shadow caster is found through the descriptor's +112 offset behind flag 64, every
    /// array inside the slot; one whose arrays would run past the slot is not handed out.</summary>
    [Fact]
    public void the_shadow_caster_is_found_behind_flag_64()
    {
        int w = 2, h = 2;
        long data = Header + 1 * Stride;
        long off = 8L * w * h; // right after the picture (no sun view)
        long c = data + off;
        Put(c, 0x54534143u);
        Put(c + 4, 3u);
        Put(c + 8, 1u);
        Put(c + 16, 0x0123456789abcdefL);
        Put(c + 24, 100.5);
        Put(c + 32, -2.0);
        Put(c + 40, 7.25);
        Put(c + 64 + 12 * 2 + 4, 1.5f);                         // the third position's y
        BitConverter.GetBytes((ushort)2).CopyTo(mem, c + 64 + 24 * 3 + 4); // the third index
        Put(256 + 128 * 1 + 112, (uint)off);
        Publish(1, counter: 4, host: 4, w: w, h: h, flags: FrameRing.FlagLinearDepth | FrameRing.FlagNoPlane3 | FrameRing.FlagCaster);
        using var ring = FrameRing.Open(path);
        long seen = 0;
        Assert.True(ring.TryLatestOf(0, ref seen, out var f));
        Assert.True(f.HasCaster);
        Assert.Equal((3, 1, 0x0123456789abcdefUL), (f.Caster.Vertices, f.Caster.Triangles, f.Caster.Topology));
        Assert.Equal(new Vec3(100.5, -2, 7.25), f.Caster.Origin);
        unsafe
        {
            Assert.Equal(1.5f, ((float*)f.Caster.Positions)[7]);
            Assert.Equal(2, ((ushort*)f.Caster.Indices)[2]);
        }

        Put(c + 4, 60000u); // 60 000 vertices: far past the slot
        Publish(1, counter: 5, host: 5, w: w, h: h, flags: FrameRing.FlagLinearDepth | FrameRing.FlagNoPlane3 | FrameRing.FlagCaster);
        Assert.True(ring.TryLatestOf(0, ref seen, out f));
        Assert.False(f.HasCaster);
    }

    void Put(long at, ushort v) => BitConverter.GetBytes(v).CopyTo(mem, at);

    /// <summary>Writes one bone entry at <paramref name="e"/>.</summary>
    void Bone(long e, ushort index, ushort flags, float px, float py, float pz, float x, float y, float z, float w)
    {
        Put(e, index);
        Put(e + 2, flags);
        Put(e + 4, px);
        Put(e + 8, py);
        Put(e + 12, pz);
        Put(e + 16, x);
        Put(e + 20, y);
        Put(e + 24, z);
        Put(e + 28, w);
    }

    /// <summary>Her bones (engine #80) are found through the descriptor's +124 offset behind flag 256:
    /// the section's revision and origin, each entry's index into the hello's list, its valid flag and
    /// its pose made world (origin + position). An entry flagged valid whose numbers are not finite is
    /// not valid; a gap in the indices is found as none. A section past its slot, with more than 8
    /// entries, a wrong magic or without the flag is not handed out.</summary>
    [Fact]
    public void her_bones_are_found_behind_flag_256()
    {
        int w = 2, h = 2;
        long data = Header + 1 * Stride;
        long off = 8L * w * h; // right after the picture
        long s = data + off;
        Put(s, 0x454E4F42u);
        Put(s + 4, 3u);
        Put(s + 8, 17u);
        Put(s + 16, 100.5);
        Put(s + 24, -2.0);
        Put(s + 32, 7.25);
        Bone(s + 48, 0, 1, 0.25f, 1.5f, -0.5f, 0f, 0.7071f, 0f, 0.7071f);
        Bone(s + 80, 2, 0, 9f, 9f, 9f, 0f, 0f, 0f, 1f);           // her body lacks it: flags 0
        Bone(s + 112, 3, 1, float.NaN, 0f, 0f, 0f, 0f, 0f, 1f);   // "valid", but not a number
        Put(256 + 128 * 1 + 124, (uint)off);
        uint flags = FrameRing.FlagLinearDepth | FrameRing.FlagNoPlane3 | FrameRing.FlagBones;
        Publish(1, counter: 4, host: 4, w: w, h: h, flags: flags);
        // The ring is opened afresh for each case: Windows refuses to rewrite a file while it is
        // mapped, so a case is published with no reader holding it.
        bool HasBonesNow()
        {
            using var r = FrameRing.Open(path);
            long fresh = 0;
            Assert.True(r.TryLatestOf(0, ref fresh, out var g));
            return g.HasBones;
        }
        using (var ring = FrameRing.Open(path))
        {
            long seen = 0;
            Assert.True(ring.TryLatestOf(0, ref seen, out var f));
            Assert.True(f.HasBones);
            Assert.Equal((17u, 3), (f.Bones.Rev, f.Bones.Count));
            Assert.Equal(new Vec3(100.5, -2, 7.25), f.Bones.Origin);
            Assert.True(f.Bones.TryFind(0, out var hand));
            Assert.True(hand.Valid);
            Assert.Equal(new Vec3(100.75, -0.5, 6.75), hand.Position);
            Assert.Equal((0f, 0.7071f, 0f, 0.7071f), (hand.X, hand.Y, hand.Z, hand.W));
            Assert.True(f.Bones.TryFind(2, out var lacked) && !lacked.Valid);
            Assert.True(f.Bones.TryFind(3, out var nan) && !nan.Valid);
            Assert.False(f.Bones.TryFind(1, out _), "a name the engine did not know leaves a gap");
            Assert.Throws<ArgumentOutOfRangeException>(() => f.Bones[3]);
            Assert.True(ring.StillValid(ref f));
        }

        Put(s + 4, 9u); // more than 8 entries
        Publish(1, counter: 5, host: 5, w: w, h: h, flags: flags);
        Assert.False(HasBonesNow());

        Put(s + 4, 3u);
        long late = Stride - 48 - 32; // a whole header and ONE entry fit there; the second and third run past the slot
        Put(data + late, 0x454E4F42u);
        Put(data + late + 4, 3u);
        Put(256 + 128 * 1 + 124, (uint)late);
        Publish(1, counter: 6, host: 6, w: w, h: h, flags: flags);
        Assert.False(HasBonesNow());

        Put(256 + 128 * 1 + 124, (uint)off);
        Put(s, 0x54534143u); // another section's magic
        Publish(1, counter: 7, host: 7, w: w, h: h, flags: flags);
        Assert.False(HasBonesNow());

        Put(s, 0x454E4F42u);
        Publish(1, counter: 8, host: 8, w: w, h: h); // no flag 256
        Assert.False(HasBonesNow());

        Publish(1, counter: 9, host: 9, w: w, h: h, flags: flags); // the control: the same section, whole again
        Assert.True(HasBonesNow());
    }

    [Theory]
    [InlineData(0, 0x12345678u)] // magic
    [InlineData(4, 2u)] // version
    [InlineData(12, 99u)] // slots
    [InlineData(24, 4096u)] // max width past the stride
    public void a_ring_with_a_lying_header_is_refused(long at, uint value)
    {
        Put(at, value);
        Flush();
        Assert.Throws<IOException>(() => FrameRing.Open(path));
    }

    [Fact]
    public void a_missing_ring_is_an_io_error()
    {
        Assert.ThrowsAny<IOException>(() => FrameRing.Open(path + "-missing"));
    }
}
