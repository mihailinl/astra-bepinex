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
        var (f, _, _, nearest, farthest, _, _) = Draw(crop: false);
        Assert.InRange(f.HostFrame, 1, long.MaxValue);
        Assert.Equal(42f, f.Echo0);
        Assert.Equal(new Vec3(0, 1, 0), f.CamPos);
        Assert.Equal((640, 360), (f.Width, f.Height));
        Assert.False(f.Cropped);
        // Her body is within half a metre of the 3 m she stands at.
        Assert.InRange(nearest, 2.5f, 3.5f);
        Assert.InRange(farthest, 2.5f, 3.5f);
    }

    /// <summary>With <c>hello.crop</c> the engine sends her rectangle only (flag 128): a fraction of the
    /// view, and every pixel of her the whole picture has lies inside it.</summary>
    [Fact]
    public void a_cropped_picture_holds_all_of_her_in_a_fraction_of_the_view()
    {
        var whole = Draw(crop: false);
        var cut = Draw(crop: true);
        var f = cut.Frame;
        Assert.True(f.Cropped, "flag 128");
        Assert.Equal((640, 360), (f.Width, f.Height));
        Assert.True(f.CropW > 0 && f.CropH > 0, "a rectangle");
        Assert.True(f.CropW % 64 == 0 || f.CropX + f.CropW == f.Width, $"a 64-pixel step: {f.CropW}");
        Assert.True(f.CropW * f.CropH < f.Width * f.Height / 2, $"a fraction of the view: {f.CropW}x{f.CropH}");
        Assert.Equal(4 * f.CropW * f.CropH, f.PlaneBytes);
        // Every pixel of her in the whole picture (any alpha) lies inside the rectangle (the same
        // camera, a moment apart: the margin covers her breathing).
        var (x0, y0, x1, y1) = whole.Opaque;
        Assert.True(f.CropX <= x0 && f.CropY <= y0 && x1 < f.CropX + f.CropW && y1 < f.CropY + f.CropH,
            $"her pixels {x0},{y0}..{x1},{y1} inside {f.CropX},{f.CropY} {f.CropW}x{f.CropH}");
        Assert.InRange(cut.Nearest, 2.5f, 3.5f);
    }

    /// <summary>One engine in game mode on a free port, a camera 1 m up looking at her 3 m away, until
    /// a frame with her in it: that frame, its planes, her depth range, the bounds of every pixel of
    /// her with any alpha (whole-view coordinates) and the count of her opaque ones.</summary>
    static (RingFrame Frame, byte[] Colour, float[] Depth, float Nearest, float Farthest, (int, int, int, int) Opaque, int Count) Draw(bool crop)
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
            using var link = new BridgeLink("astra-bepinex-tests", port, retryMs: 250) { Crop = crop };
            link.Start();
            var until = DateTime.UtcNow.AddSeconds(20);
            // The link lives while it is WANTED (the foundation's main thread says so every frame).
            while (!link.Ready && DateTime.UtcNow < until)
            {
                link.MarkWanted();
                Thread.Sleep(50);
            }
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
            byte[] colour = null;
            float[] depth = null;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            while (DateTime.UtcNow < until && opaque == 0)
            {
                link.MarkWanted();
                Assert.True(link.Send(Messages.Avatar(her, new ParamSet().Set("speed", 0.0))));
                Assert.True(link.Send(Messages.Cam(++id, new Vec3(0, 1, 0), new Vec3(0, 0, -1), new Vec3(0, 1, 0), 60, 640, 360, new double[] { 42 })));
                Thread.Sleep(30);
                if (!ring.TryLatest(ref seen, out f)) continue;
                colour = new byte[f.PlaneBytes];
                depth = new float[f.PlaneBytes / 4];
                System.Runtime.InteropServices.Marshal.Copy(f.Colour, colour, 0, colour.Length);
                System.Runtime.InteropServices.Marshal.Copy(f.Depth, depth, 0, depth.Length);
                if (!ring.StillValid(ref f)) continue;
                int pw = f.Cropped ? f.CropW : f.Width;
                opaque = 0;
                bx0 = by0 = int.MaxValue;
                bx1 = by1 = -1;
                for (int i = 0; i < depth.Length; i++)
                {
                    if (colour[4 * i + 3] == 0) continue;
                    // Every pixel of her, a hair tip's or an edge's too, bounds her; only opaque ones
                    // count as "her picture" and give her depth.
                    if (colour[4 * i + 3] == 255)
                    {
                        opaque++;
                        nearest = Math.Min(nearest, depth[i]);
                        farthest = Math.Max(farthest, depth[i]);
                    }
                    int x = i % pw + (f.Cropped ? f.CropX : 0), y = i / pw + (f.Cropped ? f.CropY : 0);
                    bx0 = Math.Min(bx0, x);
                    by0 = Math.Min(by0, y);
                    bx1 = Math.Max(bx1, x);
                    by1 = Math.Max(by1, y);
                }
            }
            Assert.True(opaque > 1000, $"no picture of her after {id} cameras:\n" + log);
            Assert.InRange(f.HostFrame, 1, id);
            return (f, colour, depth, nearest, farthest, (bx0, by0, bx1, by1), opaque);
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
