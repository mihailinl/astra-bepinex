// SPDX-License-Identifier: MIT
using System;
using Astra.Bridge;
using Xunit;

/// <summary>
/// <see cref="RecentById{T}"/>, as the Unity foundation's camera feed uses it: one camera put per game
/// frame, and the engine's picture coming back for a camera sent a moment ago.
/// </summary>
public class RecentByIdTests
{
    /// <summary>The foundation's own sizes (<c>Astra.Unity.CameraFeed</c>).</summary>
    const int Capacity = 1024;
    const double MaxAge = 1.0;

    /// <summary>
    /// Run a game at <paramref name="fps"/> for three seconds, sending one camera a frame, and ask each
    /// frame for the camera sent <paramref name="latency"/> seconds earlier, as the compositor asks for
    /// the camera a picture was drawn for. Returns how many of those asks found it.
    /// </summary>
    static (int asked, int found) Play(RecentById<long> sent, int fps, double latency)
    {
        double dt = 1.0 / fps;
        int behind = (int)Math.Round(latency / dt);
        int asked = 0, found = 0;
        for (long frame = 1; frame <= 3 * fps; frame++)
        {
            double now = frame * dt;
            sent.Put(frame, now, frame);
            long pictureFor = frame - behind;
            if (pictureFor < 1) continue;
            asked++;
            if (sent.Find(pictureFor, now, out long got) && got == pictureFor) found++;
        }
        return (asked, found);
    }

    /// <summary>The engine draws at about 60 a second and reads the picture back from the GPU, so a
    /// picture is a fraction of a second late. Whatever the game's frame rate, every one is found.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    [InlineData(500)]
    [InlineData(1000)]
    public void A_picture_a_quarter_second_late_finds_its_camera_at_any_frame_rate(int fps)
    {
        var (asked, found) = Play(new RecentById<long>(Capacity, MaxAge), fps, latency: 0.25);
        Assert.True(asked > 0);
        Assert.Equal(asked, found);
    }

    /// <summary>
    /// Why it is an age: the 64-camera history of 0.8.1, at the frame rate of a game left uncapped,
    /// found none of them (Valheim at several hundred FPS on Windows, 2026-11-16: she was not drawn
    /// while the game had the focus). At 60 FPS the same history found them all, which is how it
    /// passed every test before.
    /// </summary>
    [Fact]
    public void A_history_of_64_cameras_loses_every_picture_at_500_fps()
    {
        var at60 = Play(new RecentById<long>(64, double.MaxValue), 60, latency: 0.25);
        Assert.Equal(at60.asked, at60.found);

        var at500 = Play(new RecentById<long>(64, double.MaxValue), 500, latency: 0.25);
        Assert.True(at500.asked > 0);
        Assert.Equal(0, at500.found);
    }

    [Fact]
    public void An_entry_is_found_for_its_max_age_and_not_after()
    {
        var sent = new RecentById<string>(Capacity, MaxAge);
        sent.Put(7, 10.0, "seven");
        Assert.True(sent.Find(7, 10.0, out var v) && v == "seven");
        Assert.True(sent.Find(7, 11.0, out _));
        Assert.False(sent.Find(7, 11.001, out v));
        Assert.Null(v);
    }

    /// <summary>More entries within the age than it holds: the oldest go first, a newer one never.</summary>
    [Fact]
    public void Past_its_capacity_the_oldest_entries_go_first()
    {
        var sent = new RecentById<long>(Capacity, MaxAge);
        const int fps = 2000; // 1024 slots are 0.512 s here, inside the one-second age
        double dt = 1.0 / fps;
        long last = fps; // one second of frames
        for (long id = 1; id <= last; id++) sent.Put(id, id * dt, id);
        double now = last * dt;
        Assert.True(sent.Find(last - Capacity + 1, now, out _));
        Assert.False(sent.Find(last - Capacity, now, out _));
    }

    [Fact]
    public void Id_zero_an_unknown_id_and_a_negative_id_are_not_found()
    {
        var sent = new RecentById<long>(8, MaxAge);
        Assert.False(sent.Find(0, 0, out _));
        sent.Put(0, 0, 42);
        Assert.False(sent.Find(0, 0, out _));
        Assert.False(sent.Find(3, 0, out _));
        Assert.False(sent.Find(-3, 0, out _));
        sent.Put(-3, 0, 9);
        Assert.True(sent.Find(-3, 0, out long got) && got == 9);
    }

    [Fact]
    public void A_capacity_below_one_or_an_age_not_above_zero_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentById<int>(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentById<int>(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecentById<int>(1, double.NaN));
    }
}
