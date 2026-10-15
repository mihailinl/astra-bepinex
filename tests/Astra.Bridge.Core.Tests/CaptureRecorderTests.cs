// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Xunit;

/// <summary>
/// <see cref="CaptureRecorder"/>: when it records (only with an inbox, only while its parameter is
/// true), how it writes (<c>.part</c>, then the name), what it tells the engine, and every bound — the
/// take's length, the takes per run, the gap between two offers, a refused or orphaned file removed.
/// The game's frames are simulated at 60 per second; the file writing runs at once.
/// </summary>
public class CaptureRecorderTests : IDisposable
{
    readonly string inbox = Directory.CreateTempSubdirectory("astra-capture-").FullName;
    readonly List<string> sent = new List<string>();
    readonly List<string> log = new List<string>();
    readonly CaptureRig.Sampler rig = new CaptureRig.Sampler();
    double now = 100;

    public void Dispose() => Directory.Delete(inbox, true);

    CaptureRecorder Recorder() => new CaptureRecorder("climb", "climbing", l => log.Add(l), a => a());

    bool connected = true;

    /// <summary>Run <paramref name="seconds"/> of game frames with the parameter at
    /// <paramref name="want"/>.</summary>
    void Play(CaptureRecorder r, double seconds, bool want, string inboxNow = "")
    {
        string dir = inboxNow == "" ? inbox : inboxNow;
        for (int i = 0; i < (int)Math.Round(seconds * 60); i++)
        {
            now += 1 / 60.0;
            r.Step(now, dir, want, () => rig, json => { if (connected) sent.Add(json); return connected; });
        }
    }

    string[] Files() => Directory.GetFiles(inbox).Select(Path.GetFileName).OrderBy(f => f).ToArray();

    /// <summary>Capture OFF (no inbox in the hello reply): nothing is ever recorded, not even
    /// sampled, however long the parameter is true.</summary>
    [Fact]
    public void with_capture_off_nothing_is_recorded()
    {
        var r = Recorder();
        Play(r, 5, true, inboxNow: null);
        Play(r, 1, false, inboxNow: null);
        Assert.Equal(0, rig.Samples);
        Assert.Empty(sent);
        Assert.Empty(Files());
    }

    /// <summary>A take, end to end: sampled at 30 per second while the parameter is true, written as
    /// <c>&lt;file&gt;.part</c> and renamed, offered with a <c>clip_ready</c> that names the bare file,
    /// the clip and the parameter — and the file is a VRMA of the take.</summary>
    [Fact]
    public void a_take_is_written_renamed_and_offered()
    {
        var r = Recorder();
        Play(r, 0.5, false);
        Play(r, 2, true);
        Assert.Equal(CaptureState.Recording, r.State);
        Assert.InRange(rig.Samples, 59, 62); // 2 s at 30 per second, from game frames at 60
        Play(r, 0.1, false);
        Assert.Equal(CaptureState.Idle, r.State);
        var file = Assert.Single(Files());
        Assert.Matches(@"^climb-\d{10}\.vrma$", file);
        Assert.True(CaptureRecorder.ValidFileName(file));
        Assert.Equal($"{{\"t\":\"clip_ready\",\"file\":\"{file}\",\"name\":\"climb\",\"for\":\"climbing\"}}", Assert.Single(sent));
        var (json, _) = VrmaWriterTests.ReadGlb(File.ReadAllBytes(Path.Combine(inbox, file)));
        Assert.Equal("climb", json.GetProperty("animations")[0].GetProperty("name").GetString());
        int frames = json.GetProperty("accessors")[0].GetProperty("count").GetInt32();
        Assert.Equal(rig.Samples, frames);
        Assert.Equal(1, r.Takes);
    }

    /// <summary>A take shorter than <see cref="CaptureRecorder.MinSeconds"/> (a grab at a ledge) is
    /// not kept, offered or counted.</summary>
    [Fact]
    public void a_short_take_is_dropped()
    {
        var r = Recorder();
        Play(r, 1, true);
        Play(r, 0.1, false);
        Assert.Empty(Files());
        Assert.Empty(sent);
        Assert.Equal(0, r.Takes);
        Assert.Contains(log, l => l.Contains("not kept"));
    }

    /// <summary>A take ends at <see cref="CaptureRecorder.MaxSeconds"/> and is offered; the next one
    /// waits for the parameter to turn false and true again.</summary>
    [Fact]
    public void a_take_ends_at_the_cap_and_the_next_waits_for_a_new_edge()
    {
        var r = Recorder();
        Play(r, CaptureRecorder.MaxSeconds + 5, true);
        Assert.Single(sent);
        Assert.Equal(CaptureState.Idle, r.State);
        Play(r, 10, true);
        Assert.Single(sent); // still true: no second take
        Play(r, 0.1, false);
        Play(r, 2, true);
        Assert.Equal(CaptureState.Recording, r.State);
    }

    /// <summary>At most <see cref="CaptureRecorder.MaxTakes"/> takes are offered in a game run: each
    /// becomes a card the player answers.</summary>
    [Fact]
    public void at_most_max_takes_are_offered_per_run()
    {
        var r = Recorder();
        for (int i = 0; i < CaptureRecorder.MaxTakes + 2; i++)
        {
            Play(r, 2, true);
            Play(r, 1.1, false); // 1.1 s apart: the gap is not what stops them
        }
        Assert.Equal(CaptureRecorder.MaxTakes, sent.Count);
        Assert.Equal(CaptureRecorder.MaxTakes, Files().Length);
        Assert.Equal(sent.Count, sent.Distinct().Count());
    }

    /// <summary>Two offers are never closer than the engine's 500 ms (it answers a faster one with
    /// an error): a take lasts at least <see cref="CaptureRecorder.MinSeconds"/>, and the next is
    /// recorded only once the one before was offered — even when the link held that one back.</summary>
    [Fact]
    public void two_offers_are_never_closer_than_the_engine_allows()
    {
        Assert.True(CaptureRecorder.MinSeconds > 0.5);
        var r = Recorder();
        var at = new List<double>();
        int count = 0;
        Play(r, 2, true);
        connected = false;
        Play(r, 2 / 60.0, false);
        Assert.Equal(CaptureState.Ready, r.State);
        Play(r, 3, true); // climbing again while the first waits: not recorded over it
        Assert.Equal(CaptureState.Ready, r.State);
        connected = true;
        for (int i = 0; i < 4 * 60; i++)
        {
            Play(r, 1 / 60.0, i % 120 < 100); // true, then a short false, twice
            if (sent.Count > count)
            {
                count = sent.Count;
                at.Add(now);
            }
        }
        Assert.True(at.Count >= 2, $"{at.Count} offers");
        for (int i = 1; i < at.Count; i++) Assert.True(at[i] - at[i - 1] > 0.5, $"{at[i] - at[i - 1]} s apart");
    }

    /// <summary>The inbox goes away mid-take (the player switched capture off, or Astra went away):
    /// the take is dropped, nothing written.</summary>
    [Fact]
    public void a_take_is_dropped_when_the_inbox_goes()
    {
        var r = Recorder();
        Play(r, 2, true);
        Play(r, 1, true, inboxNow: null);
        Assert.Equal(CaptureState.Idle, r.State);
        Play(r, 0.1, false);
        Assert.Empty(Files());
        Assert.Empty(sent);
    }

    /// <summary>A take written but not yet offered when capture goes off is removed — the engine
    /// will never hear of it, so it must not stay in the player's inbox.</summary>
    [Fact]
    public void a_written_take_never_offered_is_removed()
    {
        var r = Recorder();
        Play(r, 2, true);
        connected = false;
        Play(r, 1 / 60.0, false);
        Assert.Single(Files());
        Play(r, 1 / 60.0, false, inboxNow: null);
        Assert.Empty(Files());
        Assert.Empty(sent);
        Assert.Equal(CaptureState.Idle, r.State);
    }

    /// <summary>The engine's <c>clip_refused</c> for a file this recorder offered removes it (the
    /// engine leaves a refused file to the game); a name it never offered removes nothing.</summary>
    [Fact]
    public void a_refused_file_is_removed_and_nothing_else()
    {
        var stranger = Path.Combine(inbox, "climb-1.vrma");
        File.WriteAllText(stranger, "not ours");
        var r = Recorder();
        Play(r, 2, true);
        Play(r, 0.1, false);
        string ours = Files().Single(f => f != "climb-1.vrma");
        r.Refused("climb-1.vrma");
        r.Refused(ours);
        Play(r, 0.1, false);
        Assert.Equal(new[] { "climb-1.vrma" }, Files());
        Assert.Contains(log, l => l.Contains("did not take") && l.Contains(ours));
    }

    /// <summary>The rig vanishing mid-take (the player died, the level unloaded) drops the take.</summary>
    [Fact]
    public void a_take_whose_rig_vanishes_is_dropped()
    {
        var r = Recorder();
        Play(r, 2, true);
        rig.Gone = true;
        Play(r, 1, true);
        Assert.Equal(CaptureState.Idle, r.State);
        Play(r, 0.1, false);
        Assert.Empty(Files());
    }

    /// <summary>The names the engine refuses are refused here first, and the inbox a Linux engine
    /// names is reached through Wine's Z: drive by a Windows game.</summary>
    [Fact]
    public void names_and_the_inbox_path_follow_the_engines_rules()
    {
        Assert.Throws<ArgumentException>(() => new CaptureRecorder(".climb", "climbing", null));
        Assert.Throws<ArgumentException>(() => new CaptureRecorder("cl/imb", "climbing", null));
        Assert.Throws<ArgumentException>(() => new CaptureRecorder("climb", new string('c', 65), null));
        Assert.False(CaptureRecorder.ValidFileName(".climb-1.vrma"));
        Assert.False(CaptureRecorder.ValidFileName("climb-1.fbx"));
        Assert.False(CaptureRecorder.ValidFileName("a/b.vrma"));
        Assert.False(CaptureRecorder.ValidFileName(new string('c', 124) + ".vrma"));
        Assert.True(CaptureRecorder.ValidFileName(new string('c', 123) + ".vrma"));
        Assert.Null(Messages.ClipReady("../x.vrma", "climb", "climbing"));
        Assert.Equal(@"Z:\home\u\.config\astra\inbox\capture\astra.peak",
            CaptureRecorder.LocalInbox("/home/u/.config/astra/inbox/capture/astra.peak", windows: true));
        Assert.Equal("/home/u/inbox", CaptureRecorder.LocalInbox("/home/u/inbox", windows: false));
        Assert.Equal(@"C:\Users\u\inbox", CaptureRecorder.LocalInbox(@"C:\Users\u\inbox", windows: true));
    }

    /// <summary>The hello reply's <c>inbox</c> is read; an empty one is no inbox. A frame's flag is
    /// read back from the frame's parameters.</summary>
    [Fact]
    public void the_hello_reply_names_the_inbox_and_a_flag_reads_back()
    {
        var reply = HelloReply.From(JsonReader.ReadObject("{\"t\":\"hello\",\"v\":1,\"caps\":[\"capture\"],\"inbox\":\"/x/y\"}"));
        Assert.Equal("/x/y", reply.Inbox);
        Assert.Null(HelloReply.From(JsonReader.ReadObject("{\"t\":\"hello\",\"v\":1,\"inbox\":\"\"}")).Inbox);
        Assert.Null(HelloReply.From(JsonReader.ReadObject("{\"t\":\"hello\",\"v\":1}")).Inbox);
        var p = new ParamSet().Set("climbing", true).Set("depth", 3.0);
        Assert.True(p.TryGetFlag("climbing", out bool on) && on);
        Assert.False(p.TryGetFlag("depth", out on) || on);
        Assert.False(p.TryGetFlag("absent", out on) || on);
        p.Set("climbing", false);
        Assert.True(p.TryGetFlag("climbing", out on) && !on);
    }

    /// <summary>
    /// The handshake against a fake ENGINE, over the real link: its hello reply names an inbox and
    /// lists <c>capture</c>; a take written there is offered with <c>clip_ready</c>; its
    /// <c>clip_refused</c> warning reaches the recorder through <see cref="BridgeLink.Warned"/> and the
    /// file is removed. A reply without an inbox records nothing.
    /// </summary>
    [Fact]
    public async Task the_inbox_handshake_with_an_engine()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var received = new List<string>();
        var refuseNow = new TaskCompletionSource<string>();
        string escaped = inbox.Replace("\\", "\\\\");
        var served = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buf = new byte[65536];
            var hello = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            lock (received) received.Add(Encoding.UTF8.GetString(buf, 0, hello.Count));
            await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"fake\",\"shm\":\"/dev/shm/none\",\"maxW\":64,\"maxH\":64,"
                + "\"caps\":[\"params\",\"warn\",\"capture\"],\"inbox\":\"" + escaped + "\"}");
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                string text = Encoding.UTF8.GetString(buf, 0, r.Count);
                if (!text.Contains("clip_ready")) continue;
                lock (received) received.Add(text);
                string file = text.Split("\"file\":\"")[1].Split('"')[0];
                await Send(ws, "{\"t\":\"warn\",\"code\":\"clip_refused\",\"name\":\"" + file + "\",\"why\":\"not a VRMA animation\"}");
                refuseNow.TrySetResult(file);
            }
        });

        var r = Recorder();
        using var link = new BridgeLink("test", port, retryMs: 250) { Integration = "astra.peak" };
        link.Warned = (code, name, why) => { if (code == "clip_refused") r.Refused(name); };
        link.MarkWanted();
        link.Start();
        for (int i = 0; i < 100 && !link.Ready; i++) await Task.Delay(50);
        Assert.True(link.Ready, "the fake engine answered");
        Assert.True(link.Supports("capture"));
        Assert.Equal(inbox, link.Hello.Inbox);

        void Frame(bool want)
        {
            now += 1 / 60.0;
            link.MarkWanted();
            r.Step(now, CaptureRecorder.LocalInbox(link.Hello?.Inbox), want, () => rig, link.Send);
        }
        for (int i = 0; i < 120; i++) Frame(true);
        for (int i = 0; i < 250 && !refuseNow.Task.IsCompleted; i++)
        {
            Frame(false);
            await Task.Delay(20);
        }
        string offered = await refuseNow.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(Path.Combine(inbox, offered)), "written before it was offered");
        for (int i = 0; i < 100 && File.Exists(Path.Combine(inbox, offered)); i++)
        {
            Frame(false);
            await Task.Delay(20);
        }
        Assert.False(File.Exists(Path.Combine(inbox, offered)), "the refused file is removed");
        lock (received)
        {
            Assert.Contains("\"integration\":\"astra.peak\"", received[0]);
            Assert.Contains("\"t\":\"clip_ready\"", received[1]);
        }
        http.Stop();
        try { await served; } catch (Exception) { }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
}
