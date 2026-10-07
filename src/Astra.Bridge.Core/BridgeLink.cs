// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Astra.Bridge
{
    /// <summary>
    /// The mod's connection to Astra: connects, says <c>hello</c>, and reconnects whenever the engine
    /// goes away (Astra not running yet, restarted, another game holding her). A background thread
    /// owns the socket's receive side; the game's thread sends.
    /// <para>
    /// The game polls <see cref="Session"/>: it changes on every new engine hello, and that is when
    /// the frame ring must be (re)mapped from <see cref="Hello"/>.
    /// </para>
    /// </summary>
    public sealed class BridgeLink : IDisposable
    {
        public const int DefaultPort = 25600;

        readonly string client;
        readonly string host;
        readonly int port;
        readonly string token;
        readonly int retryMs;
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        Thread thread;

        volatile WebSocketClient ws;
        volatile HelloReply hello;
        volatile bool ready;
        int session;
        string lastState;

        /// <summary>Diagnostics, called on the link's own thread — the sink must be thread-safe.</summary>
        public Action<string> Log;

        /// <param name="client">Your mod's name, shown in Astra's log.</param>
        /// <param name="port">The engine's bridge port.</param>
        /// <param name="token">The engine's <c>WGPU_BRIDGE_TOKEN</c>, when the user set one.</param>
        /// <param name="retryMs">How long to wait before trying again.</param>
        /// <param name="host">Loopback; the engine listens nowhere else.</param>
        public BridgeLink(string client, int port = DefaultPort, string token = null, int retryMs = 2000, string host = "127.0.0.1")
        {
            this.client = client;
            this.port = port;
            this.token = token;
            this.retryMs = Math.Max(250, retryMs);
            this.host = host;
        }

        /// <summary>The engine answered our hello and the socket is open.</summary>
        public bool Ready => ready;

        /// <summary>The current session's hello reply (null before the first).</summary>
        public HelloReply Hello => hello;

        /// <summary>Which of her shadows this game reads, said in every hello (<see cref="Messages.Hello(string, string, string)"/>);
        /// null = not said. Set it before <see cref="Start"/>.</summary>
        public string Shadow { get; set; }

        /// <summary>Bumped on every engine hello: a new session — map the ring again.</summary>
        public int Session => Volatile.Read(ref session);

        /// <summary>The last <c>error</c> the engine sent, or the last connection failure.</summary>
        public string LastError { get; private set; }

        public void Start()
        {
            if (thread != null) return;
            thread = new Thread(Run) { IsBackground = true, Name = "astra-bridge-link" };
            thread.Start();
        }

        /// <summary>Send one message (a <see cref="Messages"/> builder's output). False when not
        /// connected, or when <paramref name="json"/> is null (the builder refused its input).</summary>
        public bool Send(string json)
        {
            if (json == null || !ready) return false;
            var socket = ws;
            if (socket == null) return false;
            if (socket.SendText(json)) return true;
            // A failed (or timed-out, so possibly half-written) send: this connection is done. Close it,
            // so the receive thread returns and the link reconnects, rather than stay "connected" mute.
            ready = false;
            socket.Dispose();
            return false;
        }

        void Run()
        {
            while (!stopping.WaitOne(0))
            {
                WebSocketClient socket = null;
                try
                {
                    socket = WebSocketClient.Connect(host, port, "/", 2000);
                    ws = socket;
                    if (!socket.SendText(Messages.Hello(client, token, Shadow))) throw new IOException("closed before hello");
                    State($"connected to Astra on {host}:{port}");
                    while (true)
                    {
                        string text = socket.Receive();
                        if (text == null) break;
                        Handle(text);
                    }
                    State("Astra closed the connection" + (LastError != null ? $" ({LastError})" : ""));
                }
                catch (IOException e)
                {
                    LastError = e.Message;
                    State($"Astra is not reachable on {host}:{port} ({e.Message}); retrying");
                }
                finally
                {
                    ready = false;
                    ws = null;
                    socket?.Dispose();
                }
                stopping.WaitOne(retryMs);
            }
        }

        void Handle(string text)
        {
            Dictionary<string, object> m;
            try
            {
                m = JsonReader.ReadObject(text);
            }
            catch (FormatException e)
            {
                Log?.Invoke($"unreadable message from Astra ({e.Message}): {Clip(text)}");
                return;
            }
            var reply = HelloReply.From(m);
            if (reply != null)
            {
                if (reply.Version != Messages.ProtocolVersion)
                    Log?.Invoke($"Astra speaks protocol v{reply.Version}, this mod v{Messages.ProtocolVersion}");
                hello = reply;
                LastError = null;
                Interlocked.Increment(ref session);
                ready = true;
                Log?.Invoke($"Astra {reply.EngineVersion}: frames in '{reply.Shm}', up to {reply.MaxWidth}x{reply.MaxHeight}");
                return;
            }
            if (m.TryGetValue("t", out var t) && (t as string) == "error")
            {
                LastError = m.TryGetValue("msg", out var msg) ? msg as string : "error";
                Log?.Invoke($"Astra refused a message: {LastError}");
            }
        }

        /// <summary>Log a connection state once, not every retry.</summary>
        void State(string s)
        {
            if (s == lastState) return;
            lastState = s;
            Log?.Invoke(s);
        }

        static string Clip(string s) => s.Length <= 200 ? s : s.Substring(0, 200) + "…";

        public void Dispose()
        {
            stopping.Set();
            var socket = ws;
            socket?.Close(); // unblocks Receive
            thread?.Join(1000);
            thread = null;
        }
    }
}
