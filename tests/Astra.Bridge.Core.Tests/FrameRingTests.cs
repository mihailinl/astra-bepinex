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
    void Publish(int slot, long counter, long host, int w, int h, long seq = 2, uint flags = FrameRing.FlagLinearDepth | FrameRing.FlagNoPlane3)
    {
        long d = 256 + 128 * slot;
        Put(d, seq);
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
