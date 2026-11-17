// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Astra.Bridge;
using Xunit;

/// <summary>
/// <see cref="GameLink"/>, the integration's side of Astra Link as the SDK hands it out: it works the
/// same with and without a connection, keeps what was set before one for when it comes, and closes
/// the connection it was given. Against the same mock of the daemon's socket as <see cref="LinkClientTests"/>.
/// </summary>
public class GameLinkTests : IDisposable
{
    const string Id = "knlce.test";
    const string Token = "3f1a9c2e7b6d4805a1c9e2f6b7d84e1a";

    readonly string dir = Path.Combine(Path.GetTempPath(), "astra-gamelink-" + Guid.NewGuid().ToString("N"));
    readonly LinkClientTests.Mock mock = new LinkClientTests.Mock();
    readonly List<string> warnings = new List<string>();
    readonly List<LinkClient> clients = new List<LinkClient>();
    double now = 1000;

    public GameLinkTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        foreach (var c in clients) c.Dispose();
        mock.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static readonly LinkClient.Timings Fast = new LinkClient.Timings
    {
        RetryMinMs = 20, RetryMaxMs = 200, WelcomeWithinMs = 1500, KeeperMs = 20, ConnectTimeoutMs = 1000,
    };

    GameLink Link()
    {
        var l = new GameLink();
        l.Warn = w => warnings.Add(w);
        return l;
    }

    LinkClient Client()
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir, LinkFile.FileName),
            "{\"schema\":1,\"url\":\"ws://127.0.0.1:" + mock.Port + "/link\",\"token\":\"" + Token + "\",\"integration\":\"" + Id + "\"," +
            "\"game\":\"892970\",\"issued_unix_ms\":" + ms + ",\"expires_unix_ms\":" + (ms + 600_000) + "}");
        var c = LinkClient.TryCreate(dir, Id, "tests/1.0", out var why, Fast, () => now, env: _ => null);
        Assert.True(c != null, why);
        clients.Add(c);
        return c;
    }

    static void PumpUntil(GameLink l, Func<bool> until, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!until())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("timed out waiting for " + what);
            l.Pump();
            Thread.Sleep(5);
        }
    }

    /// <summary>Accept the link's connection, take its hello, answer <paramref name="welcome"/>.</summary>
    async Task<LinkClientTests.Peer> Session(GameLink l, string welcome)
    {
        var peer = await mock.Accept();
        Assert.NotNull(peer);
        var hello = await peer.Next();
        Assert.Equal("hello", hello["t"]);
        Assert.Equal(Token, hello["token"]);
        await peer.Send(welcome);
        PumpUntil(l, () => l.Live, "the session to go live");
        return peer;
    }

    [Fact]
    public void Without_a_connection_everything_works_and_does_nothing()
    {
        var l = Link();
        int calls = 0;
        Assert.Same(l, l.Tool("come_here", _ => { calls++; return LinkResult.Done(); }));
        Assert.Throws<ArgumentException>(() => l.Tool("Come Here", _ => LinkResult.Done()));
        Assert.Throws<ArgumentNullException>(() => l.Tool("stay", null));
        Assert.Throws<ArgumentNullException>(() => l.Context(null));
        Assert.False(l.Live);
        Assert.Same(LinkGrants.None, l.Grants);
        Assert.False(l.Event("player_fell"));
        Assert.False(l.Trigger("rested"));
        Assert.False(l.Talk());
        Assert.True(l.Context(new LinkData().Set("night", true))); // kept for a session that may come
        l.Quiet(true);
        l.Pump(); // nothing attached: nothing happens
        Assert.Equal(0, calls);
        Assert.Empty(warnings);
        // A word that is not v1 is said once, whatever the connection
        Assert.False(l.Event("boss_killed"));
        Assert.False(l.Event("boss_killed"));
        Assert.Single(warnings);
        Assert.Contains("boss_killed", warnings[0]);
    }

    [Fact]
    public void A_context_larger_than_a_context_may_be_is_refused_at_once()
    {
        var l = Link();
        var big = new LinkData();
        for (int i = 0; i < 200; i++) big.Set("fact_" + i, 123456.789);
        Assert.False(l.Context(big));
        Assert.False(l.Context(big));
        Assert.Single(warnings);
        Assert.True(l.Context(new LinkData().Set("night", true)));
    }

    [Fact]
    public async Task What_was_set_before_the_connection_is_said_once_it_is_live()
    {
        var l = Link();
        string target = null;
        int ranOn = -1;
        l.Tool("come_here", a =>
        {
            ranOn = Thread.CurrentThread.ManagedThreadId;
            target = a.Str("target");
            return LinkResult.Done(new LinkData().Set("arrived", true).Set("distance_m", 2.5));
        });
        var context = new LinkData().Set("biome", "meadows");
        l.Context(context);
        context.Set("biome", "swamp"); // the link keeps its own copy
        l.Quiet(true);
        l.Attach(Client());
        Assert.False(l.Live);
        var peer = await Session(l, LinkClientTests.Welcome("r1",
            tools: "[\"game_0123abcd_come_here\",\"game_0123abcd_map_pin_add\"]", context: true, speaks: true));
        // The standing context and quiet, in whichever order they leave (the frame after the welcome)
        var said = new Dictionary<string, Dictionary<string, object>>();
        var standing = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var m = await peer.Next();
                said[(string)m["t"]] = m;
            }
        });
        PumpUntil(l, () => standing.IsCompleted, "the standing context and quiet");
        await standing;
        Assert.Equal("meadows", ((Dictionary<string, object>)said["context"]["data"])["biome"]);
        Assert.Equal(true, said["quiet"]["on"]);

        // A tool registered after the connection answers too
        l.Tool("map_pin_add", _ => LinkResult.Refuse(LinkRefusal.NotInWorld));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_come_here\",\"args\":{\"target\":\"player\"}}");
        await peer.Send("{\"t\":\"call\",\"id\":\"c2\",\"tool\":\"game_0123abcd_map_pin_add\",\"args\":{}}");
        var results = new Dictionary<string, Dictionary<string, object>>();
        var reading = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var r = await peer.Next();
                results[(string)r["id"]] = r;
            }
        });
        int pumpedOn = Thread.CurrentThread.ManagedThreadId;
        PumpUntil(l, () => reading.IsCompleted, "two results");
        await reading;
        Assert.Equal(pumpedOn, ranOn);
        Assert.Equal("player", target);
        Assert.True((bool)results["c1"]["ok"]);
        Assert.Equal(2.5, ((Dictionary<string, object>)results["c1"]["data"])["distance_m"]);
        Assert.Equal("not_in_world", results["c2"]["error"]);
        l.Detach();
    }

    [Fact]
    public async Task Grants_and_the_end_reach_the_integration_through_its_link()
    {
        var l = Link();
        var grants = new List<LinkGrants>();
        string ended = null;
        l.GrantsChanged += g => grants.Add(g);
        l.Ended += r => ended = r;
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", events: "[\"player_fell\"]"));
        PumpUntil(l, () => grants.Count >= 1, "the first GrantsChanged");
        Assert.True(grants[0].HasEvent("player_fell"));
        Assert.True(l.Grants.HasEvent("player_fell"));
        Assert.True(l.Event("player_fell", new LinkData().Set("height_m", 6)));
        var ev = await peer.Next();
        Assert.Equal(("event", "player_fell"), ((string)ev["t"], (string)ev["name"]));

        // The user switched "Tell Astra what happens" off on the game's row
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"events\":[]}}");
        PumpUntil(l, () => grants.Count >= 2, "the second GrantsChanged");
        Assert.False(grants[1].HasEvent("player_fell"));
        Assert.False(l.Event("player_fell"));

        await peer.Send("{\"t\":\"end\",\"reason\":\"withdrawn\"}");
        PumpUntil(l, () => ended != null, "Ended");
        Assert.Equal("withdrawn", ended);
        Assert.False(l.Live);
        Assert.Same(LinkGrants.None, l.Grants);
    }

    [Fact]
    public async Task Detach_closes_the_connection_and_nothing_reconnects()
    {
        var l = Link();
        var c = Client();
        l.Attach(c);
        await Session(l, LinkClientTests.Welcome("r1", events: "[\"player_fell\"]"));
        l.Detach();
        Assert.True(c.Over);
        Assert.False(l.Live);
        Assert.False(l.Event("player_fell"));
        Assert.Null(await mock.Accept(300));
        l.Detach(); // a second time is nothing
    }

    [Fact]
    public void A_second_attach_closes_the_first_connection()
    {
        var l = Link();
        var first = Client();
        l.Attach(first);
        var second = Client();
        l.Attach(second);
        Assert.True(first.Over);
        Assert.False(second.Over);
        l.Detach();
        Assert.True(second.Over);
    }
}
