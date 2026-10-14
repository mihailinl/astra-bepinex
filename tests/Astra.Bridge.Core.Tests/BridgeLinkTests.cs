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

    /// <summary>L3: whatever a config file holds, the socket is never asked to connect to a port
    /// outside TCP's own range.</summary>
    [Fact]
    public void the_port_is_clamped_to_a_valid_tcp_range()
    {
        Assert.Equal(1, new BridgeLink("t", 0).Port);
        Assert.Equal(1, new BridgeLink("t", -5).Port);
        Assert.Equal(65535, new BridgeLink("t", 99999).Port);
        Assert.Equal(25600, new BridgeLink("t", 25600).Port);
    }

    /// <summary>L3: a refusal saying another game already holds her is not worth retrying every
    /// retryMs — that answer will not change until the other game lets go.</summary>
    [Fact]
    public async Task a_refusal_that_another_game_holds_her_backs_off_for_a_while()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        int connections = 0;
        var serverLoop = Task.Run(async () =>
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await http.GetContextAsync(); }
                catch (Exception) { return; }
                Interlocked.Increment(ref connections);
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var buf = new byte[4096];
                await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None); // the hello
                await Send(ws, "{\"t\":\"error\",\"msg\":\"another game already holds Astra\"}");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        });

        // retryMs is far shorter than the 10 s backoff, so a reconnect inside this window proves the
        // ordinary retry ran instead of the backoff.
        using var link = new BridgeLink("test", port, retryMs: 200);
        link.MarkWanted();
        link.Start();
        await Task.Delay(1500);
        http.Stop();
        try { await serverLoop; } catch (Exception) { /* the listener was stopped under it */ }

        Assert.Equal(1, Volatile.Read(ref connections));
    }

    /// <summary>hello.params and hello.caps go out in the hello; the engine's warn messages come back
    /// through the Warn sink, each distinct one once.</summary>
    [Fact]
    public async Task the_hello_declares_what_it_sends_and_each_warning_is_said_once()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        string hello = null;
        var served = Task.Run(async () =>
        {
            var ctx = await http.GetContextAsync();
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            hello = await Receive(ws);
            await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"e\",\"ver\":\"0\",\"shm\":\"x\",\"maxW\":8,\"maxH\":8,\"caps\":[\"hello.params\",\"warn\"]}");
            for (int i = 0; i < 3; i++)
                await Send(ws, "{\"t\":\"warn\",\"code\":\"param_undeclared\",\"name\":\"climbing\"}");
            // The foundation's own measured parameter: nothing a reader of the console can act on.
            await Send(ws, "{\"t\":\"warn\",\"code\":\"param_undeclared\",\"name\":\"speed\"}");
            await Send(ws, "{\"t\":\"warn\",\"code\":\"state_unknown\",\"name\":\"seat\"}");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        });

        var warned = new List<string>();
        var logged = new List<string>();
        using var link = new BridgeLink("test", port, retryMs: 2000);
        link.Log = s => { lock (logged) logged.Add(s); };
        link.Warn = s => { lock (warned) warned.Add(s); };
        link.Integration = "astra.test";
        link.Params = () => new[] { ParamDecl.Speed, ParamDecl.Airborne, new ParamDecl("climbing", ParamType.Bool) };
        link.MarkWanted();
        link.Start();
        await Task.WhenAny(served, Task.Delay(5000));
        for (int i = 0; i < 50 && !warned.Exists(l => l.Contains("'seat'")); i++) await Task.Delay(50);
        http.Stop();

        Assert.Contains("\"caps\":[\"warn\"]", hello);
        Assert.Contains("\"params\":[{\"name\":\"speed\",\"type\":\"float\"},{\"name\":\"airborne\",\"type\":\"bool\"},{\"name\":\"climbing\",\"type\":\"bool\"}]", hello);
        lock (warned)
        {
            Assert.Equal(2, warned.Count);
            Assert.Single(warned, l => l.Contains("'climbing'"));
            Assert.Single(warned, l => l.Contains("'seat'"));
        }
        lock (logged) Assert.DoesNotContain(logged, l => l.Contains("warns"));
    }

    /// <summary>An engine whose hello listed caps WITHOUT hello.params is not sent them on the next
    /// connection; one whose caps are not known yet is (an older engine ignores the field).</summary>
    [Fact]
    public async Task an_engine_that_does_not_take_declarations_is_not_sent_them_again()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var hellos = new List<string>();
        var served = Task.Run(async () =>
        {
            for (int n = 0; n < 2; n++)
            {
                var ctx = await http.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var text = await Receive(ws);
                lock (hellos) hellos.Add(text);
                await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"e\",\"ver\":\"0\",\"shm\":\"x\",\"maxW\":8,\"maxH\":8,\"caps\":[\"warn\"]}");
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        });

        using var link = new BridgeLink("test", port, retryMs: 250);
        link.Params = () => new[] { ParamDecl.Speed };
        link.MarkWanted();
        link.Start();
        for (int i = 0; i < 60 && !served.IsCompleted; i++)
        {
            link.MarkWanted();
            await Task.Delay(50);
        }
        http.Stop();

        lock (hellos)
        {
            Assert.Equal(2, hellos.Count);
            Assert.Contains("\"params\":", hellos[0]);
            Assert.DoesNotContain("\"params\":", hellos[1]);
            Assert.Contains("\"caps\":[\"warn\"]", hellos[1]);
        }
    }

    /// <summary>F3: the declarations are ASKED at every hello, never copied once: a parameter declared
    /// between two connections is in the second hello, and the first hello marks them said.</summary>
    [Fact]
    public async Task each_hello_says_the_declarations_as_they_are_then()
    {
        int port = FreePort();
        var http = new HttpListener();
        http.Prefixes.Add($"http://127.0.0.1:{port}/");
        http.Start();
        var hellos = new List<string>();
        var firstSaid = new TaskCompletionSource<bool>();
        var declaredMore = new TaskCompletionSource<bool>();
        var declarations = new ParamDeclarations(ParamDecl.Speed, ParamDecl.Airborne);
        declarations.Declare("climbing", ParamType.Bool);
        var served = Task.Run(async () =>
        {
            for (int n = 0; n < 2; n++)
            {
                var ctx = await http.GetContextAsync();
                var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                var text = await Receive(ws);
                lock (hellos) hellos.Add(text);
                await Send(ws, "{\"t\":\"hello\",\"v\":1,\"engine\":\"e\",\"ver\":\"0\",\"shm\":\"x\",\"maxW\":8,\"maxH\":8,\"caps\":[\"hello.params\",\"warn\"]}");
                if (n == 0)
                {
                    firstSaid.SetResult(true);
                    await declaredMore.Task; // the game declares one more while connected
                }
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
        });

        using var link = new BridgeLink("test", port, retryMs: 250);
        link.Params = () => declarations.ForHello();
        link.MarkWanted();
        link.Start();
        await Task.WhenAny(firstSaid.Task, Task.Delay(5000));
        Assert.True(declarations.Said);
        Assert.True(declarations.Declare("depth", ParamType.Float)); // late: the caller warns
        declaredMore.SetResult(true);
        for (int i = 0; i < 60 && !served.IsCompleted; i++)
        {
            link.MarkWanted();
            await Task.Delay(50);
        }
        http.Stop();

        lock (hellos)
        {
            Assert.Equal(2, hellos.Count);
            Assert.Contains("\"climbing\"", hellos[0]);
            Assert.DoesNotContain("\"depth\"", hellos[0]);
            Assert.Contains("{\"name\":\"climbing\",\"type\":\"bool\"},{\"name\":\"depth\",\"type\":\"float\"}", hellos[1]);
        }
    }

    static async Task<string> Receive(WebSocket ws)
    {
        var buf = new byte[8192];
        var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
        return Encoding.UTF8.GetString(buf, 0, r.Count);
    }

    static Task Send(WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
}
