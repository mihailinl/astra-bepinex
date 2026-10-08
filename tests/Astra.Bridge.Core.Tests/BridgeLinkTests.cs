// SPDX-License-Identifier: MIT
using System;
using System.Net;
using System.Net.Sockets;
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
}
