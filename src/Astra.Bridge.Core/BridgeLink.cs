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

        /// <summary>Said by the engine when a refused hello is some OTHER connection already holding
        /// the claim (<c>ws.rs::take_claim</c>) — never a wording a foundation should guess at, so this
        /// is the engine's own text, verbatim.</summary>
        const string AnotherGameHoldsHer = "another game already holds Astra";

        /// <summary>How long to wait before trying again after <see cref="AnotherGameHoldsHer"/>: that
        /// refusal will not change until the OTHER game leaves, so retrying every <c>retryMs</c> (as
        /// short as 250 ms) only spams the engine's log for no chance of a different answer.</summary>
        const int AnotherGameRetryMs = 10000;

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
        readonly HashSet<string> loggedErrors = new HashSet<string>(); // per CONNECTION: cleared on every new one

        /// <summary>How long ago (<see cref="Environment.TickCount"/> ms) the owning game last proved,
        /// through <see cref="MarkWanted"/>, that it is alive and still wants this link. Read on the
        /// link's OWN thread, on ITS OWN clock — never Unity's <c>Time</c>, which stalls WITH the main
        /// thread: a frozen game must still be detectable from here.</summary>
        volatile int wantedAtMs;

        /// <summary>How long a missing heartbeat is tolerated before the link stops (re)connecting.
        /// Shorter than the engine's own "let her go" timeout (8 s, <c>BRIDGE.md</c>) so a stalled game
        /// does not re-claim her the instant the engine's timeout fires.</summary>
        const int WantedTimeoutMs = 2000;

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
            // Whatever a config file holds (L3): a value outside TCP's range must never reach a
            // socket call.
            this.port = Math.Max(1, Math.Min(65535, port));
            this.token = token;
            this.retryMs = Math.Max(250, retryMs);
            this.host = host;
            // Nothing has proved it is alive yet: wait for a MarkWanted() before ever connecting.
            wantedAtMs = unchecked(Environment.TickCount - WantedTimeoutMs - 1);
        }

        /// <summary>The engine answered our hello and the socket is open.</summary>
        public bool Ready => ready;

        /// <summary>The (clamped) port this link connects to.</summary>
        public int Port => port;

        /// <summary>Tell the link its owning game is alive and still wants her — call this once a
        /// frame (<c>LateUpdate</c>). The link only connects or reconnects while a call landed within
        /// the last <see cref="WantedTimeoutMs"/>: see <see cref="wantedAtMs"/>.</summary>
        public void MarkWanted() => wantedAtMs = Environment.TickCount;

        bool Wanted => unchecked(Environment.TickCount - wantedAtMs) < WantedTimeoutMs;

        /// <summary>The current session's hello reply (null before the first).</summary>
        public HelloReply Hello => hello;

        /// <summary>Which of her shadows this game reads, said in every hello (<see cref="Messages.Hello(string, string, string)"/>);
        /// null = not said. Read fresh on every (re)connect, like <see cref="Game"/>.</summary>
        public string Shadow { get; set; }

        /// <summary>Ask for her rectangle of each picture only (<c>hello.crop</c>, flag 128). Read fresh
        /// on every (re)connect.</summary>
        public bool Crop { get; set; }

        /// <summary>This game's display name (<c>hello.game</c>); null = not said (the engine derives
        /// one from <see cref="client"/>). Read fresh on every (re)connect.</summary>
        public string Game { get; set; }

        /// <summary>This foundation's version (<c>hello.foundation</c>); null = not said.</summary>
        public string Foundation { get; set; }

        /// <summary>The game's integration id, when one is registered (<c>hello.integration</c>);
        /// null = none.</summary>
        public string Integration { get; set; }

        /// <summary>Bumped on every engine hello: a new session — map the ring again.</summary>
        public int Session => Volatile.Read(ref session);

        /// <summary>The last <c>error</c> the engine sent, or the last connection failure.</summary>
        public string LastError { get; private set; }

        /// <summary>Does the connected engine declare <paramref name="cap"/> (<c>hello.caps</c>)? True
        /// when no hello has arrived yet, or it named no <c>caps</c> at all (an engine from before
        /// capability negotiation): the foundation then keeps sending what it always sent.</summary>
        public bool Supports(string cap) => HelloReply.Supports(hello?.Caps, cap);

        /// <summary>The <c>shadow</c> word to actually send: an engine that never said <c>caps</c>
        /// gets today's behaviour unchanged (it may be old enough to REFUSE an unknown word outright);
        /// a known engine that does not list <c>"caster"</c> is sent nothing, which it takes for the
        /// sun view — never a word it might not understand.</summary>
        string EffectiveShadow()
        {
            string shadow = Shadow;
            if (string.IsNullOrEmpty(shadow) || shadow == "sun") return shadow;
            return Supports("caster") ? shadow : null;
        }

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
                if (!Wanted)
                {
                    // No heartbeat recently (switched off, or the main thread has stalled): do not
                    // connect or reconnect, quietly — this is not a fault.
                    stopping.WaitOne(retryMs);
                    continue;
                }
                WebSocketClient socket = null;
                int wait = retryMs;
                try
                {
                    socket = WebSocketClient.Connect(host, port, "/", 2000);
                    ws = socket;
                    loggedErrors.Clear(); // a new connection: an engine error is worth saying again
                    LastError = null;
                    if (!socket.SendText(Messages.Hello(client, token, EffectiveShadow(), Game, Foundation, Integration, Crop)))
                        throw new IOException("closed before hello");
                    State($"connected to Astra on {host}:{port}");
                    while (true)
                    {
                        string text = socket.Receive();
                        if (text == null) break;
                        Handle(text);
                    }
                    State("Astra closed the connection" + (LastError != null ? $" ({LastError})" : ""));
                    // That refusal will not change until the other game lets go: retrying every
                    // retryMs (as fast as 250 ms) only spams Astra's log for nothing.
                    if (LastError == AnotherGameHoldsHer) wait = AnotherGameRetryMs;
                }
                catch (IOException e)
                {
                    LastError = e.Message;
                    State($"Astra is not reachable on {host}:{port} ({e.Message}); retrying");
                }
                catch (Exception e)
                {
                    // Nothing may escape this thread (L3): an uncaught exception here crashes an
                    // IL2CPP game outright, for a link that is meant to fail soft and retry.
                    LastError = e.Message;
                    State($"the link to Astra faulted ({e.GetType().Name}: {e.Message}); retrying");
                }
                finally
                {
                    ready = false;
                    ws = null;
                    socket?.Dispose();
                }
                stopping.WaitOne(wait);
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
                // Once per distinct engine error text per connection: a game that keeps sending
                // something the engine refuses must not flood the log at frame rate.
                if (loggedErrors.Add(LastError)) Log?.Invoke($"Astra refused a message: {LastError}");
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
