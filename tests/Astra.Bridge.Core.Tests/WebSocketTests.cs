// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Astra.Bridge;
using Xunit;

/// <summary>The hand-written client against .NET's own WebSocket server.</summary>
public class WebSocketTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Serve one connection with <paramref name="serve"/>; returns the request headers seen.</summary>
    static (int port, Task<WebHeaderCollection> done) Server(Func<WebSocket, Task> serve)
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var done = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            var headers = new WebHeaderCollection();
            headers.Add(ctx.Request.Headers);
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            await serve(ws);
            http.Stop();
            return headers;
        });
        return (port, done);
    }

    static async Task<string> ReceiveText(WebSocket ws)
    {
        var buf = new byte[1 << 20];
        int have = 0;
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, have, buf.Length - have), CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            have += r.Count;
            if (r.EndOfMessage) return Encoding.UTF8.GetString(buf, 0, have);
        }
    }

    [Fact]
    public async Task text_goes_both_ways_and_no_origin_is_sent()
    {
        var (port, done) = Server(async ws =>
        {
            string got = await ReceiveText(ws);
            await ws.SendAsync(Encoding.UTF8.GetBytes("echo:" + got), WebSocketMessageType.Text, true, CancellationToken.None);
            // A long message (16-bit length) in three fragments.
            var big = new string('x', 70000);
            var bytes = Encoding.UTF8.GetBytes(big);
            await ws.SendAsync(new ArraySegment<byte>(bytes, 0, 100), WebSocketMessageType.Text, false, CancellationToken.None);
            await ws.SendAsync(new ArraySegment<byte>(bytes, 100, 60000), WebSocketMessageType.Text, false, CancellationToken.None);
            await ws.SendAsync(new ArraySegment<byte>(bytes, 60100, bytes.Length - 60100), WebSocketMessageType.Text, true, CancellationToken.None);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        });
        using var c = WebSocketClient.Connect("127.0.0.1", port, "/", 2000);
        Assert.True(c.SendText("привет"));
        Assert.Equal("echo:привет", c.Receive());
        Assert.Equal(70000, c.Receive().Length);
        Assert.Null(c.Receive()); // the close
        var headers = await done;
        Assert.Null(headers["Origin"]);
    }

    [Fact]
    public async Task a_large_message_from_the_client_arrives_whole()
    {
        string got = null;
        var (port, done) = Server(async ws =>
        {
            got = await ReceiveText(ws);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        });
        using var c = WebSocketClient.Connect("127.0.0.1", port, "/", 2000);
        var text = string.Concat(Enumerable.Repeat("0123456789", 7000)); // 70 000 B: past 65 535, so a 64-bit length
        Assert.True(c.SendText(text));
        Assert.Null(c.Receive());
        await done;
        Assert.Equal(text, got);
    }

    [Fact]
    public void a_refused_connection_is_an_io_error()
    {
        Assert.ThrowsAny<System.IO.IOException>(() => WebSocketClient.Connect("127.0.0.1", FreePort(), "/", 1000));
    }
}
