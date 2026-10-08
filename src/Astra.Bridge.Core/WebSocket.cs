// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Bridge
{
    /// <summary>
    /// The smallest WebSocket client the bridge needs (RFC 6455: text frames, ping/pong, close),
    /// over a plain <see cref="TcpClient"/>. Not <c>ClientWebSocket</c>: its implementation differs
    /// between the runtimes a game may ship (old Mono, Unity's Mono, IL2CPP's CoreCLR), and the
    /// engine REFUSES a handshake that carries an <c>Origin</c> header — this one never sends it.
    /// <para>
    /// One thread receives (<see cref="Receive"/>); any thread may send (<see cref="SendText"/> is
    /// locked). Nagle is off: a camera message must leave NOW, not with the next one.
    /// </para>
    /// </summary>
    public sealed class WebSocketClient : IDisposable
    {
        const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        /// <summary>A message larger than this is a protocol error (the engine's replies are tiny).</summary>
        public const int MaxMessage = 1 << 20;

        readonly TcpClient tcp;
        readonly NetworkStream stream;
        readonly object sendLock = new object();
        readonly Random maskRng = new Random();
        byte[] sendBuf = new byte[1024];
        byte[] encodeBuf = new byte[1024]; // SendText's reusable UTF-8 scratch (M5)

        // Receive side: bytes read past the handshake are kept here first.
        readonly byte[] head = new byte[14];
        byte[] pending = new byte[0];
        int pendingAt;

        WebSocketClient(TcpClient tcp)
        {
            this.tcp = tcp;
            stream = tcp.GetStream();
        }

        /// <summary>Connect and complete the handshake.</summary>
        /// <exception cref="IOException">Refused, timed out, or not a WebSocket server.</exception>
        public static WebSocketClient Connect(string host, int port, string path, int timeoutMs)
        {
            var tcp = new TcpClient { NoDelay = true };
            try
            {
                var connect = tcp.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
                    throw new IOException($"no answer from {host}:{port} in {timeoutMs} ms");
                tcp.EndConnect(connect);
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;
                var ws = new WebSocketClient(tcp);
                ws.Handshake(host, port, path);
                tcp.ReceiveTimeout = 0; // the receive thread blocks until a message or the close
                // Sends happen on the GAME's thread, once a frame: an engine that stops reading must
                // cost a frame, not seconds. A send that times out ends the connection (BridgeLink).
                tcp.SendTimeout = 250;
                return ws;
            }
            catch (SocketException e)
            {
                tcp.Close();
                throw new IOException(e.Message, e);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        void Handshake(string host, int port, string path)
        {
            var nonce = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            string key = Convert.ToBase64String(nonce);
            string request =
                $"GET {path} HTTP/1.1\r\n" +
                $"Host: {host}:{port}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(request);
            stream.Write(bytes, 0, bytes.Length);

            // Read the response head up to the blank line; whatever follows it is frame data.
            var buf = new byte[4096];
            int have = 0, end = -1;
            while (end < 0)
            {
                if (have == buf.Length) throw new IOException("handshake response too long");
                int n = stream.Read(buf, have, buf.Length - have);
                if (n <= 0) throw new IOException("closed during the handshake");
                have += n;
                for (int i = 3; i < have; i++)
                    if (buf[i - 3] == '\r' && buf[i - 2] == '\n' && buf[i - 1] == '\r' && buf[i] == '\n') { end = i + 1; break; }
            }
            string response = Encoding.ASCII.GetString(buf, 0, end);
            string[] lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (!lines[0].StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                throw new IOException($"refused: {lines[0]}");
            string expect;
            using (var sha = SHA1.Create())
                expect = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + AcceptGuid)));
            bool accepted = false;
            foreach (var line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                if (!line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase)) continue;
                accepted = line.Substring(colon + 1).Trim() == expect;
            }
            if (!accepted) throw new IOException("bad Sec-WebSocket-Accept");
            pending = new byte[have - end];
            Buffer.BlockCopy(buf, end, pending, 0, pending.Length);
            pendingAt = 0;
        }

        /// <summary>Send one text message. False when the connection is gone (never throws). Encodes
        /// into a reusable buffer (M5) — a camera message goes out at frame rate, and
        /// <see cref="Encoding.GetBytes(string)"/> allocated a fresh array every single time.</summary>
        public bool SendText(string text)
        {
            if (text == null) return Send(0x1, null, 0);
            lock (sendLock)
            {
                int max = Encoding.UTF8.GetMaxByteCount(text.Length);
                if (encodeBuf.Length < max) encodeBuf = new byte[Math.Max(max, encodeBuf.Length * 2)];
                int len = Encoding.UTF8.GetBytes(text, 0, text.Length, encodeBuf, 0);
                return Send(0x1, encodeBuf, len);
            }
        }

        bool Send(byte opcode, byte[] payload, int len)
        {
            lock (sendLock)
            {
                int headLen = len < 126 ? 2 : len <= 0xFFFF ? 4 : 10;
                int total = headLen + 4 + len;
                if (sendBuf.Length < total) sendBuf = new byte[Math.Max(total, sendBuf.Length * 2)];
                var b = sendBuf;
                b[0] = (byte)(0x80 | opcode);
                if (len < 126)
                {
                    b[1] = (byte)(0x80 | len);
                }
                else if (len <= 0xFFFF)
                {
                    b[1] = 0x80 | 126;
                    b[2] = (byte)(len >> 8);
                    b[3] = (byte)len;
                }
                else
                {
                    b[1] = 0x80 | 127;
                    for (int i = 0; i < 8; i++) b[2 + i] = (byte)((long)len >> (56 - 8 * i));
                }
                // A client MUST mask (RFC 6455 §5.3); loopback has no proxy to poison, so a fast PRNG will do.
                int m = maskRng.Next();
                b[headLen] = (byte)m;
                b[headLen + 1] = (byte)(m >> 8);
                b[headLen + 2] = (byte)(m >> 16);
                b[headLen + 3] = (byte)(m >> 24);
                for (int i = 0; i < len; i++) b[headLen + 4 + i] = (byte)(payload[i] ^ b[headLen + (i & 3)]);
                try
                {
                    stream.Write(b, 0, total);
                    return true;
                }
                catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is SocketException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Block until the next text message. Answers pings, ignores pongs and binary messages.
        /// Null when the connection closed (cleanly or not).
        /// </summary>
        public string Receive()
        {
            var message = new MemoryStream();
            byte messageOp = 0;
            try
            {
                while (true)
                {
                    if (!ReadExact(head, 0, 2)) return null;
                    bool fin = (head[0] & 0x80) != 0;
                    byte op = (byte)(head[0] & 0x0F);
                    bool masked = (head[1] & 0x80) != 0;
                    long len = head[1] & 0x7F;
                    if (len == 126)
                    {
                        if (!ReadExact(head, 2, 2)) return null;
                        len = (head[2] << 8) | head[3];
                    }
                    else if (len == 127)
                    {
                        if (!ReadExact(head, 2, 8)) return null;
                        len = 0;
                        for (int i = 0; i < 8; i++) len = (len << 8) | head[2 + i];
                    }
                    if (len < 0 || len > MaxMessage || message.Length + len > MaxMessage) return Fail();
                    var key = new byte[4];
                    if (masked && !ReadExact(key, 0, 4)) return null;
                    var payload = new byte[len];
                    if (!ReadExact(payload, 0, (int)len)) return null;
                    if (masked)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= key[i & 3];

                    switch (op)
                    {
                        case 0x8: // close: echo it, then we are done
                            Send(0x8, payload.Length >= 2 ? new[] { payload[0], payload[1] } : null, payload.Length >= 2 ? 2 : 0);
                            return null;
                        case 0x9:
                            Send(0xA, payload, payload.Length);
                            continue;
                        case 0xA:
                            continue;
                        case 0x0:
                            if (messageOp == 0) return Fail();
                            break;
                        case 0x1:
                        case 0x2:
                            if (messageOp != 0) return Fail();
                            messageOp = op;
                            break;
                        default:
                            return Fail();
                    }
                    message.Write(payload, 0, payload.Length);
                    if (!fin) continue;
                    byte done = messageOp;
                    messageOp = 0;
                    if (done == 0x1) return Encoding.UTF8.GetString(message.ToArray());
                    message.SetLength(0); // a binary message is not part of the protocol: drop it
                }
            }
            catch (Exception e) when (e is IOException || e is ObjectDisposedException || e is SocketException)
            {
                return null;
            }
        }

        string Fail()
        {
            Dispose();
            return null;
        }

        bool ReadExact(byte[] buf, int at, int count)
        {
            while (count > 0 && pendingAt < pending.Length)
            {
                buf[at++] = pending[pendingAt++];
                count--;
            }
            while (count > 0)
            {
                int n = stream.Read(buf, at, count);
                if (n <= 0) return false;
                at += n;
                count -= n;
            }
            return true;
        }

        /// <summary>Say goodbye (best effort) and close the socket.</summary>
        public void Close()
        {
            Send(0x8, new byte[] { 0x03, 0xE8 }, 2); // 1000: normal closure
            Dispose();
        }

        public void Dispose()
        {
            try { tcp.Close(); }
            catch (Exception) { /* already gone */ }
        }
    }
}
