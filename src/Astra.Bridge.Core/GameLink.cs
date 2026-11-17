// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge
{
    /// <summary>
    /// A game integration's side of Astra Link (AstraPlugins <c>spec/games/link-v1.md</c>), as the SDK
    /// hands it out (<c>GameIntegration.Link</c>): the tools Astra's AI may call in this game, the events
    /// and the context it is told, the integration's triggers, the voice key and "quiet".
    /// <code>
    /// var astra = AstraSdk.Register("knlce.valheim", "Valheim");
    /// astra.Link.Tool("come_here", args => AstraSdk.Drawn
    ///     ? LinkResult.Done(new LinkData().Set("arrived", Viking.WalkToPlayer()))
    ///     : LinkResult.Refuse(LinkRefusal.NotHolding));
    /// astra.Link.OnFrame(() =>
    /// {
    ///     if (Viking.JustDied) astra.Link.Event("player_died");
    ///     astra.Link.Quiet(Viking.InMenu);
    /// });
    /// </code>
    /// <para><b>The foundation owns the connection</b> (a <see cref="LinkClient"/>), and opens one only
    /// when Astra wrote a link file for this integration on Play: a game started from Astra, with an
    /// integration from the signed catalogue. Astra decides what is granted, from the switches the user
    /// turned on for this game. Without a link file everything here still works, and does nothing: a
    /// tool is kept for a session that never comes, every send answers false, <see cref="OnFrame"/> is
    /// never called. So an integration registers the same way whether Link is there or not.</para>
    /// <para><b>It does not follow her picture.</b> Hiding her, or her drawing stopping, leaves it open,
    /// and <see cref="OnFrame"/> goes on — unlike the integration's own frame handlers, which run only
    /// while she is drawn.</para>
    /// <para><b>The main thread only</b>, like the rest of the SDK. Tool handlers run on it too, in the
    /// frame, so they may read the game. What an integration hands here never takes it down: a tool that
    /// throws answers <c>failed</c>, a frame handler that throws is switched off, and each is said once in
    /// the log with its stack.</para>
    /// <para><b>What Astra is told is facts</b>: enums, numbers and booleans under the schemas the
    /// integration declared (<c>[integration.link]</c> in its manifest) — never text a player wrote.
    /// A tool answers with what its declared result schema names, and nothing else reaches the
    /// model.</para>
    /// </summary>
    public sealed class GameLink
    {
        readonly Dictionary<string, Func<LinkArgs, LinkResult>> tools = new Dictionary<string, Func<LinkArgs, LinkResult>>();
        readonly List<Action> frameHandlers = new List<Action>();
        readonly HashSet<string> warned = new HashSet<string>();
        LinkClient client;
        LinkData context;
        bool quiet;

        /// <summary>For the foundation: where a misuse or a failure is said once (the plugin's log).</summary>
        internal Action<string> Warn;

        /// <summary>
        /// Answer the tool this integration declared as <paramref name="name"/> (its
        /// <c>[[integration.link.tools]] name</c>) with <paramref name="handler"/>. Called on the main
        /// thread when Astra's AI calls the tool; return <see cref="LinkResult.Done"/> with what the
        /// tool's result schema names, or <see cref="LinkResult.Refuse"/> with a <see cref="LinkRefusal"/>
        /// code (<see cref="LinkRefusal.NotHolding"/> for a tool that moves her while she is not drawn).
        /// Answer at once: Astra waits 10 seconds for a call, and one that waited 9 for the frame is refused
        /// <c>busy</c> without running. A second handler for a name replaces the first. Register them where
        /// you register.
        /// </summary>
        /// <exception cref="ArgumentException">The name is not a tool name (<c>^[a-z][a-z0-9_]{0,31}$</c>).</exception>
        public GameLink Tool(string name, Func<LinkArgs, LinkResult> handler)
        {
            if (!LinkRefusal.IsCode(name)) throw new ArgumentException($"'{name}' is not a tool name (^[a-z][a-z0-9_]{{0,31}}$)", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            var guarded = Guard(name, handler);
            tools[name] = guarded;
            client?.Tool(name, guarded);
            return this;
        }

        /// <summary>
        /// Called every frame on the main thread while Astra Link has a connection (Astra wrote a link file
        /// for this integration), whether or not she is drawn, after the tool calls of the frame: the place
        /// to send events, the context and quiet from. Several run in the order they were added; one that
        /// throws is switched off and said once.
        /// </summary>
        public GameLink OnFrame(Action handler)
        {
            frameHandlers.Add(handler ?? throw new ArgumentNullException(nameof(handler)));
            return this;
        }

        /// <summary>
        /// Tell Astra that <paramref name="word"/> just happened: one of the v1 event words
        /// (<see cref="LinkClient.EventWords"/>) this integration declared, with data under its declared
        /// schema. Send it once, at the change (the moment the player falls), never every frame while a
        /// state holds: a state is the context, or a bridge parameter for her animation. False, and
        /// nothing sent, when no session is live, the user has not granted it, or its rate is spent.
        /// </summary>
        public bool Event(string word, LinkData data = null)
        {
            if (!LinkClient.IsEventWord(word))
            {
                Once("word:" + word, $"Astra Link: '{word}' is not a v1 event word ({string.Join(", ", LinkClient.EventWords)}); it is never sent");
                return false;
            }
            return client != null && client.Event(word, data);
        }

        /// <summary>
        /// What the game is like now, as facts under this integration's declared context schema. It
        /// replaces the last value and is kept: said when a session starts, again at every new one, and
        /// at most every 1.25 s in between (the newest value wins). Set it when it changes, or once a
        /// second; every call copies it. False when it is larger than a context may be (2 KiB): it is then
        /// not taken, and the last value that fitted stands.
        /// </summary>
        public bool Context(LinkData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!LinkClient.ContextFits(data))
            {
                Once("big-context", $"Astra Link: a context larger than {LinkClient.MaxContextBytes} bytes is not taken; the last one that fitted stands");
                return false;
            }
            context = data.Copy();
            if (client != null) client.Context(context);
            return true;
        }

        /// <summary>Fire the trigger this integration declared as <paramref name="name"/>: the automations
        /// the user bound to it run. False when not granted, no session is live, or its rate is spent.</summary>
        public bool Trigger(string name, LinkData data = null) => client != null && client.Trigger(name, data);

        /// <summary>The player pressed this integration's voice key on her: Astra opens the user's voice
        /// input as if they had pressed their own key. False when not granted (the integration declares
        /// <c>talk</c> and the user switched "AI may act" on) or within 2.2 s of the last.</summary>
        public bool Talk() => client != null && client.Talk();

        /// <summary>The game is in a menu, a cutscene or paused (<paramref name="on"/>), or play resumed:
        /// Astra does not speak on her own meanwhile. Say it every frame if that is easier: only a change
        /// is sent. Kept for a session that starts later.</summary>
        public void Quiet(bool on)
        {
            quiet = on;
            client?.Quiet(on);
        }

        /// <summary>A session is live: Astra took this game's hello and nothing has ended it since.</summary>
        public bool Live => client != null && client.Live;

        /// <summary>What the user granted now (<see cref="LinkGrants.None"/> while no session is live): a
        /// tool, an event or <c>speaks</c> they have not switched on is never asked for.</summary>
        public LinkGrants Grants => client != null ? client.Grants : LinkGrants.None;

        /// <summary>A session started, or the user changed what is granted (a switch on the game's row
        /// in Astra): stop at once what is no longer granted. On the main thread.</summary>
        public event Action<LinkGrants> GrantsChanged;

        /// <summary>Astra ended Link for this game for good, with its reason (<c>withdrawn</c>,
        /// <c>stopped</c>, <c>replaced</c>, <c>game_exited</c>, <c>catalogue_stale</c>). On the main thread.</summary>
        public event Action<string> Ended;

        // ---- the foundation's side --------------------------------------------------------------------

        /// <summary>The tools registered so far.</summary>
        internal int ToolCount => tools.Count;

        /// <summary>A client is attached (Astra wrote a link file for this integration).</summary>
        internal bool Attached => client != null;

        /// <summary>Take <paramref name="c"/> as this integration's connection: hand it the tools, the
        /// context and quiet, then start it (a started one goes on). A client attached before is closed.
        /// A session already live is told to the integration at once (<see cref="GrantsChanged"/>).</summary>
        internal void Attach(LinkClient c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            Detach();
            client = c;
            foreach (var t in tools) c.Tool(t.Key, t.Value);
            c.GrantsChanged += OnGrants;
            c.Ended += OnEnded;
            if (context != null) c.Context(context);
            c.Quiet(quiet);
            c.Start();
            if (c.Live) OnGrants(c.Grants);
        }

        /// <summary>
        /// The same integration registered again (a new <c>GameIntegration</c> with the same id): its
        /// session goes on in <paramref name="next"/> — the link file's token was spent at the first hello,
        /// and only this connection can resume. The old handlers are forgotten first, so none of them runs.
        /// </summary>
        internal void HandOver(GameLink next)
        {
            var c = client;
            if (c == null || next == null || ReferenceEquals(next, this)) return;
            client = null;
            c.GrantsChanged -= OnGrants;
            c.Ended -= OnEnded;
            c.ClearTools();
            next.Attach(c);
        }

        /// <summary>Once a frame on the main thread: the calls that arrived run, the events are raised, then
        /// the frame handlers.</summary>
        internal void Pump()
        {
            var c = client;
            if (c == null) return;
            c.Pump();
            if (c.Over || !ReferenceEquals(c, client)) return;
            for (int i = 0; i < frameHandlers.Count; i++)
            {
                var h = frameHandlers[i];
                if (h == null) continue;
                try
                {
                    h();
                }
                catch (Exception e)
                {
                    frameHandlers[i] = null;
                    Warn?.Invoke($"Astra Link: a frame handler threw and is switched off: {e}");
                }
            }
        }

        /// <summary>Close the connection (the integration was dropped or replaced, or the game is closing).</summary>
        internal void Detach()
        {
            var c = client;
            if (c == null) return;
            client = null;
            c.GrantsChanged -= OnGrants;
            c.Ended -= OnEnded;
            c.Dispose();
        }

        Func<LinkArgs, LinkResult> Guard(string name, Func<LinkArgs, LinkResult> handler) => args =>
        {
            try
            {
                return handler(args);
            }
            catch (Exception e)
            {
                Once("threw:" + name, $"Astra Link: the tool '{name}' threw and answered 'failed': {e}");
                return LinkResult.Refuse(LinkRefusal.Failed);
            }
        };

        void OnGrants(LinkGrants g) => Raise(GrantsChanged, g, nameof(GrantsChanged));

        void OnEnded(string why) => Raise(Ended, why, nameof(Ended));

        /// <summary>Each subscriber on its own: one that throws is said once and the next still runs.</summary>
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
                    Once("raise:" + name, $"Astra Link: a {name} handler threw: {e}");
                }
            }
        }

        void Once(string key, string line)
        {
            if (warned.Add(key)) Warn?.Invoke(line);
        }
    }
}
