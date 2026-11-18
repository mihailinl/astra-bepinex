// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astra.Bridge;
using Xunit;

/// <summary><c>hello.bones</c> over the real link, against a fake ENGINE (engine #80).</summary>
public class BonesLinkTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>
    /// The link asks <see cref="BridgeLink.Bones"/> at every hello and says the list: the first engine
    /// (its caps not known yet) is told <c>["rightHand"]</c> and answers the list and its revision,
    /// which <see cref="BridgeLink.Hello"/> reads back. That engine lists no <c>bones</c> cap, so the
    /// reconnect's hello names none.
    /// </summary>
    [Fact]
    public async Task the_bones_are_said_in_the_hello_and_their_revision_read_back()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var hellos = new List<string>();
        var second = new TaskCompletionSource<bool>();
        var served = Task.Run(async () =>
        {
            for (int n = 0; n < 2; n++)
            {
                var ctx = await http.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var buf = new byte[65536];
                var hello = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
                lock (hellos) hellos.Add(Encoding.UTF8.GetString(buf, 0, hello.Count));
                if (n == 1)
                {
                    second.TrySetResult(true);
                    break;
                }
                // The first engine streams the hand (revision 17) but lists no "bones" cap: an engine
                // that, by its own caps, does not take the key.
                await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"fake\",\"shm\":\"/dev/shm/none\",\"maxW\":64,\"maxH\":64,"
                    + "\"caps\":[\"warn\"],\"bones\":[\"rightHand\"],\"bones_rev\":17}");
                await Task.Delay(300);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
        });

        int asked = 0;
        using var link = new BridgeLink("test", port, retryMs: 250)
        {
            Bones = () =>
            {
                Interlocked.Increment(ref asked);
                return new[] { "rightHand" };
            },
        };
        link.MarkWanted();
        link.Start();
        for (int i = 0; i < 100 && !link.Ready; i++) await Task.Delay(50);
        Assert.True(link.Ready, "the fake engine answered");
        Assert.Equal(new List<string> { "rightHand" }, link.Hello.Bones);
        Assert.Equal(17, link.Hello.BonesRev);

        for (int i = 0; i < 100 && !second.Task.IsCompleted; i++)
        {
            link.MarkWanted();
            await Task.Delay(50);
        }
        Assert.True(second.Task.IsCompleted, "the link connected again");
        lock (hellos)
        {
            Assert.Contains("\"bones\":[\"rightHand\"]", hellos[0]);
            Assert.DoesNotContain("\"bones\"", hellos[1]);
        }
        Assert.Equal(1, Volatile.Read(ref asked));
        http.Stop();
        await Task.WhenAny(served, Task.Delay(2000));
    }
}
