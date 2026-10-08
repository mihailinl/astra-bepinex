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

/// <summary>
/// <see cref="BridgeLink"/>'s own rules, against a raw <see cref="TcpListener"/> (no WebSocket
/// handshake needed to prove whether it even attempted a connection).
/// </summary>
public class BridgeLinkTests
{
    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>B1: a link that was never told the main thread is alive must never connect — a
    /// foundation that cannot draw her must never hold her — and it connects promptly once told.</summary>
    [Fact]
    public async Task the_link_does_not_connect_until_marked_wanted()
    {
        int port = FreePort();
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        try
        {
            var accept = l.AcceptTcpClientAsync();
            using var link = new BridgeLink("test", port, retryMs: 150);
            link.Start();
            try
            {
                var early = await Task.WhenAny(accept, Task.Delay(400));
                Assert.NotSame(accept, early); // no connection before MarkWanted()

                link.MarkWanted();
                var late = await Task.WhenAny(accept, Task.Delay(3000));
                Assert.Same(accept, late); // connected soon after
            }
            finally
            {
                if (accept.IsCompleted) (await accept).Dispose();
            }
        }
        finally
        {
            l.Stop();
        }
    }

    /// <summary>M4: the same engine error text, sent more than once on one connection, is logged only
    /// the first time.</summary>
    [Fact]
    public async Task a_repeated_refusal_is_logged_once_per_connection()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var served = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buf = new byte[4096];
            await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None); // the hello
            for (int i = 0; i < 3; i++)
                await Send(ws, "{\"t\":\"error\",\"msg\":\"nope\"}");
            await Send(ws, "{\"t\":\"error\",\"msg\":\"nope again\"}");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        });

        var lines = new List<string>();
        using var link = new BridgeLink("test", port, retryMs: 2000);
        link.Log = s => { lock (lines) lines.Add(s); };
        link.MarkWanted();
        link.Start();
        await Task.WhenAny(served, Task.Delay(5000));
        // Give the receive thread a moment to drain what the server already queued and close.
        for (int i = 0; i < 50 && !lines.Exists(l => l.Contains("nope again")); i++) await Task.Delay(50);
        http.Stop();

        lock (lines)
        {
            // Exclude the unrelated "Astra closed the connection (…)" status line, which echoes
            // whatever LastError happened to hold last — only the refusal line itself is counted.
            Assert.Equal(1, lines.FindAll(l => l == "Astra refused a message: nope").Count);
            Assert.Equal(1, lines.FindAll(l => l == "Astra refused a message: nope again").Count);
        }
    }

    static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
}
