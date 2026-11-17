// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astra.Bridge;
using Xunit;

/// <summary>
/// <see cref="LinkClient"/> against a mock of the daemon's Link socket (link-v1.md §2–§5), served by
/// .NET's own WebSocket server — which also holds the client's frames and handshake to RFC 6455, and
/// refuses a client that does not ask for the <c>astra-link.1</c> subprotocol.
/// </summary>
public class LinkClientTests : IDisposable
{
    const string Id = "knlce.test";
    const string Token = "3f1a9c2e7b6d4805a1c9e2f6b7d84e1a";

    readonly string dir = Path.Combine(Path.GetTempPath(), "astra-link-" + Guid.NewGuid().ToString("N"));
    readonly Mock mock = new Mock();
    readonly List<string> log = new List<string>();
    double now = 1000; // the client's rate clock, moved by hand

    public LinkClientTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        mock.Dispose();
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static readonly LinkClient.Timings Fast = new LinkClient.Timings
    {
        RetryMinMs = 20, RetryMaxMs = 200, WelcomeWithinMs = 1500, KeeperMs = 20, ConnectTimeoutMs = 1000,
    };

    void WriteFile(string token = Token, long expiresIn = 600_000, int port = 0)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(dir, LinkFile.FileName),
            "{\"schema\":1,\"url\":\"ws://127.0.0.1:" + (port != 0 ? port : mock.Port) + "/link\",\"token\":\"" + token + "\",\"integration\":\"" + Id + "\"," +
            "\"game\":\"892970\",\"issued_unix_ms\":" + ms + ",\"expires_unix_ms\":" + (ms + expiresIn) + "}");
    }

    LinkClient Client(LinkClient.Timings timings = null, bool write = true)
    {
        if (write) WriteFile();
        var c = LinkClient.TryCreate(dir, Id, "tests/1.0", out var why, timings ?? Fast, () => now, env: _ => null);
        Assert.True(c != null, why);
        c.Log = line => { lock (log) log.Add(line); };
        return c;
    }

    static string B(bool b) => b ? "true" : "false";

    static string Welcome(string resume, string tools = "[]", string events = "[]", bool context = false, bool speaks = false,
        bool talk = false, string triggers = "[]") =>
        "{\"t\":\"welcome\",\"granted\":{\"tools\":" + tools + ",\"triggers\":" + triggers + ",\"events\":" + events +
        ",\"context\":" + B(context) + ",\"skill\":false,\"speaks\":" + B(speaks) + ",\"talk\":" + B(talk) + "}," +
        "\"limits\":{\"max_message_bytes\":65536,\"max_messages_per_second\":50},\"resume\":\"" + resume + "\",\"unknown_field\":1}";

    /// <summary>The thread the last <see cref="PumpUntil"/> pumped on (an async test moves between pool
    /// threads at every await: the "main thread" is whichever calls Pump).</summary>
    static int pumpThread;

    /// <summary>Pump the client on this thread until <paramref name="until"/> holds (or fail after 3 s).</summary>
    static void PumpUntil(LinkClient c, Func<bool> until, string what)
    {
        pumpThread = Thread.CurrentThread.ManagedThreadId;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!until())
        {
            if (DateTime.UtcNow > deadline) throw new Xunit.Sdk.XunitException("timed out waiting for " + what);
            c.Pump();
            Thread.Sleep(5);
        }
    }

    /// <summary>Accept the client, check its handshake and hello, answer with <paramref name="welcome"/>.</summary>
    async Task<Peer> Session(LinkClient c, string welcome, string expectToken = Token, string expectResume = null)
    {
        var peer = await mock.Accept();
        Assert.NotNull(peer);
        Assert.Null(peer.Headers["Origin"]);
        Assert.Equal("127.0.0.1:" + mock.Port, peer.Headers["Host"]);
        Assert.Equal("astra-link.1", peer.Headers["Sec-WebSocket-Protocol"]);
        var hello = await peer.Next();
        Assert.Equal("hello", hello["t"]);
        Assert.Equal(Id, hello["integration"]);
        Assert.Equal(1.0, hello["v"]);
        Assert.Equal("tests/1.0", hello["client"]);
        if (expectResume == null)
        {
            Assert.Equal(expectToken, hello["token"]);
            Assert.False(hello.ContainsKey("resume"));
        }
        else
        {
            Assert.Equal(expectResume, hello["resume"]);
            Assert.False(hello.ContainsKey("token"));
        }
        await peer.Send(welcome);
        PumpUntil(c, () => c.Live, "the session to go live");
        return peer;
    }

    [Fact]
    public void No_link_file_is_no_client_and_says_nothing()
    {
        Assert.Null(LinkClient.TryCreate(dir, Id, "tests", out var why, env: _ => null));
        Assert.Null(why);
    }

    [Fact]
    public async Task Hello_spends_the_token_and_welcome_grants_what_the_user_switched_on()
    {
        using var c = Client();
        LinkGrants seen = null;
        c.GrantsChanged += g => seen = g;
        c.Start();
        await Session(c, Welcome("r1", tools: "[\"game_0123abcd_come_here\"]", events: "[\"player_fell\"]", context: true));
        PumpUntil(c, () => seen != null, "GrantsChanged");
        Assert.True(seen.HasTool("come_here"));
        Assert.True(seen.HasEvent("player_fell"));
        Assert.False(seen.HasEvent("player_died"));
        Assert.True(seen.Context);
        Assert.False(seen.Talk);
        Assert.Equal("892970", c.Game);
    }

    [Fact]
    public async Task A_call_runs_on_the_main_thread_and_its_result_goes_back()
    {
        using var c = Client();
        int ranOn = -1;
        double? speed = null;
        c.Tool("come_here", a =>
        {
            ranOn = Thread.CurrentThread.ManagedThreadId;
            speed = a.Num("speed");
            return LinkResult.Done(new LinkData().Set("distance", 7.5).Set("arrived", false));
        });
        c.Tool("map_pin_add", _ => LinkResult.Refuse(LinkRefusal.NotInWorld));
        c.Tool("drop_item", _ => throw new InvalidOperationException("boom"));
        c.Start();
        var peer = await Session(c, Welcome("r1", tools: "[\"game_0123abcd_come_here\",\"game_0123abcd_map_pin_add\"]"));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_come_here\",\"args\":{\"speed\":2}}");
        await peer.Send("{\"t\":\"call\",\"id\":\"c2\",\"tool\":\"game_0123abcd_map_pin_add\",\"args\":{}}");
        await peer.Send("{\"t\":\"call\",\"id\":\"c3\",\"tool\":\"game_0123abcd_drop_item\"}");
        await peer.Send("{\"t\":\"call\",\"id\":\"c4\",\"tool\":\"game_0123abcd_no_such\",\"args\":{}}");
        await peer.Send("{\"t\":\"something_new\",\"x\":1}"); // an unknown kind is ignored
        var results = new Dictionary<string, Dictionary<string, object>>();
        var reading = Task.Run(async () =>
        {
            for (int i = 0; i < 4; i++)
            {
                var r = await peer.Next();
                results[(string)r["id"]] = r;
            }
        });
        PumpUntil(c, () => reading.IsCompleted, "four results");
        int pumpedOn = pumpThread;
        await reading;
        Assert.Equal(pumpedOn, ranOn);
        Assert.Equal(2.0, speed);
        Assert.True((bool)results["c1"]["ok"]);
        var data = (Dictionary<string, object>)results["c1"]["data"];
        Assert.Equal(7.5, data["distance"]);
        Assert.Equal(false, data["arrived"]);
        Assert.Equal(("not_in_world", false), ((string)results["c2"]["error"], (bool)results["c2"]["ok"]));
        Assert.Equal("failed", results["c3"]["error"]);
        Assert.Equal("unknown_tool", results["c4"]["error"]);
        lock (log) Assert.Contains(log, l => l.Contains("drop_item") && l.Contains("boom"));
    }

    [Fact]
    public async Task A_call_that_waited_past_its_timeout_is_refused_busy_without_running()
    {
        using var c = Client();
        bool ran = false;
        c.Tool("come_here", _ => { ran = true; return LinkResult.Done(); });
        c.Start();
        var peer = await Session(c, Welcome("r1", tools: "[\"game_0123abcd_come_here\"]"));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_come_here\",\"args\":{}}");
        // It arrives while the game's main thread is stalled (nothing pumps).
        var until = DateTime.UtcNow.AddSeconds(3);
        while (c.PendingCalls == 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.Equal(1, c.PendingCalls);
        now += LinkClient.CallTimeoutSeconds;
        var reading = peer.Next();
        PumpUntil(c, () => reading.IsCompleted, "the busy result");
        var r = await reading;
        Assert.Equal("busy", r["error"]);
        Assert.False(ran);
    }

    [Fact]
    public async Task Events_go_only_as_granted_v1_words_within_their_rate()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", events: "[\"player_fell\",\"goal_reached\"]"));
        Assert.False(c.Event("player_died"));      // a v1 word the user did not grant
        Assert.False(c.Event("boss_defeated"));    // not a v1 word at all
        int sent = 0;
        for (int i = 0; i < 7; i++)
            if (c.Event("player_fell", new LinkData().Set("height_m", 3.5 + i))) sent++;
        Assert.Equal(5, sent); // a burst of 5
        now += 1.2;           // just under 2 more a second: 2.16 by now
        Assert.True(c.Event("goal_reached"));
        Assert.True(c.Event("player_fell"));
        Assert.False(c.Event("player_fell"));
        var got = new List<Dictionary<string, object>>();
        for (int i = 0; i < 7; i++) got.Add(await peer.Next());
        Assert.All(got, m => Assert.Equal("event", m["t"]));
        Assert.Equal(3.5, ((Dictionary<string, object>)got[0]["data"])["height_m"]);
        Assert.Equal("goal_reached", got[5]["name"]);
        lock (log) Assert.Contains(log, l => l.Contains("boss_defeated"));
    }

    [Fact]
    public async Task The_newest_context_waits_for_its_rate_and_quiet_needs_speaks()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", context: true, speaks: false));
        Assert.True(c.Context(new LinkData().Set("biome", "meadows").Set("night", false)));
        Assert.True(c.Context(new LinkData().Set("biome", "swamp").Set("night", true)));  // waits
        Assert.True(c.Context(new LinkData().Set("biome", "mountain").Set("night", true))); // replaces the waiting one
        c.Quiet(true); // speaks is not granted: not sent
        var first = await peer.Next();
        Assert.Equal("meadows", ((Dictionary<string, object>)first["data"])["biome"]);
        now += 1;
        c.Pump(); // 1 s: not yet
        now += 0.3; // every 1.25 s, a margin under the daemon's 1 s
        var reading = peer.Next();
        PumpUntil(c, () => reading.IsCompleted, "the waiting context");
        var second = await reading;
        Assert.Equal("context", second["t"]);
        Assert.Equal("mountain", ((Dictionary<string, object>)second["data"])["biome"]);
        // speaks switched on: the standing quiet is said
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"context\":true,\"speaks\":true}}");
        now += 1.3;
        reading = peer.Next();
        PumpUntil(c, () => reading.IsCompleted, "quiet after speaks was granted");
        var q = await reading;
        // the context is said again with the new grants first, then the quiet
        if ((string)q["t"] == "context") q = await peer.Next();
        Assert.Equal(("quiet", true), ((string)q["t"], (bool)q["on"]));
    }

    [Fact]
    public async Task Talk_goes_once_per_two_seconds_and_only_when_granted()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", talk: false));
        Assert.False(c.Talk());
        await peer.Send("{\"t\":\"grants\",\"granted\":{\"talk\":true}}");
        PumpUntil(c, () => c.Grants.Talk, "talk to be granted");
        Assert.True(c.Talk());
        Assert.False(c.Talk());
        now += 1;
        Assert.False(c.Talk());
        now += 1;
        Assert.False(c.Talk()); // 2 s: the daemon's limit, and the client keeps a margin under it
        now += 0.3;
        Assert.True(c.Talk());
        Assert.Equal("talk", (await peer.Next())["t"]);
        var t = await peer.Next();
        Assert.Equal("talk", t["t"]);
        Assert.Single(t); // no text, nothing but its kind
    }

    [Fact]
    public async Task A_dropped_session_resumes_with_the_rotated_resume_and_says_the_context_again()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", context: true));
        c.Context(new LinkData().Set("day", 12));
        Assert.Equal(12.0, ((Dictionary<string, object>)(await peer.Next())["data"])["day"]);
        int first = c.Session;
        await peer.Close();
        PumpUntil(c, () => !c.Live, "the session to drop");
        now += 1.3;
        var again = await Session(c, Welcome("r2", context: true), expectResume: "r1");
        Assert.Equal(first + 1, c.Session);
        var reading = again.Next();
        PumpUntil(c, () => reading.IsCompleted, "the context said again");
        Assert.Equal(12.0, ((Dictionary<string, object>)(await reading)["data"])["day"]);
        await again.Close();
        PumpUntil(c, () => !c.Live, "the second drop");
        await Session(c, Welcome("r3"), expectResume: "r2"); // every welcome rotates it
    }

    [Fact]
    public async Task End_stops_it_for_good()
    {
        using var c = Client();
        string reason = null;
        c.Ended += r => reason = r;
        c.Start();
        var peer = await Session(c, Welcome("r1"));
        await peer.Send("{\"t\":\"end\",\"reason\":\"game_exited\"}");
        PumpUntil(c, () => reason != null, "Ended");
        Assert.Equal("game_exited", reason);
        Assert.True(c.Over);
        Assert.False(c.Live);
        Assert.Null(await mock.Accept(400)); // no reconnect
    }

    [Fact]
    public async Task Silence_ends_the_connection_and_the_session_resumes()
    {
        var t = new LinkClient.Timings { RetryMinMs = 20, RetryMaxMs = 200, WelcomeWithinMs = 1500, KeeperMs = 20, PingEveryMs = 100, SilenceEndsMs = 600 };
        using var c = Client(t);
        c.Start();
        await Session(c, Welcome("r1")); // and then reads nothing: no pong ever comes back
        var again = await mock.Accept(3000);
        Assert.NotNull(again);
        var hello = await again.Next();
        Assert.Equal("r1", hello["resume"]);
        lock (log) Assert.Contains(log, l => l.Contains("silent"));
    }

    [Fact]
    public async Task A_resume_the_daemon_no_longer_knows_falls_back_to_the_file_and_stops_on_a_spent_token()
    {
        var t = new LinkClient.Timings { RetryMinMs = 10, RetryMaxMs = 40, WelcomeWithinMs = 1500, KeeperMs = 20, ResumeTries = 2 };
        using var c = Client(t);
        c.Start();
        var peer = await Session(c, Welcome("r1"));
        await peer.Close(); // the daemon restarted: it knows no resume, and no new Play wrote a new token
        for (int i = 0; i < 2; i++)
        {
            var p = await mock.Accept();
            Assert.Equal("r1", (await p.Next())["resume"]);
            await p.Close();
        }
        PumpUntil(c, () => c.Over, "the client to give up");
        Assert.Null(await mock.Accept(300));
        lock (log) Assert.Contains(log, l => l.Contains("next Play"));
    }

    [Fact]
    public async Task A_new_token_from_a_new_play_is_taken_after_the_resume_is_refused()
    {
        var t = new LinkClient.Timings { RetryMinMs = 10, RetryMaxMs = 40, WelcomeWithinMs = 1500, KeeperMs = 20, ResumeTries = 1 };
        using var c = Client(t);
        c.Start();
        var peer = await Session(c, Welcome("r1"));
        WriteFile(token: "fresh-token-of-a-new-play");
        await peer.Close();
        var p = await mock.Accept();
        Assert.Equal("r1", (await p.Next())["resume"]);
        await p.Close();
        await Session(c, Welcome("r9"), expectToken: "fresh-token-of-a-new-play");
    }

    [Fact]
    public async Task No_welcome_in_time_drops_the_connection_and_tries_again()
    {
        var t = new LinkClient.Timings { RetryMinMs = 10, RetryMaxMs = 40, WelcomeWithinMs = 200, KeeperMs = 20 };
        using var c = Client(t);
        c.Start();
        var mute = await mock.Accept();
        Assert.Equal("hello", (await mute.Next())["t"]);
        var next = await mock.Accept(2000); // it gave up on the mute one
        Assert.NotNull(next);
        Assert.Equal(Token, (await next.Next())["token"]); // no welcome came, so the token is not spent
    }

    [Fact]
    public async Task Triggers_go_only_when_granted_and_within_their_own_rate()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", triggers: "[\"boss_spawned\"]"));
        Assert.False(c.Trigger("raid_started"));
        int sent = 0;
        for (int i = 0; i < 8; i++)
            if (c.Trigger("boss_spawned", new LinkData().Set("boss", "the_elder").Set("stars", 2))) sent++;
        Assert.Equal(5, sent); // results keep room: triggers never take the whole overall rate
        var m = await peer.Next();
        Assert.Equal(("trigger", "boss_spawned"), ((string)m["t"], (string)m["name"]));
        Assert.Equal("the_elder", ((Dictionary<string, object>)m["data"])["boss"]);
    }

    [Fact]
    public async Task A_rate_refusal_from_Astra_says_the_context_again()
    {
        using var c = Client();
        c.Start();
        var peer = await Session(c, Welcome("r1", context: true));
        c.Context(new LinkData().Set("night", true));
        Assert.Equal(true, ((Dictionary<string, object>)(await peer.Next())["data"])["night"]);
        await peer.Send("{\"t\":\"error\",\"code\":\"rate_limited\"}");
        now += 1.3;
        var reading = peer.Next();
        PumpUntil(c, () => reading.IsCompleted, "the context said again");
        Assert.Equal("context", (await reading)["t"]);
    }

    [Fact]
    public async Task A_hello_refused_not_granted_spends_the_token_and_stops()
    {
        using var c = Client();
        c.Start();
        var peer = await mock.Accept();
        Assert.Equal(Token, (await peer.Next())["token"]);
        await peer.Send("{\"t\":\"error\",\"code\":\"not_granted\"}");
        await peer.Close();
        PumpUntil(c, () => c.Over, "the client to stop");
        Assert.Null(await mock.Accept(300));
        lock (log) Assert.Contains(log, l => l.Contains("next Play"));
    }

    [Fact]
    public void A_link_nobody_answers_stops_when_its_file_expires()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int closed = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        WriteFile(expiresIn: 400, port: closed);
        using var c = Client(new LinkClient.Timings { RetryMinMs = 20, RetryMaxMs = 50, ConnectTimeoutMs = 200, KeeperMs = 20 }, write: false);
        c.Start();
        PumpUntil(c, () => c.Over, "the client to stop at expiry");
        lock (log) Assert.Contains(log, l2 => l2.Contains("expired"));
    }

    [Fact]
    public async Task A_result_larger_than_a_result_may_be_is_refused_too_large()
    {
        using var c = Client();
        c.Tool("nearby", _ => LinkResult.Done(new LinkData().Set("list", new string('x', LinkClient.MaxResultBytes))));
        c.Start();
        var peer = await Session(c, Welcome("r1", tools: "[\"game_0123abcd_nearby\"]"));
        await peer.Send("{\"t\":\"call\",\"id\":\"c1\",\"tool\":\"game_0123abcd_nearby\",\"args\":{}}");
        var reading = peer.Next();
        PumpUntil(c, () => reading.IsCompleted, "the result");
        var r = await reading;
        Assert.Equal(("too_large", false), ((string)r["error"], (bool)r["ok"]));
    }

    [Fact]
    public async Task Dispose_ends_the_session_and_nothing_reconnects()
    {
        var c = Client();
        c.Start();
        await Session(c, Welcome("r1", events: "[\"player_fell\"]"));
        c.Dispose();
        Assert.True(c.Over);
        Assert.False(c.Live);
        Assert.False(c.Event("player_fell"));
        Assert.Null(await mock.Accept(300));
    }

    [Fact]
    public void Exposed_tool_names_map_back_to_the_declared_ones()
    {
        Assert.Equal("come_here", LinkClient.LocalToolName("game_0123abcd_come_here"));
        Assert.Equal("come_here", LinkClient.LocalToolName("come_here"));
        Assert.Equal("game_0123ABCD_x", LinkClient.LocalToolName("game_0123ABCD_x")); // not lowercase hex
        Assert.Equal("game_short_x", LinkClient.LocalToolName("game_short_x"));
        Assert.Throws<ArgumentException>(() => LinkResult.Refuse("Not A Code"));
        Assert.Throws<ArgumentException>(() => new LinkData().Set("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LinkData().Set("x", double.NaN));
    }

    // ---- the mock daemon ---------------------------------------------------------------------------

    sealed class Mock : IDisposable
    {
        readonly HttpListener http = new HttpListener();
        Task<HttpListenerContext> pending;
        public int Port { get; }

        public Mock()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            http.Prefixes.Add($"http://127.0.0.1:{Port}/");
            http.Start();
        }

        /// <summary>The next connection, or null when none comes within <paramref name="timeoutMs"/>.</summary>
        public async Task<Peer> Accept(int timeoutMs = 3000)
        {
            pending ??= http.GetContextAsync();
            if (await Task.WhenAny(pending, Task.Delay(timeoutMs)) != pending) return null;
            var ctx = await pending;
            pending = null;
            var headers = new WebHeaderCollection();
            headers.Add(ctx.Request.Headers);
            // Throws unless the client asked for this subprotocol; no keep-alive of the server's own.
            var ws = (await ctx.AcceptWebSocketAsync(LinkClient.Subprotocol, 1 << 16, TimeSpan.Zero)).WebSocket;
            return new Peer(ws, headers);
        }

        public void Dispose()
        {
            try { http.Stop(); http.Close(); } catch (ObjectDisposedException) { }
        }
    }

    sealed class Peer
    {
        readonly WebSocket ws;
        public WebHeaderCollection Headers { get; }

        public Peer(WebSocket ws, WebHeaderCollection headers)
        {
            this.ws = ws;
            Headers = headers;
        }

        /// <summary>The next message, parsed (fails the test after <paramref name="timeoutMs"/>).</summary>
        public async Task<Dictionary<string, object>> Next(int timeoutMs = 3000)
        {
            var buf = new byte[1 << 16];
            int have = 0;
            using var cts = new CancellationTokenSource(timeoutMs);
            while (true)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, have, buf.Length - have), cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) throw new Xunit.Sdk.XunitException("the client closed");
                have += r.Count;
                if (r.EndOfMessage) return (Dictionary<string, object>)JsonReader.ReadValue(Encoding.UTF8.GetString(buf, 0, have));
            }
        }

        public Task Send(string json) => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task Close()
        {
            using var cts = new CancellationTokenSource(2000);
            try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token); }
            catch (Exception e) when (e is WebSocketException || e is OperationCanceledException) { }
            ws.Abort();
        }
    }
}
