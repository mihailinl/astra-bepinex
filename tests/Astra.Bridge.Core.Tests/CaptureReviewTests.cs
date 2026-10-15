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
/// The capture review's findings (round 1), each as the behaviour it asked for: the live capture
/// switch (S1), files never left in the player's inbox (S2), the take's frame frozen at its start
/// (S3), the writer and Dispose serialised (S4), the rest from the right mesh (S5, N2), the take's
/// lead-in (T0) and no allocation per sample (N1).
/// </summary>
public class CaptureReviewTests : IDisposable
{
    readonly string inbox = Directory.CreateTempSubdirectory("astra-capture-r1-").FullName;
    readonly List<string> sent = new List<string>();
    readonly List<string> log = new List<string>();
    readonly CaptureRig.Sampler rig = new CaptureRig.Sampler();
    readonly List<Action> writes = new List<Action>(); // a writer that runs only when the test says
    double now = 100;

    public void Dispose() => Directory.Delete(inbox, true);

    string[] Files() => Directory.GetFiles(inbox).Select(Path.GetFileName).OrderBy(f => f).ToArray();

    void Play(CaptureRecorder r, double seconds, bool want, string dir = "", IPoseSampler sampler = null)
    {
        string at = dir == "" ? inbox : dir;
        for (int i = 0; i < (int)Math.Round(seconds * 60); i++)
        {
            now += 1 / 60.0;
            r.Step(now, at, want, () => sampler ?? rig, json => { sent.Add(json); return true; });
        }
    }

    CaptureRecorder Deferred(string clip = "climb") => new CaptureRecorder(clip, "climbing", l => log.Add(l), a => writes.Add(a));

    void RunWrites()
    {
        foreach (var w in writes) w();
        writes.Clear();
    }

    // ---- S2: names the engine echoes back whole; a stale take swept ----

    /// <summary>S2b (the review's repro): a clip name longer than 48 is refused at once — its file
    /// name would pass the 64 characters the engine echoes back in <c>clip_refused</c>, and the
    /// refusal would never match. A 48-character one's file name is at most 64, and its refusal
    /// (cut to 64 by the engine: unchanged) removes it.</summary>
    [Fact]
    public void a_long_clip_name_is_refused_and_a_48_character_ones_refusal_matches()
    {
        Assert.Throws<ArgumentException>(() => new CaptureRecorder(new string('c', 60), "climbing", null));
        Assert.Throws<ArgumentException>(() => new CaptureRecorder(new string('c', 49), "climbing", null));
        var r = new CaptureRecorder(new string('c', 48), "climbing", l => log.Add(l), a => a());
        Play(r, 2.5, true);
        Play(r, 0.2, false);
        var file = Assert.Single(Files());
        Assert.InRange(file.Length, 1, CaptureInbox.MaxEchoedName);
        r.Refused(file.Length > 64 ? file.Substring(0, 64) : file); // what the engine's clip_refused carries
        Play(r, 0.1, false);
        Assert.Empty(Files());
    }

    /// <summary>S2d: a take of this clip left in the inbox longer than 10 minutes — refused while the
    /// game was gone, never offered — is removed when an inbox next appears; a recent one, another
    /// clip's, a stranger's file and a file in another shape are left alone.</summary>
    [Fact]
    public void old_takes_left_in_the_inbox_are_swept_when_an_inbox_appears()
    {
        var old = DateTime.UtcNow.AddMinutes(-11);
        void Put(string name, DateTime when)
        {
            var p = Path.Combine(inbox, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, when);
        }
        Put("climb-1792000000.vrma", old);
        Put("climb-1792000001.vrma.part", old);
        Put("climb-1792000002.vrma", DateTime.UtcNow);
        Put("rope-1792000000.vrma", old);
        Put("climb-x.vrma", old);
        Put("notes.txt", old);
        var r = new CaptureRecorder("climb", "climbing", l => log.Add(l), a => a());
        Play(r, 0.1, false, dir: null);
        Assert.Equal(6, Files().Length); // no inbox: nothing touched
        Play(r, 0.1, false);
        Assert.Equal(new[] { "climb-1792000002.vrma", "climb-x.vrma", "notes.txt", "rope-1792000000.vrma" }, Files());
        Assert.Contains(log, l => l.Contains("removed 2 old take"));
    }

    // ---- S1: the switch flipped while the game runs ----

    /// <summary>S1: capture switched OFF mid-take stops it at once and leaves nothing — the take
    /// being recorded is dropped, and one being written is removed by its writer when it finishes
    /// (no <c>.part</c>, no <c>.vrma</c>).</summary>
    [Fact]
    public void capture_off_mid_take_leaves_nothing()
    {
        var r = Deferred();
        Play(r, 2.5, true);
        Assert.Equal(CaptureState.Recording, r.State);
        Play(r, 0.1, true, dir: null);
        Assert.Equal(CaptureState.Idle, r.State);
        Play(r, 0.5, false);
        RunWrites();
        Assert.Empty(Files());
        Assert.Empty(sent);

        Play(r, 2.5, true);
        Play(r, 0.05, false); // finished: its writer has not run yet
        Assert.Equal(CaptureState.Writing, r.State);
        Play(r, 0.05, false, dir: null);
        RunWrites();
        Assert.Empty(Files());
        Assert.Empty(sent);
    }

    /// <summary>S1, end to end over the link against a fake engine that lists <c>capture-live</c>:
    /// its hello reply names no inbox; <c>{"t":"capture","inbox":…}</c> starts capturing without a
    /// reconnect; <c>{"t":"capture","inbox":null}</c> mid-take stops it and nothing is offered. An
    /// engine WITHOUT <c>capture-live</c> is not believed: its message changes nothing.</summary>
    [Fact]
    public async Task the_capture_switch_reaches_a_running_game()
    {
        async Task Run(bool live)
        {
            int port = FreePort();
            var http = new HttpListener();
            http.Prefixes.Add($"http://127.0.0.1:{port}/");
            http.Start();
            var say = new SemaphoreSlim(0);
            string next = null;
            string hello = null;
            var served = Task.Run(async () =>
            {
                var ctx = await http.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var buf = new byte[65536];
                var r0 = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                hello = Encoding.UTF8.GetString(buf, 0, r0.Count);
                string caps = live ? "[\"warn\",\"capture\",\"capture-live\"]" : "[\"warn\",\"capture\"]";
                await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"fake\",\"shm\":\"/dev/shm/none\",\"maxW\":64,\"maxH\":64,\"caps\":" + caps + "}");
                var drain = Task.Run(async () =>
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                    }
                });
                while (await say.WaitAsync(10000))
                {
                    if (next == null) break;
                    await Send(ws, next);
                }
            });
            var r = new CaptureRecorder("climb", "climbing", l => { lock (log) log.Add(l); }, a => a());
            using var link = new BridgeLink("test", port, retryMs: 250) { Integration = "astra.peak" };
            link.MarkWanted();
            link.Start();
            for (int i = 0; i < 100 && !link.Ready; i++) await Task.Delay(50);
            Assert.True(link.Ready);
            Assert.Contains("\"capture\"", hello);
            Assert.Equal(live, link.CaptureLive);
            Assert.Null(link.CaptureInbox);

            async Task Tell(string json, Func<bool> until)
            {
                next = json;
                say.Release();
                for (int i = 0; i < 100 && !until(); i++) await Task.Delay(20);
            }
            void Frames(double seconds, bool want)
            {
                for (int i = 0; i < (int)Math.Round(seconds * 60); i++)
                {
                    now += 1 / 60.0;
                    link.MarkWanted();
                    r.Step(now, CaptureInbox.Local(link.CaptureInbox), want, () => rig, link.Send);
                }
            }
            string escaped = inbox.Replace("\\", "\\\\");
            await Tell("{\"t\":\"capture\",\"inbox\":\"" + escaped + "\"}", () => link.CaptureInbox != null);
            if (!live)
            {
                Assert.Null(link.CaptureInbox);
                Frames(2.5, true);
                Assert.Equal(CaptureState.Idle, r.State);
            }
            else
            {
                Assert.Equal(inbox, link.CaptureInbox);
                Frames(2.5, true);
                Assert.Equal(CaptureState.Recording, r.State);
                await Tell("{\"t\":\"capture\",\"inbox\":null}", () => link.CaptureInbox == null);
                Assert.Null(link.CaptureInbox);
                Frames(0.1, true);
                Assert.Equal(CaptureState.Idle, r.State);
                Frames(0.5, false);
            }
            Assert.Empty(Files());
            next = null;
            say.Release();
            http.Stop();
            try { await served; } catch (Exception) { }
        }
        await Run(live: true);
        await Run(live: false);
    }

    // ---- S4: the writer and Dispose ----

    /// <summary>S4: the game closing while a take is being written leaves no file, whichever ends
    /// first: Dispose before the writer finishes (the writer removes its own <c>.part</c>), and
    /// Dispose after it finished but before the offer (Dispose removes the <c>.vrma</c>).</summary>
    [Fact]
    public void dispose_and_the_writer_never_leave_a_file()
    {
        var r = Deferred();
        Play(r, 2.5, true);
        Play(r, 0.05, false);
        Assert.Equal(CaptureState.Writing, r.State);
        r.Dispose();
        RunWrites();
        Assert.Empty(Files());

        var r2 = new CaptureRecorder("climb", "climbing", l => log.Add(l), a => a());
        Play(r2, 2.5, true);
        for (int i = 0; i < 2; i++)
        {
            now += 1 / 60.0;
            r2.Step(now, inbox, false, () => rig, json => false); // written, not offered (the link is down)
        }
        Assert.Equal(CaptureState.Ready, r2.State);
        Assert.Single(Files());
        r2.Dispose();
        Assert.Empty(Files());
    }

    // ---- S3: the take's frame is frozen at its start ----

    /// <summary>S3: a root that turns while the body holds still (PEAK turns its scout's root with
    /// the camera while the scout holds the wall) puts NO turn into the take; a body that turns with
    /// its root (a real turn) keeps it.</summary>
    [Fact]
    public void the_players_mouse_is_not_in_the_take()
    {
        RootPose Turning(int k) => new RootPose(Quat.AngleAxis(k, new Vec3(0, 1, 0)), new Vec3(5, 2, 1), new Vec3(1, 1, 1));
        var still = new CaptureRig.Sampler { Root = Turning, BodyStill = true };
        var r = new CaptureRecorder("climb", "climbing", l => log.Add(l), a => a());
        Play(r, 2.5, true, sampler: still);
        var take = r.Take;
        for (int f = 0; f < take.Count; f++)
            Assert.True(CaptureRig.Near(take.Rotation(f, 0), take.Rotation(0, 0), 0.01), $"frame {f}: {take.Rotation(f, 0)}");

        var turning = new CaptureRig.Sampler { Root = Turning };
        var r2 = new CaptureRecorder("turn", "turning", l => log.Add(l), a => a());
        Play(r2, 2.5, true, sampler: turning);
        double turned = Quat.Angle(r2.Take.Rotation(0, 0), r2.Take.Rotation(r2.Take.Count - 1, 0));
        Assert.InRange(turned, r2.Take.Count - 1 - 0.1, r2.Take.Count - 1 + 0.1);
    }

    // ---- T0: the take's lead-in ----

    /// <summary>T0: the take's FIRST frame is the pose the game shows <see cref="CaptureRecorder.LeadIn"/>
    /// after the parameter turned true — never the frame of the edge (a game's way INTO the state:
    /// PEAK's scout flings its arms out as it jumps at the wall) and never the rest.</summary>
    [Fact]
    public void a_takes_first_frame_is_the_state_not_the_way_into_it()
    {
        var sk = rig.Skeleton;
        int arm = sk.IndexOf("leftUpperArm");
        var jump = Quat.AngleAxis(-80, new Vec3(0, 0, 1));  // flung out
        var climb = Quat.AngleAxis(70, new Vec3(0, 0, 1));  // reaching up
        double edge = 0;
        var timed = new CaptureRig.Sampler();
        timed.Local = _ => Enumerable.Range(0, sk.Bones.Length).Select(i => i != arm ? Quat.Identity : now - edge < 0.2 ? jump : climb).ToArray();
        var r = new CaptureRecorder("climb", "climbing", l => log.Add(l), a => a());
        Play(r, 0.1, false);
        edge = now;
        Play(r, 2.5, true, sampler: timed);
        var take = r.Take;
        Assert.True(CaptureRig.Near(take.Rotation(0, arm), climb, 0.01), $"frame 0's arm: {take.Rotation(0, arm)}");
        Assert.False(CaptureRig.Near(take.Rotation(0, arm), Quat.Identity, 1), "not the rest");
        Assert.Equal(0f, take.Times[0]);
        Assert.InRange(take.Duration, 2.5 - CaptureRecorder.LeadIn - 0.1, 2.5 - CaptureRecorder.LeadIn + 0.05);
    }

    // ---- N1: no allocation per sample ----

    /// <summary>N1: a frame's normalization allocates nothing once warm (30 times a second for a whole
    /// climb).</summary>
    [Fact]
    public void normalizing_a_frame_allocates_nothing()
    {
        var rest = CaptureRig.Rest();
        var sk = CaptureRig.Skeleton(rest);
        var rot = CaptureRig.RestRotations(sk, rest);
        var pos = CaptureRig.RestPositions(sk, rest);
        var local = new Quat[sk.Bones.Length];
        sk.Normalize(rot, pos, local, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) sk.Normalize(rot, pos, local, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- S5, N2: the rest from the right mesh, a matrix read honestly ----

    static double[] Matrix(Quat q, Vec3 p, Vec3 scale = default)
    {
        if (scale.Length == 0) scale = new Vec3(1, 1, 1);
        var x = q.Rotate(new Vec3(1, 0, 0)) * scale.X;
        var y = q.Rotate(new Vec3(0, 1, 0)) * scale.Y;
        var z = q.Rotate(new Vec3(0, 0, 1)) * scale.Z;
        return new[] { x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, p.X, p.Y, p.Z, 1 };
    }

    /// <summary>N2: a bind matrix becomes its rotation — also under a non-uniform scale (the nearest
    /// rotation, and the skew said); a mirrored one is said, never silently taken.</summary>
    [Fact]
    public void a_bind_matrix_is_read_honestly()
    {
        var q = CaptureRig.Arbitrary(3, 120);
        var r = BindRest.FromMatrix(Matrix(q, new Vec3(1, 2, 3)), out var p, out bool mirrored, out double skew);
        Assert.True(CaptureRig.Near(r, q), $"{r} vs {q}");
        Assert.Equal(new Vec3(1, 2, 3), p);
        Assert.False(mirrored);
        Assert.InRange(skew, 0, 1e-9);
        r = BindRest.FromMatrix(Matrix(q, Vec3.Zero, new Vec3(2, 0.5, 1)), out _, out mirrored, out _);
        Assert.True(CaptureRig.Near(r, q), "a non-uniform scale keeps the rotation");
        r = BindRest.FromMatrix(Matrix(q, Vec3.Zero, new Vec3(-1, 1, 1)), out _, out mirrored, out _);
        Assert.True(mirrored);
        Assert.True(CaptureRig.Near(r, q), "a mirrored X: the unmirrored twin");
    }

    /// <summary>S5: the rest comes from the mesh that skins the MOST bones (a body, not a glove bound
    /// another way), a mesh that moves with a bone last; where two meshes disagree, a warning says so;
    /// a bone only the glove has is taken from it.</summary>
    [Fact]
    public void the_rest_comes_from_the_body_and_disagreement_is_said()
    {
        var body = new BindMesh { Name = "Body" };
        var glove = new BindMesh { Name = "Glove" };
        var hat = new BindMesh { Name = "Hat", UnderBone = true };
        var a = CaptureRig.Arbitrary(1, 40);
        var b = CaptureRig.Arbitrary(2, 40);
        foreach (var bone in new[] { "hips", "spine", "leftHand" }) body.Bones[bone] = Matrix(a, Vec3.Zero);
        glove.Bones["leftHand"] = Matrix(b, Vec3.Zero);
        glove.Bones["leftIndexProximal"] = Matrix(b, new Vec3(1, 0, 0));
        foreach (var bone in new[] { "hips", "spine", "leftHand", "head" }) hat.Bones[bone] = Matrix(b, Vec3.Zero);
        var rest = BindRest.Choose(new[] { glove, hat, body }, new[] { "hips", "spine", "head", "leftHand", "leftIndexProximal", "leftFoot" },
            out var warnings, out var missing, out string source);
        RestBone Of(string n) => rest.Single(x => x.Name == n);
        Assert.True(CaptureRig.Near(Of("hips").Rotation, a), "the body, not the hat that skins more but moves with a bone");
        Assert.True(CaptureRig.Near(Of("leftHand").Rotation, a), "the body's hand, not the glove's");
        Assert.True(CaptureRig.Near(Of("leftIndexProximal").Rotation, b), "a bone only the glove has");
        Assert.True(CaptureRig.Near(Of("head").Rotation, b), "a bone only the hat has: the hat, last");
        Assert.Equal(new[] { "leftFoot" }, missing);
        Assert.Contains(warnings, w => w.Contains("disagree") && w.Contains("Glove"));
        Assert.Equal("Body, Hat, Glove", source);
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
