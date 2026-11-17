// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Astra.Bridge
{
    /// <summary>
    /// The game's side of Astra Link (AstraPlugins <c>spec/games/link-v1.md</c>): the daemon's socket
    /// through which a game integration gives Astra's AI tools to call, tells it what happens in the
    /// game (as facts), fires triggers, and asks for the user's voice key. It is NOT the companion's
    /// bridge (<see cref="BridgeLink"/>), which only draws her: nothing of this channel ever goes there.
    /// <para><b>Off unless Astra says otherwise.</b> It runs only from a link file (<see cref="LinkFile"/>),
    /// which Astra writes on Play for a game integration from the signed catalogue. Without one there is
    /// no client at all (<see cref="TryCreate"/> returns null), and once a session cannot go on (the game
    /// was stopped, the integration withdrawn, the token spent and not resumable) it stops for good,
    /// quietly. Whatever the user has not switched on is not sent.</para>
    /// <para><b>Threads.</b> A background thread owns the socket's receive side, a second one pings and
    /// notices silence. The game's main thread calls <see cref="Pump"/> once a frame: tool calls run
    /// there, and the events it raises (<see cref="GrantsChanged"/>, <see cref="Ended"/>) are raised
    /// there. <see cref="Event"/>, <see cref="Context"/>, <see cref="Trigger"/>, <see cref="Talk"/> and
    /// <see cref="Quiet"/> may be called from any thread. Nothing thrown escapes either background thread
    /// (an uncaught exception there ends an IL2CPP game).</para>
    /// <para><b>Limits</b> are kept here a little under the daemon's, so a message that left on time and
    /// arrived late still counts as on time: under 50 messages a second; events under 2 a second in
    /// bursts of 5, each at most 4 KiB; the context every 1.25 s at most (the newest value wins), at
    /// most 2 KiB; <c>talk</c> every 2.2 s; <c>quiet</c> under twice a second (the newest wins); triggers
    /// 5 a second, so they never crowd out results; a result at most 16 KiB, sent within 10 seconds of its
    /// call or refused <c>busy</c>. When the daemon still answers <c>rate_limited</c>, the context and
    /// <c>quiet</c> are said again, since they are states and a dropped one would stand wrong.</para>
    /// </summary>
    public sealed class LinkClient : IDisposable
    {
        public const string Subprotocol = "astra-link.1";
        public const int ProtocolVersion = 1;

        /// <summary>The closed list of event words in v1 (<c>link-v1.md</c> §5, "Event words"). An event is
        /// sent once, at the moment of the change; a state that lasts is never an event.</summary>
        public static readonly IReadOnlyList<string> EventWords = new[]
        {
            "session_started", "session_ended", "player_hurt", "player_fell", "player_died", "player_revived", "goal_reached",
        };

        public const int MaxMessageBytes = 64 * 1024;
        public const int MaxEventBytes = 4 * 1024;
        public const int MaxContextBytes = 2 * 1024;
        public const int MaxResultBytes = 16 * 1024;

        /// <summary>How long the daemon waits for a result; a call that waited this long, less a second
        /// for the way back, is refused <c>busy</c> without running.</summary>
        public const double CallTimeoutSeconds = 10;

        /// <summary>The waits the protocol sets, kept here so a test can shorten them.</summary>
        public sealed class Timings
        {
            public int PingEveryMs = 10000;
            public int SilenceEndsMs = 30000;
            public int WelcomeWithinMs = 5000;
            public int ConnectTimeoutMs = 2000;
            public int RetryMinMs = 1000;
            public int RetryMaxMs = 30000;
            /// <summary>Refused resumes in a row before the link file is read again for a new token.</summary>
            public int ResumeTries = 3;
            /// <summary>How often the keeper thread looks.</summary>
            public int KeeperMs = 250;
        }

        readonly string profileDir;
        readonly string integration;
        readonly string client;
        readonly Func<double> clock;
        readonly Func<long> unixMs;
        readonly Func<string, string> env;
        readonly Timings timings;
        readonly ManualResetEvent stopping = new ManualResetEvent(false);
        Thread thread, keeper;

        LinkFile file;
        volatile WebSocketClient ws;
        volatile bool live;
        volatile bool over;
        volatile LinkGrants grants = LinkGrants.None;
        volatile string resume;
        volatile string spentToken;
        int session;
        int pendingCalls;
        volatile int connectedTick, pingedTick;
        string lastState;
        readonly HashSet<string> said = new HashSet<string>();

        // Rates and what waits for one: held under rateLock. A flush builds and sends under flushLock, so
        // two threads never put the context or quiet on the wire in the wrong order.
        readonly object rateLock = new object(), flushLock = new object();
        readonly Bucket overall = new Bucket(10, 38), events = new Bucket(5, 1.8), context = new Bucket(1, 0.8),
            talk = new Bucket(1, 0.45), quiet = new Bucket(1, 1.8), triggers = new Bucket(5, 5);
        int maxMessageBytes = MaxMessageBytes;
        LinkData lastContext;
        bool contextDue;
        bool quietOn, quietDue, quietSaid;

        enum InKind { Call, Welcome, Grants, Ended }

        struct Inbound
        {
            public InKind Kind;
            public int Session;
            public string Id, Tool, Text;
            public LinkArgs Args;
            public double At;
        }

        readonly ConcurrentQueue<Inbound> inbox = new ConcurrentQueue<Inbound>();
        readonly Dictionary<string, Func<LinkArgs, LinkResult>> tools = new Dictionary<string, Func<LinkArgs, LinkResult>>();

        /// <summary>Diagnostics, from any of the client's threads — the sink must be thread-safe.</summary>
        public Action<string> Log;

        /// <summary>Raised in <see cref="Pump"/> when a session starts and whenever the user changes what
        /// is granted (a switch on the game's row): stop what is no longer granted at once.</summary>
        public event Action<LinkGrants> GrantsChanged;

        /// <summary>Raised in <see cref="Pump"/> when Astra ends the session for good (<c>end</c>), with its
        /// reason: <c>withdrawn</c>, <c>stopped</c>, <c>replaced</c>, <c>game_exited</c>, <c>catalogue_stale</c>.</summary>
        public event Action<string> Ended;

        LinkClient(LinkFile file, string profileDir, string integration, string client, Timings timings, Func<double> clock, Func<long> unixMs, Func<string, string> env)
        {
            this.file = file;
            this.profileDir = profileDir;
            this.integration = integration;
            this.client = client ?? "astra-bepinex";
            this.timings = timings ?? new Timings();
            if (clock == null)
            {
                var watch = Stopwatch.StartNew();
                clock = () => watch.Elapsed.TotalSeconds;
            }
            this.clock = clock;
            this.unixMs = unixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            this.env = env;
        }

        /// <summary>
        /// A client for <paramref name="integration"/> when Astra wrote a usable link file for it
        /// (<see cref="LinkFile.Locate"/> in <paramref name="profileDir"/>), else null — and
        /// <paramref name="why"/> says why, for one line in the log (null when there is simply no file).
        /// Never throws. Not started: call <see cref="Start"/>.
        /// </summary>
        /// <param name="profileDir">The game's Astra profile (the folder holding <c>BepInEx</c>).</param>
        /// <param name="integration">The integration's id (<c>[integration] id</c>); the file must be for it.</param>
        /// <param name="client">This mod's name and version, said in <c>hello</c>.</param>
        /// <param name="why">Why there is no client although a file was found; null otherwise.</param>
        /// <param name="timings">The protocol's waits (tests shorten them); null = the protocol's own.</param>
        /// <param name="clock">Seconds, for the rates; null = a stopwatch.</param>
        /// <param name="unixMs">Wall-clock ms, for the file's expiry; null = the system clock.</param>
        /// <param name="env">Reads an environment variable; null = the process's own.</param>
        public static LinkClient TryCreate(string profileDir, string integration, string client, out string why,
            Timings timings = null, Func<double> clock = null, Func<long> unixMs = null, Func<string, string> env = null)
        {
            why = null;
            try
            {
                string path = LinkFile.Locate(profileDir, env);
                if (path == null) return null;
                if (string.IsNullOrEmpty(integration))
                {
                    why = "Astra Link needs the integration's id: no client";
                    return null;
                }
                long now = (unixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))();
                var f = LinkFile.Read(path, integration, now, out why);
                return f == null ? null : new LinkClient(f, profileDir, integration, client, timings, clock, unixMs, env);
            }
            catch (Exception e)
            {
                why = $"Astra Link: the link file could not be taken ({e.GetType().Name}: {e.Message})";
                return null;
            }
        }

        /// <summary>A session is live: Astra answered <c>hello</c> and nothing has ended it since.</summary>
        public bool Live => live;

        /// <summary>What is granted now (<see cref="LinkGrants.None"/> while no session is live).</summary>
        public LinkGrants Grants => live ? grants : LinkGrants.None;

        /// <summary>The session ended for good, or no session can be had: the client does nothing more.</summary>
        public bool Over => over;

        /// <summary>The daemon's key for this game, from the link file (null when it named none).</summary>
        public string Game => file?.Game;

        /// <summary>Bumped on every new session.</summary>
        public int Session => Volatile.Read(ref session);

        /// <summary>Tool calls that arrived and wait for <see cref="Pump"/>.</summary>
        public int PendingCalls => Volatile.Read(ref pendingCalls);

        bool Stopped => over || stopping.WaitOne(0);

        /// <summary>Answer the tool the integration declared as <paramref name="name"/> with
        /// <paramref name="handler"/>, called on the main thread in <see cref="Pump"/>. A second handler for
        /// a name replaces the first.</summary>
        public void Tool(string name, Func<LinkArgs, LinkResult> handler)
        {
            if (!LinkRefusal.IsCode(name)) throw new ArgumentException($"'{name}' is not a tool name (^[a-z][a-z0-9_]{{0,31}}$)", nameof(name));
            lock (tools) tools[name] = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Start()
        {
            if (thread != null || Stopped) return;
            thread = new Thread(Run) { IsBackground = true, Name = "astra-link" };
            keeper = new Thread(Keep) { IsBackground = true, Name = "astra-link-keeper" };
            thread.Start();
            keeper.Start();
        }

        // ---- what the game sends -------------------------------------------------------------

        /// <summary>
        /// Tell Astra that <paramref name="word"/> just happened (one of <see cref="EventWords"/>, once, at
        /// the change). False — and nothing sent — when no session is live, the user has not granted that
        /// event, the word is not a v1 word, the data is too large, or the rate is spent.
        /// </summary>
        public bool Event(string word, LinkData data = null)
        {
            if (!IsEventWord(word))
            {
                SayOnce("word:" + word, $"'{word}' is not an Astra Link v1 event word ({string.Join(", ", EventWords)}): not sent");
                return false;
            }
            var g = grants;
            if (!live || !g.HasEvent(word)) return false;
            string json = Message("event", word, data);
            if (Bytes(json) > MaxEventBytes)
            {
                SayOnce("big-event:" + word, $"the event '{word}' is larger than {MaxEventBytes} bytes: not sent");
                return false;
            }
            return Send(json, events, "events");
        }

        /// <summary>
        /// What the game is like now, as facts under the integration's declared context schema; it
        /// replaces the last value. Sent every 1.25 s at most — a newer value waiting for the rate
        /// replaces the waiting one — and again at the start of every session. False when it is too large.
        /// </summary>
        public bool Context(LinkData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            var copy = data.Copy();
            if (Bytes(Message("context", null, copy)) > MaxContextBytes)
            {
                SayOnce("big-context", $"the context is larger than {MaxContextBytes} bytes: not sent");
                return false;
            }
            lock (rateLock)
            {
                lastContext = copy;
                contextDue = true;
            }
            FlushContext();
            return true;
        }

        /// <summary>Fire the trigger the integration declared as <paramref name="name"/> (automations the
        /// user bound to it run). False when not granted, not live, or the rate is spent.</summary>
        public bool Trigger(string name, LinkData data = null)
        {
            var g = grants;
            if (!live || !g.HasTrigger(name)) return false;
            string json = Message("trigger", name, data ?? new LinkData());
            if (Bytes(json) > maxMessageBytes) return false;
            return Send(json, triggers, "triggers");
        }

        /// <summary>The player pressed the integration's voice key on the companion: Astra opens the
        /// user's voice input as if they had pressed their own key. Every 2.2 seconds at most; false when
        /// not granted (the integration declares <c>talk</c>, the user switched "AI may act" on).</summary>
        public bool Talk()
        {
            var g = grants;
            if (!live || !g.Talk) return false;
            return Send("{\"t\":\"talk\"}", talk, "talk");
        }

        /// <summary>The game is in a menu, a cutscene or paused (<paramref name="on"/>), or play resumed:
        /// Astra does not speak on her own meanwhile. Sent when it changes (under twice a second, the newest
        /// wins) and again at the start of a session while on.</summary>
        public void Quiet(bool on)
        {
            lock (rateLock)
            {
                if (quietOn == on && !quietDue) return;
                quietOn = on;
                quietDue = true;
            }
            FlushQuiet();
        }

        /// <summary>
        /// Call once a frame on the game's main thread: runs the tool calls that arrived, raises
        /// <see cref="GrantsChanged"/> and <see cref="Ended"/>, and sends a context or quiet that was
        /// waiting for its rate.
        /// </summary>
        public void Pump()
        {
            while (inbox.TryDequeue(out var m))
            {
                switch (m.Kind)
                {
                    case InKind.Call:
                        Interlocked.Decrement(ref pendingCalls);
                        RunCall(m);
                        break;
                    case InKind.Welcome:
                    case InKind.Grants:
                        // A new session knows nothing yet, and a switch turned on again knows nothing either:
                        // say the context and a standing quiet again.
                        SayStatesAgain(refused: false);
                        Raise(GrantsChanged, grants, "GrantsChanged");
                        break;
                    case InKind.Ended:
                        Raise(Ended, m.Text, "Ended");
                        break;
                }
            }
            FlushContext();
            FlushQuiet();
        }

        /// <summary>Say the context and quiet again: for a new session (which takes quiet as off until
        /// told), or after the daemon <paramref name="refused"/> a message for its rate (then quiet is said
        /// again whichever way it stands, once it was said at all: a dropped "off" would keep her silent).</summary>
        void SayStatesAgain(bool refused)
        {
            lock (rateLock)
            {
                if (lastContext != null) contextDue = true;
                quietDue = quietDue || quietOn || (refused && quietSaid);
            }
        }

        void RunCall(Inbound m)
        {
            if (m.Session != Session || !live) return; // its session is gone: nobody waits for the answer
            if (clock() - m.At > CallTimeoutSeconds - 1)
            {
                Reply(m.Id, LinkResult.Refuse(LinkRefusal.Busy));
                return;
            }
            Func<LinkArgs, LinkResult> handler;
            lock (tools) tools.TryGetValue(m.Tool, out handler);
            if (handler == null)
            {
                Reply(m.Id, LinkResult.Refuse(LinkRefusal.UnknownTool));
                return;
            }
            LinkResult result;
            try
            {
                result = handler(m.Args) ?? LinkResult.Done();
            }
            catch (Exception e)
            {
                SayOnce("threw:" + m.Tool, $"the tool '{m.Tool}' threw {e.GetType().Name}: {e.Message}");
                result = LinkResult.Refuse(LinkRefusal.Failed);
            }
            Reply(m.Id, result);
        }

        /// <summary>A result is always sent (the daemon waits for it): it counts against the overall rate
        /// but is never dropped for it — what the game itself sends has its own rates, which leave room.</summary>
        void Reply(string id, LinkResult r)
        {
            string json = Result(id, r);
            int cap;
            lock (rateLock) cap = Math.Min(MaxResultBytes, maxMessageBytes);
            if (Bytes(json) > cap)
            {
                SayOnce("big-result:" + id, $"a tool's result is larger than {cap} bytes: refused too_large");
                json = Result(id, LinkResult.Refuse(LinkRefusal.TooLarge));
            }
            lock (rateLock) overall.Force(clock());
            SendTaken(json);
        }

        void FlushContext()
        {
            lock (flushLock)
            {
                string json;
                lock (rateLock)
                {
                    if (!contextDue || !live || !grants.Context || lastContext == null) return;
                    // The newest value waits for its rate (and the overall one), never dropped for it.
                    if (!TakeBoth(context)) return;
                    json = Message("context", null, lastContext);
                    contextDue = false;
                }
                if (!SendTaken(json))
                    lock (rateLock) contextDue = true; // the session dropped: say it again on the next one
            }
        }

        void FlushQuiet()
        {
            lock (flushLock)
            {
                string json;
                lock (rateLock)
                {
                    if (!quietDue || !live || !grants.Speaks) return;
                    if (!TakeBoth(quiet)) return;
                    json = quietOn ? "{\"t\":\"quiet\",\"on\":true}" : "{\"t\":\"quiet\",\"on\":false}";
                    quietDue = false;
                    quietSaid = true;
                }
                if (!SendTaken(json))
                    lock (rateLock) quietDue = true;
            }
        }

        /// <summary>Take one from the overall rate and from <paramref name="own"/>, when both have one
        /// (under <see cref="rateLock"/>).</summary>
        bool TakeBoth(Bucket own)
        {
            double now = clock();
            if (!overall.Ready(now) || !own.Ready(now)) return false;
            overall.Take(now);
            own.Take(now);
            return true;
        }

        /// <summary>Send one message within the overall rate and <paramref name="own"/>.</summary>
        bool Send(string json, Bucket own, string what)
        {
            if (!live || ws == null) return false;
            lock (rateLock)
            {
                if (Bytes(json) > maxMessageBytes) return false;
                if (!TakeBoth(own))
                {
                    SayOnce("rate:" + what, $"Astra Link: {what} over the rate, dropped (said once)");
                    return false;
                }
            }
            return SendTaken(json);
        }

        /// <summary>Send a message whose rate is already taken. A failed send ends that connection, so
        /// the receive side notices and the session resumes.</summary>
        bool SendTaken(string json)
        {
            var socket = ws;
            if (!live || socket == null) return false;
            if (socket.SendText(json)) return true;
            Drop(socket);
            return false;
        }

        /// <summary>End the connection <paramref name="socket"/> — and the session with it only if it is
        /// still the current one: a newer session must not be marked dead for an old socket's failure.</summary>
        void Drop(WebSocketClient socket)
        {
            if (ws == socket) live = false;
            socket.Dispose();
        }

        // ---- the socket ------------------------------------------------------------------------

        void Run()
        {
            int failures = 0, resumeRefusals = 0;
            while (!Stopped)
            {
                WebSocketClient socket = null;
                bool welcomed = false;
                string resumeWith = null;
                try
                {
                    string token = null;
                    resumeWith = resume;
                    if (resumeWith == null)
                    {
                        // No session yet, or it cannot be resumed: the file's token — read again each
                        // time, since a new Play writes a new one (and may name a new port).
                        file = Reread() ?? file;
                        if (file == null || file.Token == spentToken)
                        {
                            Stop(spentToken == null
                                ? "Astra Link: no usable link file; Link is off"
                                : "Astra Link: the session cannot be resumed and the link file holds no new token; Link is off until the next Play");
                            break;
                        }
                        if (unixMs() >= file.ExpiresUnixMs)
                        {
                            Stop("Astra Link: the link file expired before Astra answered; Link is off until the next Play");
                            break;
                        }
                        token = file.Token;
                    }
                    socket = WebSocketClient.Connect(file.Host, file.Port, file.UrlPath, timings.ConnectTimeoutMs, Subprotocol);
                    if (Stopped) break; // disposed while connecting: spend nothing
                    connectedTick = pingedTick = Environment.TickCount;
                    lock (said) said.RemoveWhere(k => k.StartsWith("conn:", StringComparison.Ordinal));
                    ws = socket;
                    if (!socket.SendText(Hello(token, resumeWith))) throw new IOException("closed before hello");
                    State($"Astra Link: connected to {file.Host}:{file.Port}, {(token != null ? "hello" : "resuming")}");
                    int startSession = Session;
                    while (true)
                    {
                        string text = socket.Receive();
                        if (text == null) break;
                        try
                        {
                            Handle(text, token, resumeWith);
                        }
                        catch (Exception e)
                        {
                            SayOnce("conn:handle", $"Astra Link: a message could not be handled ({e.GetType().Name}: {e.Message})");
                        }
                        if (Stopped) break;
                    }
                    welcomed = Session != startSession;
                    if (!Stopped) State(welcomed ? "Astra Link: the session dropped; resuming" : "Astra Link: Astra closed the connection before answering hello");
                }
                catch (IOException e)
                {
                    State($"Astra Link: not reachable on {file?.Host}:{file?.Port} ({e.Message}); retrying");
                }
                catch (Exception e)
                {
                    State($"Astra Link: faulted ({e.GetType().Name}: {e.Message}); retrying");
                }
                finally
                {
                    if (socket != null)
                    {
                        if (ws == socket)
                        {
                            live = false;
                            ws = null;
                        }
                        socket.Dispose();
                    }
                }
                if (Stopped) break;
                if (welcomed)
                {
                    failures = 0;
                    resumeRefusals = 0;
                }
                else
                {
                    failures++;
                    if (resumeWith != null && resume == resumeWith && ++resumeRefusals >= timings.ResumeTries)
                    {
                        resume = null; // the daemon does not know it (restarted?): back to the file
                        resumeRefusals = 0;
                    }
                }
                int wait = welcomed ? timings.RetryMinMs : (int)Math.Min(timings.RetryMaxMs, timings.RetryMinMs * Math.Pow(2, Math.Min(failures - 1, 16)));
                stopping.WaitOne(Math.Max(10, wait));
            }
        }

        void Handle(string text, string tokenUsed, string resumeUsed)
        {
            if (text.Length > MaxMessageBytes)
            {
                SayOnce("conn:big", $"Astra Link: a message larger than {MaxMessageBytes} bytes, ignored");
                return;
            }
            Dictionary<string, object> m;
            try
            {
                m = JsonReader.ReadValue(text) as Dictionary<string, object>;
            }
            catch (FormatException e)
            {
                SayOnce("conn:unreadable", $"Astra Link: an unreadable message ({e.Message})");
                return;
            }
            if (m == null || !m.TryGetValue("t", out var t)) return;
            switch (t as string)
            {
                case "welcome":
                {
                    if (Stopped || live) return;
                    if (!(m.TryGetValue("resume", out var r) && r is string rs && rs.Length > 0)) return;
                    if (tokenUsed != null) spentToken = tokenUsed;
                    resume = rs;
                    grants = LinkGrants.From(m.TryGetValue("granted", out var g) ? g : null);
                    AdoptLimits(m.TryGetValue("limits", out var l) ? l as Dictionary<string, object> : null);
                    Interlocked.Increment(ref session);
                    live = true;
                    inbox.Enqueue(new Inbound { Kind = InKind.Welcome });
                    State($"Astra Link: session live ({grants})");
                    return;
                }
                case "grants":
                    if (!live) return;
                    grants = LinkGrants.From(m.TryGetValue("granted", out var ng) ? ng : null);
                    inbox.Enqueue(new Inbound { Kind = InKind.Grants });
                    Log?.Invoke($"Astra Link: granted now {grants}");
                    return;
                case "call":
                {
                    if (!live) return;
                    string id = m.TryGetValue("id", out var i) ? i as string : null;
                    string tool = m.TryGetValue("tool", out var tl) ? tl as string : null;
                    if (string.IsNullOrEmpty(id) || id.Length > 128 || string.IsNullOrEmpty(tool)) return;
                    var args = m.TryGetValue("args", out var a) ? a as Dictionary<string, object> : null;
                    Interlocked.Increment(ref pendingCalls);
                    inbox.Enqueue(new Inbound { Kind = InKind.Call, Session = Session, Id = id, Tool = LocalToolName(tool), Args = new LinkArgs(args), At = clock() });
                    return;
                }
                case "end":
                {
                    string reason = m.TryGetValue("reason", out var why) ? why as string : null;
                    over = true;
                    live = false;
                    inbox.Enqueue(new Inbound { Kind = InKind.Ended, Text = reason });
                    Log?.Invoke($"Astra Link: Astra ended the session ({reason ?? "no reason"}); Link is off until the next Play");
                    return;
                }
                case "error":
                {
                    string code = m.TryGetValue("code", out var c) ? c as string : null;
                    SayOnce("conn:error:" + code, $"Astra Link: Astra refused a message ({code ?? "no code"})");
                    if (code == "rate_limited") SayStatesAgain(refused: true); // a dropped context or quiet would stand wrong
                    if (code == "not_granted" && !live)
                    {
                        // Its hello refused: a token it will never take again (spent, or no Link for this
                        // game now), or a resume it does not know. Not tried again.
                        if (tokenUsed != null) spentToken = tokenUsed;
                        if (resumeUsed != null && resume == resumeUsed) resume = null;
                    }
                    return;
                }
                default:
                    return; // an unknown kind is ignored (link-v1.md §4)
            }
        }

        void AdoptLimits(Dictionary<string, object> limits)
        {
            if (limits == null) return;
            lock (rateLock)
            {
                if (limits.TryGetValue("max_message_bytes", out var b) && b is double bytes && bytes >= 1024)
                    maxMessageBytes = (int)Math.Min(MaxMessageBytes, bytes);
                // A daemon slower than 50 a second: the same margin under its rate.
                if (limits.TryGetValue("max_messages_per_second", out var r) && r is double rate && rate >= 1 && rate < 50)
                    overall.Reset(Math.Max(1, Math.Floor(rate / 5)), rate * 0.76);
            }
        }

        /// <summary>Pings while a session is live, and ends a connection that is silent too long or not
        /// answered in time: the receive side then returns and the session resumes.</summary>
        void Keep()
        {
            while (!stopping.WaitOne(timings.KeeperMs) && !over)
            {
                try
                {
                    var socket = ws;
                    if (socket == null) continue;
                    int now = Environment.TickCount;
                    if (!live && unchecked(now - connectedTick) > timings.WelcomeWithinMs)
                    {
                        State("Astra Link: no answer to hello in time");
                        Drop(socket);
                        continue;
                    }
                    if (live && unchecked(now - pingedTick) >= timings.PingEveryMs)
                    {
                        pingedTick = now;
                        if (!socket.SendPing())
                        {
                            Drop(socket);
                            continue;
                        }
                    }
                    if (unchecked(now - socket.LastFrameTick) >= timings.SilenceEndsMs)
                    {
                        State($"Astra Link: Astra silent for {timings.SilenceEndsMs / 1000.0:0.#} s; resuming");
                        Drop(socket);
                    }
                }
                catch (Exception e)
                {
                    SayOnce("keeper", $"Astra Link: the keeper faulted ({e.GetType().Name}: {e.Message})");
                }
            }
        }

        LinkFile Reread()
        {
            string path = LinkFile.Locate(profileDir, env);
            return path == null ? null : LinkFile.Read(path, integration, unixMs(), out _);
        }

        void Stop(string line)
        {
            over = true;
            live = false;
            Log?.Invoke(line);
        }

        // ---- messages --------------------------------------------------------------------------

        string Hello(string token, string resumeWith)
        {
            var w = new JsonWriter().Open().Str("t", "hello");
            if (token != null) w.Str("token", token);
            else w.Str("resume", resumeWith);
            return w.Str("integration", integration).Num("v", ProtocolVersion).Str("client", client).Close().ToString();
        }

        static string Message(string kind, string name, LinkData data)
        {
            var w = new JsonWriter().Open().Str("t", kind);
            if (name != null) w.Str("name", name);
            w.Open("data");
            data?.WriteTo(w);
            return w.Close().Close().ToString();
        }

        static string Result(string id, LinkResult r)
        {
            var w = new JsonWriter().Open().Str("t", "result").Str("id", id).Bool("ok", r.Ok);
            if (r.Ok)
            {
                if (r.Data != null)
                {
                    w.Open("data");
                    r.Data.WriteTo(w);
                    w.Close();
                }
            }
            else w.Str("error", r.Error);
            return w.Close().ToString();
        }

        static int Bytes(string json) => Encoding.UTF8.GetByteCount(json);

        /// <summary>Whether <paramref name="data"/>, sent as the context, is within <see cref="MaxContextBytes"/>.</summary>
        internal static bool ContextFits(LinkData data) => Bytes(Message("context", null, data)) <= MaxContextBytes;

        public static bool IsEventWord(string word)
        {
            foreach (var w in EventWords)
                if (w == word) return true;
            return false;
        }

        /// <summary>The name a tool was declared with, from the name the AI calls it by
        /// (<c>game_&lt;hash8&gt;_&lt;name&gt;</c>); any other name as it is.</summary>
        public static string LocalToolName(string exposed)
        {
            if (exposed == null || exposed.Length <= 14 || !exposed.StartsWith("game_", StringComparison.Ordinal) || exposed[13] != '_') return exposed;
            for (int i = 5; i < 13; i++)
            {
                char c = exposed[i];
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return exposed;
            }
            return exposed.Substring(14);
        }

        // ---- logging ---------------------------------------------------------------------------

        void State(string s)
        {
            lock (said)
            {
                if (s == lastState) return;
                lastState = s;
            }
            Log?.Invoke(s);
        }

        void SayOnce(string key, string line)
        {
            lock (said)
                if (!said.Add(key)) return;
            Log?.Invoke(line);
        }

        void Raise<T>(Action<T> handlers, T value, string name)
        {
            if (handlers == null) return;
            foreach (Action<T> h in handlers.GetInvocationList())
            {
                try
                {
                    h(value);
                }
                catch (Exception e)
                {
                    SayOnce("raise:" + name, $"Astra Link: a {name} handler threw {e.GetType().Name}: {e.Message}");
                }
            }
        }

        public void Dispose()
        {
            stopping.Set();
            over = true;
            live = false;
            ws?.Close(); // unblocks Receive
            thread?.Join(1000);
            keeper?.Join(1000);
            thread = keeper = null;
        }

        /// <summary>A token bucket: <see cref="Capacity"/> messages at once, refilled at
        /// <see cref="PerSecond"/>.</summary>
        sealed class Bucket
        {
            public double Capacity { get; private set; }
            public double PerSecond { get; private set; }
            double tokens, at = double.NaN;

            public Bucket(double capacity, double perSecond) => Reset(capacity, perSecond);

            public void Reset(double capacity, double perSecond)
            {
                Capacity = capacity;
                PerSecond = perSecond;
                tokens = capacity;
                at = double.NaN;
            }

            void Refill(double now)
            {
                if (!double.IsNaN(at) && now > at) tokens = Math.Min(Capacity, tokens + (now - at) * PerSecond);
                at = now;
            }

            public bool Ready(double now)
            {
                Refill(now);
                return tokens >= 1;
            }

            public void Take(double now)
            {
                Refill(now);
                tokens -= 1;
            }

            /// <summary>Count a message that goes whatever the rate (a result): the debt it leaves, at most
            /// one second's worth, holds back what the game itself sends.</summary>
            public void Force(double now)
            {
                Refill(now);
                tokens = Math.Max(tokens - 1, -PerSecond);
            }
        }
    }
}
