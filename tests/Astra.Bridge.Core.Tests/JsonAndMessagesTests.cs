// SPDX-License-Identifier: MIT
using System;
using System.Globalization;
using System.Threading;
using Astra.Bridge;
using Xunit;

public class JsonAndMessagesTests
{
    /// <summary>A game under a comma-decimal locale must still send "1.5".</summary>
    [Fact]
    public void numbers_are_invariant_under_a_comma_decimal_locale()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            Assert.Equal("1,5", 1.5.ToString()); // the hazard is real
            var json = Messages.Cam(7, new Vec3(1.5, 2, -3.25), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 45.5, 1920, 1080);
            Assert.Equal("{\"t\":\"cam\",\"id\":7,\"pos\":[1.5,2,-3.25],\"fwd\":[0,0,-1],\"up\":[0,1,0],\"fovY\":45.5,\"w\":1920,\"h\":1080}", json);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void strings_are_escaped()
    {
        var json = Messages.Hello("my \"mod\"\\\n", null);
        Assert.Equal("{\"t\":\"hello\",\"v\":1,\"client\":\"my \\\"mod\\\"\\\\\\n\"}", json);
        Assert.Equal("my \"mod\"\\\n", JsonReader.ReadObject(json)["client"]);
    }

    [Fact]
    public void a_camera_the_engine_would_refuse_is_never_built()
    {
        var up = new Vec3(0, 1, 0);
        var fwd = new Vec3(0, 0, -1);
        Assert.Null(Messages.Cam(1, new Vec3(double.NaN, 0, 0), fwd, up, 60, 100, 100));
        Assert.Null(Messages.Cam(1, Vec3.Zero, Vec3.Zero, up, 60, 100, 100));
        Assert.Null(Messages.Cam(1, Vec3.Zero, fwd, up, 0.5, 100, 100));
        Assert.Null(Messages.Cam(1, Vec3.Zero, fwd, up, 60, 0, 100));
        Assert.Null(Messages.Cam(1, Vec3.Zero, fwd, up, 60, 9000, 100));
        Assert.Null(Messages.Cam(1, Vec3.Zero, fwd, up, 60, 100, 100, new[] { double.PositiveInfinity }));
        Assert.Contains("fovY", Messages.Cam(1, Vec3.Zero, fwd, up, 200, 100, 100) ?? Messages.Rejection);
        Assert.NotNull(Messages.Cam(1, Vec3.Zero, fwd, up, 60, 100, 100, new double[] { 1, 2, 3, 4 }));
    }

    [Fact]
    public void the_echo_keeps_at_most_three_numbers()
    {
        var json = Messages.Cam(1, Vec3.Zero, new Vec3(0, 0, -1), new Vec3(0, 1, 0), 60, 10, 10, new double[] { 1, 2, 3, 4 });
        Assert.EndsWith("\"echo\":[1,2,3]}", json);
    }

    [Fact]
    public void an_avatar_carries_only_what_was_given()
    {
        var p = new Placement { Pos = new Vec3(1, 0, 2), Fwd = new Vec3(0, 0, 1) };
        Assert.Equal("{\"t\":\"avatar\",\"id\":0,\"pos\":[1,0,2],\"fwd\":[0,0,1]}", Messages.Avatar(p));

        var ps = new ParamSet().Set("speed", 1.25).Set("airborne", false).Unset("altitude");
        p.Vel = new Vec3(0.5, 0, 0);
        p.Anchor = "hips";
        p.Scale = 1.2;
        Assert.Equal(
            "{\"t\":\"avatar\",\"id\":0,\"pos\":[1,0,2],\"fwd\":[0,0,1],\"vel\":[0.5,0,0],\"anchor\":\"hips\",\"scale\":1.2," +
            "\"params\":{\"speed\":1.25,\"airborne\":false,\"altitude\":null}}",
            Messages.Avatar(p, ps));

        ps.Set("speed", double.NaN);
        Assert.Null(Messages.Avatar(p, ps));
        p.Scale = 30;
        Assert.Null(Messages.Avatar(p));
    }

    [Fact]
    public void a_parameter_set_again_replaces_its_value_in_place()
    {
        var ps = new ParamSet().Set("speed", 1).Set("airborne", true).Set("speed", 2);
        Assert.Equal("{\"t\":\"param\",\"set\":{\"speed\":2,\"airborne\":true}}", Messages.Param(ps));
    }

    [Fact]
    public void light_clamps_and_can_hand_her_own_light_back()
    {
        var sun = new Sun { Dir = new Vec3(0, 1, 0), Color = new Vec3(1, 0.9, 0.8), Intensity = 40, Visible = 2 };
        Assert.Equal(
            "{\"t\":\"light\",\"sun\":{\"dir\":[0,1,0],\"color\":[1,0.9,0.8],\"intensity\":16,\"visible\":1},\"ambient\":[0.2,0.2,0.3]}",
            Messages.Light(sun, new Vec3(0.2, 0.2, 0.3)));
        Assert.Equal("{\"t\":\"light\"}", Messages.Light(null, null));
    }

    [Fact]
    public void the_hello_reply_is_read_and_nested_values_are_skipped()
    {
        var m = JsonReader.ReadObject(
            "{\"t\":\"hello\",\"v\":1,\"engine\":\"astra-avatar-engine\",\"extra\":{\"a\":[1,{\"b\":null}],\"c\":\"}\"}," +
            "\"ver\":\"0.1.45\",\"shm\":\"/dev/shm/astra-frame\",\"maxW\":1920,\"maxH\":1080}");
        var h = HelloReply.From(m);
        Assert.NotNull(h);
        Assert.Equal(1, h.Version);
        Assert.Equal("0.1.45", h.EngineVersion);
        Assert.Equal("/dev/shm/astra-frame", h.Shm);
        Assert.Equal(1920, h.MaxWidth);
        Assert.Equal(1080, h.MaxHeight);
        Assert.False(m.ContainsKey("extra"));
        Assert.Null(HelloReply.From(JsonReader.ReadObject("{\"t\":\"error\",\"msg\":\"no\"}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[1]")]
    [InlineData("{\"a\":1")]
    [InlineData("{\"a\":1} x")]
    [InlineData("{\"a\":tru}")]
    public void malformed_json_is_a_format_error(string text)
    {
        Assert.Throws<FormatException>(() => JsonReader.ReadObject(text));
    }
}
