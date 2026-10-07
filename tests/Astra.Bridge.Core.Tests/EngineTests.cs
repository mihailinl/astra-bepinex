// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Astra.Bridge;
using Xunit;

/// <summary>
/// End to end against a REAL engine: set <c>ASTRA_ENGINE</c> to an astra-avatar-engine binary built
/// without the daemon feature. It runs in game mode on a free port, with its own ring and a scratch
/// pack and settings — never the player's.
/// </summary>
public class EngineTests
{
    [Fact]
    public void the_engine_draws_her_for_our_camera()
    {
        string exe = Environment.GetEnvironmentVariable("ASTRA_ENGINE");
        Assert.SkipWhen(string.IsNullOrEmpty(exe), "set ASTRA_ENGINE to an engine binary to run this");

        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        string tag = Guid.NewGuid().ToString("N").Substring(0, 8);
        string scratch = Path.Combine(Path.GetTempPath(), "astra-engine-test-" + tag);
        Directory.CreateDirectory(Path.Combine(scratch, "user"));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true };
        psi.Environment["WGPU_PROTO_WINDOW"] = "bridge";
        psi.Environment["WGPU_BRIDGE_PORT"] = port.ToString();
        psi.Environment["WGPU_BRIDGE_SHM"] = "astra-test-" + tag;
        psi.Environment["WGPU_BRIDGE_MAX"] = "640x360";
        psi.Environment["WGPU_PACK_MIGRATE"] = "off";
        psi.Environment["COMPANION_PACK"] = Path.Combine(scratch, "no-pack");
        psi.Environment["ASTRA_USER_PACK"] = Path.Combine(scratch, "user");
        psi.Environment["ASTRA_SETTINGS"] = Path.Combine(scratch, "settings.json");
        var log = new System.Text.StringBuilder();
        using var engine = Process.Start(psi);
        engine.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        engine.BeginErrorReadLine();
        try
        {
            using var link = new BridgeLink("astra-bepinex-tests", port, retryMs: 250);
            link.Start();
            var until = DateTime.UtcNow.AddSeconds(20);
            while (!link.Ready && DateTime.UtcNow < until) Thread.Sleep(50);
            Assert.True(link.Ready, "no hello from the engine:\n" + log);
            Assert.EndsWith("astra-test-" + tag, link.Hello.Shm);
            Assert.Equal((640, 360), (link.Hello.MaxWidth, link.Hello.MaxHeight));

            using var ring = FrameRing.Open(link.Hello.Shm);
            // She stands 3 m in front of an eye 1 m up, facing it.
            var her = new Placement { Pos = new Vec3(0, 0, -3), Fwd = new Vec3(0, 0, 1) };
            long seen = 0, id = 0;
            RingFrame f = default;
            int opaque = 0;
            float nearest = float.MaxValue, farthest = 0;
            while (DateTime.UtcNow < until && opaque == 0)
            {
                Assert.True(link.Send(Messages.Avatar(her, new ParamSet().Set("speed", 0.0))));
                Assert.True(link.Send(Messages.Cam(++id, new Vec3(0, 1, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 60, 640, 360, new double[] { 42 })));
                Thread.Sleep(30);
                if (!ring.TryLatest(ref seen, out f)) continue;
                var colour = new byte[f.PlaneBytes];
                var depth = new float[f.Width * f.Height];
                System.Runtime.InteropServices.Marshal.Copy(f.Colour, colour, 0, colour.Length);
                System.Runtime.InteropServices.Marshal.Copy(f.Depth, depth, 0, depth.Length);
                if (!ring.StillValid(ref f)) continue;
                opaque = 0;
                for (int i = 0; i < depth.Length; i++)
                {
                    if (colour[4 * i + 3] < 255) continue;
                    opaque++;
                    nearest = Math.Min(nearest, depth[i]);
                    farthest = Math.Max(farthest, depth[i]);
                }
            }
            Assert.True(opaque > 1000, $"no picture of her after {id} cameras:\n" + log);
            Assert.InRange(f.HostFrame, 1, id);
            Assert.Equal(42f, f.Echo0);
            Assert.Equal(new Vec3(0, 1, 0), f.CamPos);
            Assert.Equal((640, 360), (f.Width, f.Height));
            // Her body is within half a metre of the 3 m she stands at.
            Assert.InRange(nearest, 2.5f, 3.5f);
            Assert.InRange(farthest, 2.5f, 3.5f);
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
