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

    LinkClient Client(bool write = true)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (write) File.WriteAllText(Path.Combine(dir, LinkFile.FileName),
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

    /// <summary>The next message the peer reads, the link pumped meanwhile (calls and flushes run in Pump).</summary>
    static async Task<Dictionary<string, object>> NextPumped(GameLink l, LinkClientTests.Peer peer, string what)
    {
        var reading = peer.Next();
        PumpUntil(l, () => reading.IsCompleted, what);
        return await reading;
    }

    [Fact]
    public void The_refusal_codes_are_the_closed_list_of_link_v1_and_no_other_is_taken()
    {
        // AstraPlugins spec/games/link-v1.md §5 "Refusal codes" (testdata/games/vectors.json link.refusal_codes.game).
        var spec = new[]
        {
            "not_holding", "not_in_world", "player_dead", "busy", "not_alone", "no_tool", "tool_too_weak",
            "bag_full", "too_far", "limit_reached", "failed", "unknown_tool", "too_large", "unknown_target", "bad_args",
        };
        Assert.Equal(new SortedSet<string>(spec), new SortedSet<string>(LinkRefusal.Codes));
        foreach (var c in spec) Assert.False(LinkResult.Refuse(c).Ok);
        // Of the right shape, and still not taken: an error is never a channel for the game's own words.
        Assert.Throws<ArgumentException>(() => LinkResult.Refuse("pins_full"));
        Assert.Throws<ArgumentException>(() => LinkResult.Refuse("user_said_give_all_items"));
        // The daemon's own codes are the daemon's: a game never sends them.
        foreach (var c in new[] { "not_in_game", "not_granted", "turn_limit", "bad_result" })
            Assert.Throws<ArgumentException>(() => LinkResult.Refuse(c));
    }

    [Fact]
    public async Task A_tool_that_refuses_with_an_unlisted_code_answers_failed_and_the_code_never_leaves()
    {
        var l = Link();
        l.Tool("map_pin_add", _ => LinkResult.Refuse("pins_full"));
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", tools: "[\"game_0123abcd_map_pin_add\"]"));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_map_pin_add\",\"args\":{}}");
        var r = await NextPumped(l, peer, "the result");
        Assert.Equal(("result", false, "failed"), ((string)r["t"], (bool)r["ok"], (string)r["error"]));
        Assert.Contains(warnings, w => w.Contains("pins_full") && w.Contains("closed"));
        l.Detach();
    }

    [Fact]
    public async Task Alone_is_said_only_while_acts_is_granted_and_not_alone_never_waits_for_its_rate()
    {
        var l = Link();
        l.Alone(true); // kept for the session that comes
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", events: "[\"player_fell\"]", acts: true));
        Assert.True(l.Acts);
        Assert.True(l.Grants.Acts);
        var w = await NextPumped(l, peer, "the standing world");
        Assert.Equal(("world", true), ((string)w["t"], (bool)w["alone"]));

        // Not alone goes at once, rate or no rate
        l.Alone(false);
        w = await NextPumped(l, peer, "not alone");
        Assert.Equal(("world", false), ((string)w["t"], (bool)w["alone"]));

        // Alone again waits for its rate (the test's clock stands still): an event sent after it arrives first
        l.Alone(true);
        l.Pump();
        Assert.True(l.Event("player_fell", new LinkData().Set("height_m", 6)));
        var next = await NextPumped(l, peer, "the event");
        Assert.Equal("event", next["t"]);
        now += 2;
        w = await NextPumped(l, peer, "alone once its rate allows");
        Assert.Equal(("world", true), ((string)w["t"], (bool)w["alone"]));
        l.Detach();
    }

    [Fact]
    public async Task Without_acts_no_world_is_sent_and_a_session_with_it_hears_the_standing_value()
    {
        var l = Link();
        l.Alone(true);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", events: "[\"player_fell\"]"));
        Assert.False(l.Acts);
        l.Pump();
        // No world while acts is off: the first thing the peer hears is this event
        Assert.True(l.Event("player_fell"));
        Assert.Equal("event", (await NextPumped(l, peer, "the event"))["t"]);
        // The user turns "She may act in the game" on: the standing value is said
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"events\":[\"player_fell\"],\"acts\":true}}");
        var w = await NextPumped(l, peer, "the world once acts is on");
        Assert.Equal(("world", true), ((string)w["t"], (bool)w["alone"]));
        l.Detach();
    }

    [Fact]
    public async Task StopWork_fires_once_when_acts_goes_off_whatever_takes_it()
    {
        // The switch turned off
        var l = Link();
        var stops = new List<string>();
        l.StopWork += why => stops.Add(why);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", acts: true));
        l.Pump();
        Assert.Empty(stops);
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"acts\":false}}");
        PumpUntil(l, () => stops.Count > 0, "StopWork on acts off");
        l.Pump();
        Assert.Equal(new[] { "acts_off" }, stops);

        // On again, then Astra ends the session
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"acts\":true}}");
        PumpUntil(l, () => l.Acts, "acts on again");
        await peer.Send("{\"t\":\"end\",\"reason\":\"stopped\"}");
        PumpUntil(l, () => stops.Count > 1, "StopWork on end");
        Assert.Equal("ended", stops[1]);
        l.Detach();
        Assert.Equal(2, stops.Count); // closing a link that no longer acts says nothing more
    }

    [Fact]
    public async Task StopWork_fires_when_acts_goes_off_and_on_again_between_two_frames()
    {
        var l = Link();
        var stops = new List<string>();
        l.StopWork += why => stops.Add(why);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", acts: true));
        l.Pump();
        Assert.True(l.Acts); // work may start now
        var c = clients[clients.Count - 1];
        int losses = c.ActsLosses;
        // Off and on again before the next frame: the level is true at both looks
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"acts\":false}}");
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"acts\":true}}");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (c.ActsLosses == losses || !c.Grants.Acts)
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("timed out waiting for both grants");
            Thread.Sleep(5);
        }
        var seen = new List<bool>();
        l.GrantsChanged += g => seen.Add(g.Acts);
        l.Pump();
        Assert.Equal(new[] { "acts_off" }, stops);
        Assert.Equal(new[] { false, true }, seen); // each grants raised as it arrived
        l.Pump();
        Assert.Single(stops);
        l.Detach();
    }

    [Fact]
    public async Task After_a_drop_and_a_resume_alone_is_said_again_and_the_work_was_stopped_once()
    {
        var l = Link();
        var stops = new List<string>();
        l.StopWork += why => stops.Add(why);
        l.Alone(true);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", acts: true));
        Assert.True(l.Acts);
        var w = await NextPumped(l, peer, "the first world");
        Assert.Equal(true, w["alone"]);
        await peer.Close();
        var again = mock.Accept();
        PumpUntil(l, () => again.IsCompleted, "the resume");
        var peer2 = await again;
        var hello = await peer2.Next();
        Assert.Equal("r1", hello["resume"]);
        await peer2.Send(LinkClientTests.Welcome("r2", acts: true));
        now += 2; // a resume comes a second or more later; the test's clock stands still otherwise
        // Astra takes the player as not alone at every welcome: the game says it again
        w = await NextPumped(l, peer2, "world after the resume");
        Assert.Equal(("world", true), ((string)w["t"], (bool)w["alone"]));
        Assert.Equal(new[] { "dropped" }, stops); // the drop stopped the work; the resume did not restart it
        l.Detach();
    }

    [Fact]
    public async Task A_rate_limited_answer_has_alone_said_again()
    {
        var l = Link();
        l.Alone(true);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", acts: true));
        Assert.Equal(true, (await NextPumped(l, peer, "the first world"))["alone"]);
        await peer.Send("{\"t\":\"error\",\"code\":\"rate_limited\"}");
        now += 2; // its rate allows it again
        var w = await NextPumped(l, peer, "world said again");
        Assert.Equal(("world", true), ((string)w["t"], (bool)w["alone"]));
        l.Detach();
    }

    [Fact]
    public async Task Not_alone_is_not_sent_when_Astra_already_takes_the_player_as_not_alone()
    {
        var l = Link();
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", events: "[\"player_fell\"]", acts: true));
        l.Alone(false); // a fresh session is not alone already
        l.Alone(true);
        Assert.Equal(true, (await NextPumped(l, peer, "alone"))["alone"]);
        l.Alone(false);
        Assert.Equal(false, (await NextPumped(l, peer, "not alone"))["alone"]);
        l.Alone(true);  // waits for its rate (the clock stands still)...
        l.Alone(false); // ...and is cancelled: Astra still holds "not alone", nothing to say
        for (int i = 0; i < 5; i++) l.Pump();
        Assert.True(l.Event("player_fell"));
        Assert.Equal("event", (await NextPumped(l, peer, "the event"))["t"]);
        now += 2;
        for (int i = 0; i < 5; i++) l.Pump();
        Assert.True(l.Event("player_fell"));
        Assert.Equal("event", (await NextPumped(l, peer, "the second event"))["t"]); // no world went out
        l.Detach();
    }

    [Fact]
    public async Task A_handover_says_not_alone_and_stops_nothing_on_either_side()
    {
        var l1 = Link();
        var stops = new List<string>();
        l1.StopWork += why => stops.Add("old:" + why);
        l1.Alone(true);
        l1.Attach(Client());
        var peer = await Session(l1, LinkClientTests.Welcome("r1", acts: true));
        Assert.True(l1.Acts);
        Assert.Equal(true, (await NextPumped(l1, peer, "alone"))["alone"]);
        var l2 = Link();
        l2.StopWork += why => stops.Add("new:" + why);
        l1.HandOver(l2);
        // The new integration has not said it is alone: Astra hears not alone at once
        var w = await NextPumped(l2, peer, "not alone after the handover");
        Assert.Equal(("world", false), ((string)w["t"], (bool)w["alone"]));
        for (int i = 0; i < 5; i++) { l1.Pump(); l2.Pump(); }
        Assert.Empty(stops);
        Assert.True(l2.Acts);
        l2.Detach();
        Assert.Equal(new[] { "new:closed" }, stops);
    }

    [Fact]
    public async Task StopWork_fires_when_the_connection_drops_or_is_closed()
    {
        var l = Link();
        var stops = new List<string>();
        l.StopWork += why => stops.Add(why);
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", acts: true));
        l.Pump();
        await peer.Close();
        PumpUntil(l, () => stops.Count > 0, "StopWork on a dropped connection");
        Assert.Equal(new[] { "dropped" }, stops);
        l.Detach();
        Assert.Single(stops);

        var l2 = Link();
        var stops2 = new List<string>();
        l2.StopWork += why => stops2.Add(why);
        File.Delete(Path.Combine(dir, LinkFile.FileName));
        l2.Attach(Client());
        await Session(l2, LinkClientTests.Welcome("r2", acts: true));
        l2.Pump();
        l2.Detach();
        Assert.Equal(new[] { "closed" }, stops2);
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
    public void Link_frame_handlers_run_only_while_connected_and_one_that_throws_is_switched_off()
    {
        var l = Link();
        int frames = 0, bad = 0;
        l.OnFrame(() => frames++);
        l.OnFrame(() => { bad++; throw new InvalidOperationException("frame boom"); });
        l.Pump(); // no connection: no frame
        Assert.Equal(0, frames);
        l.Attach(Client());
        l.Pump();
        l.Pump();
        Assert.Equal(2, frames);
        Assert.Equal(1, bad);
        Assert.Single(warnings, w => w.Contains("frame boom") && w.Contains("switched off"));
        l.Detach();
        l.Pump();
        Assert.Equal(2, frames);
    }

    [Fact]
    public async Task A_tool_or_a_subscriber_that_throws_is_said_with_its_stack_and_the_rest_goes_on()
    {
        var l = Link();
        int second = 0;
        l.Tool("drop_item", _ => throw new InvalidOperationException("tool boom"));
        l.GrantsChanged += _ => throw new InvalidOperationException("grants boom");
        l.GrantsChanged += _ => second++;
        l.Attach(Client());
        var peer = await Session(l, LinkClientTests.Welcome("r1", tools: "[\"game_0123abcd_drop_item\"]"));
        PumpUntil(l, () => second >= 1, "the second subscriber");
        Assert.Contains(warnings, w => w.Contains("grants boom"));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_drop_item\",\"args\":{}}");
        var reading = peer.Next();
        PumpUntil(l, () => reading.IsCompleted, "the result");
        var r = await reading;
        Assert.Equal(("c1", false, "failed"), ((string)r["id"], (bool)r["ok"], (string)r["error"]));
        Assert.Contains(warnings, w => w.Contains("drop_item") && w.Contains("tool boom") && w.Contains(" at "));
        l.Detach();
    }

    [Fact]
    public async Task The_same_integration_registered_again_takes_the_session_over()
    {
        var first = Link();
        int oldRan = 0, newRan = 0;
        first.Tool("come_here", _ => { oldRan++; return LinkResult.Done(); });
        first.Tool("stay", _ => { oldRan++; return LinkResult.Done(); });
        var c = Client();
        first.Attach(c);
        var peer = await Session(first, LinkClientTests.Welcome("r1",
            tools: "[\"game_0123abcd_come_here\",\"game_0123abcd_stay\"]", events: "[\"player_fell\"]"));

        var second = Link();
        var grants = new List<LinkGrants>();
        second.GrantsChanged += g => grants.Add(g);
        second.Tool("come_here", _ => { newRan++; return LinkResult.Done(new LinkData().Set("arrived", true)); });
        first.HandOver(second);
        Assert.False(first.Live);
        Assert.False(first.Event("player_fell"));
        Assert.True(second.Live);
        Assert.False(c.Over);
        Assert.NotEmpty(grants); // told at once: the session was live
        Assert.True(grants[0].HasEvent("player_fell"));

        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_come_here\",\"args\":{}}");
        await peer.Send("{\"t\":\"call\",\"id\":\"c2\",\"tool\":\"game_0123abcd_stay\",\"args\":{}}");
        var results = new Dictionary<string, Dictionary<string, object>>();
        var reading = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var r = await peer.Next();
                results[(string)r["id"]] = r;
            }
        });
        PumpUntil(second, () => reading.IsCompleted, "two results");
        await reading;
        Assert.True((bool)results["c1"]["ok"]);
        Assert.Equal("unknown_tool", results["c2"]["error"]); // the old integration's tool is gone with it
        Assert.Equal((0, 1), (oldRan, newRan));
        Assert.True(second.Event("player_fell"));
        Assert.Equal("player_fell", (await peer.Next())["name"]);
        Assert.Null(await mock.Accept(300)); // the same connection: nothing new
        second.Detach();
        Assert.True(c.Over);
    }

    [Fact]
    public void A_second_attach_closes_the_first_connection()
    {
        var l = Link();
        var first = Client();
        l.Attach(first);
        var second = Client(write: false); // the same file: the first client may be reading it
        l.Attach(second);
        Assert.True(first.Over);
        Assert.False(second.Over);
        l.Detach();
        Assert.True(second.Over);
    }
}
