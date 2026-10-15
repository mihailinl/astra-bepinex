// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Xunit;

/// <summary>
/// End to end against a REAL engine (set <c>ASTRA_ENGINE</c> to an astra-avatar-engine binary built
/// without its daemon feature): with capture switched on for a test integration in a scratch
/// <c>settings.json</c>, a take this recorder writes is the file the engine's own reader parses
/// (<c>anim/vrma.rs</c>), copies into the player's game pack as <c>captured-climb</c> and removes
/// from the inbox. Scratch pack and settings only — never the player's.
/// </summary>
public class CaptureEngineTests
{
    const string Integration = "astra.capturetest";

    [Fact]
    public void the_engine_files_a_captured_take()
    {
        string exe = Environment.GetEnvironmentVariable("ASTRA_ENGINE");
        Assert.SkipWhen(string.IsNullOrEmpty(exe), "set ASTRA_ENGINE to an engine binary to run this");

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
        string scratch = Path.Combine(Path.GetTempPath(), "astra-capture-test-" + tag);
        string user = Path.Combine(scratch, "user");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(scratch, "settings.json"), "{\"capture\":{\"" + Integration + "\":true}}");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        psi.Environment["WGPU_PROTO_WINDOW"] = "bridge";
        psi.Environment["WGPU_BRIDGE_PORT"] = port.ToString();
        psi.Environment["WGPU_BRIDGE_SHM"] = "astra-test-" + tag;
        psi.Environment["WGPU_BRIDGE_MAX"] = "320x180";
        psi.Environment["WGPU_PACK_MIGRATE"] = "off";
        // No COMPANION_PACK: a pin means "no user root", and capture needs one (its inbox is
        // <user root>/inbox/capture/<integration>/). The user root is the scratch one Astra would
        // assign; the pack migration is off, so the content ladder is only ever read.
        psi.Environment.Remove("COMPANION_PACK");
        psi.Environment["ASTRA_USER_PACK"] = user;
        psi.Environment["ASTRA_SETTINGS"] = Path.Combine(scratch, "settings.json");
        var log = new StringBuilder();
        using var engine = Process.Start(psi);
        engine.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        engine.BeginErrorReadLine();
        try
        {
            var recorder = new CaptureRecorder("climb", "climbing", m => { lock (log) log.AppendLine("[recorder] " + m); });
            using var link = new BridgeLink("astra-bepinex-tests", port, retryMs: 250) { Integration = Integration, Game = "Capture Test" };
            link.Warned = (code, name, why) =>
            {
                lock (log) log.AppendLine($"[warn] {code} {name} {why}");
                if (code == "clip_refused") recorder.Refused(name);
            };
            link.Start();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!link.Ready && DateTime.UtcNow < until)
            {
                link.MarkWanted();
                Thread.Sleep(50);
            }
            Assert.True(link.Ready, "no hello from the engine:\n" + log);
            Assert.True(link.Supports("capture"), "the engine lists capture");
            string inbox = link.Hello.Inbox;
            Assert.False(string.IsNullOrEmpty(inbox), "capture is on, so the reply names an inbox:\n" + log);
            Assert.True(Directory.Exists(inbox));

            // A take of a moving rig: 2.5 s of an arm swinging, at the game's 60 frames per second.
            var rig = new CaptureRig.Sampler();
            int upper = rig.Skeleton.IndexOf("leftUpperArm");
            rig.Local = k => Enumerable.Range(0, rig.Skeleton.Bones.Length)
                .Select(i => i == upper ? Quat.AngleAxis(60 * Math.Sin(k / 10.0), new Vec3(0, 0, 1)) : Quat.Identity).ToArray();
            double now = 0;
            long id = 0;
            string file = null;
            var her = new Placement { Pos = new Vec3(0, 0, -3), Fwd = new Vec3(0, 0, 1) };
            void Frame(bool want)
            {
                now += 1 / 60.0;
                link.MarkWanted();
                link.Send(Messages.Avatar(her, new ParamSet().Set("climbing", want)));
                link.Send(Messages.Cam(++id, new Vec3(0, 1, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 60, 320, 180));
                recorder.Step(now, CaptureInbox.Local(link.Hello?.Inbox), want, () => rig, json =>
                {
                    if (!link.Send(json)) return false;
                    file = json.Split("\"file\":\"")[1].Split('"')[0];
                    return true;
                });
                Thread.Sleep(5);
            }
            for (int i = 0; i < 150; i++) Frame(true);
            while (file == null && DateTime.UtcNow < until) Frame(false);
            Assert.True(file != null, "the take was offered:\n" + log);

            // The engine takes it: its line, the copy in the player's game pack, the inbox emptied.
            string copied = null;
            while (DateTime.UtcNow < until)
            {
                Frame(false);
                copied = Directory.Exists(Path.Combine(user, "animpacks"))
                    ? Directory.GetFiles(Path.Combine(user, "animpacks"), "captured-" + file, SearchOption.AllDirectories).FirstOrDefault()
                    : null;
                bool said;
                lock (log) said = log.ToString().Contains("[animpacks] captured ");
                if (copied != null && said && !File.Exists(Path.Combine(inbox, file))) break;
            }
            string text;
            lock (log) text = log.ToString();
            Assert.True(copied != null, "the copy is in the game pack:\n" + text);
            Assert.Contains($"[animpacks] captured 'captured-climb' from '{Integration}'", text);
            Assert.DoesNotContain("clip_refused", text);
            // The engine's reader had nothing to say about the file: no skipped channel, no missing
            // hips, no rest hips at the floor, nothing flattened.
            Assert.DoesNotContain("[anim] VRMA", text);
            Assert.False(File.Exists(Path.Combine(inbox, file)), "the engine removed the inbox file once its copy landed");
            string pack = File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(Path.GetDirectoryName(copied)), "animpack.toml").Single());
            Assert.Contains("captured-climb", pack);
            Assert.Contains("captured_from = \"" + Integration + "\"", pack);
        }
        finally
        {
            if (!engine.HasExited) engine.Kill();
            engine.WaitForExit(5000);
            File.Delete("/dev/shm/astra-test-" + tag);
            Directory.Delete(scratch, true);
        }
    }
}
