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
    /// astra.Link.Tool("come_here", args => Viking.Holding
    ///     ? LinkResult.Done(new LinkData().Set("arrived", Viking.WalkToPlayer()))
    ///     : LinkResult.Refuse(LinkRefusal.NotHolding));
    /// astra.OnFrame(f =>
    /// {
    ///     if (Viking.JustDied) astra.Link.Event("player_died");
    ///     astra.Link.Quiet(Viking.InMenu);
    /// });
    /// </code>
    /// <para><b>The foundation owns the connection</b> (a <see cref="LinkClient"/>), and opens one only
    /// when Astra wrote a link file for this integration on Play: a game started from Astra, with an
    /// integration from the signed catalogue. Astra decides what is granted, from the switches the user
    /// turned on for this game. Without a link file everything here still works, and does nothing: a
    /// tool is kept for a session that never comes, every send answers false. So an integration
    /// registers its tools and sends its events the same way whether Link is there or not.</para>
    /// <para><b>The main thread only</b>, like the rest of the SDK. Tool handlers run on it too, in the
    /// frame (<see cref="LinkClient.Pump"/>), so they may read the game; one that throws answers
    /// <c>failed</c> and the game goes on.</para>
    /// <para><b>What Astra is told is facts</b>: enums, numbers and booleans under the schemas the
    /// integration declared (<c>[integration.link]</c> in its manifest) — never text a player wrote.
    /// A tool answers with what its declared result schema names, and nothing else reaches the
    /// model.</para>
    /// </summary>
    public sealed class GameLink
    {
        readonly Dictionary<string, Func<LinkArgs, LinkResult>> tools = new Dictionary<string, Func<LinkArgs, LinkResult>>();
        readonly HashSet<string> warned = new HashSet<string>();
        LinkClient client;
        LinkData context;
        bool quiet;

        /// <summary>For the foundation: where a misuse is said once (the plugin's log).</summary>
        internal Action<string> Warn;

        /// <summary>
        /// Answer the tool this integration declared as <paramref name="name"/> (its
        /// <c>[[integration.link.tools]] name</c>) with <paramref name="handler"/>. Called on the main
        /// thread when Astra's AI calls the tool; return <see cref="LinkResult.Done"/> with what the
        /// tool's result schema names, or <see cref="LinkResult.Refuse"/> with a <see cref="LinkRefusal"/>
        /// code (<see cref="LinkRefusal.NotHolding"/> for a tool that moves her while she is not drawn).
        /// A second handler for a name replaces the first. Register them where you register.
        /// </summary>
        /// <exception cref="ArgumentException">The name is not a tool name (<c>^[a-z][a-z0-9_]{0,31}$</c>).</exception>
        public GameLink Tool(string name, Func<LinkArgs, LinkResult> handler)
        {
            if (!LinkRefusal.IsCode(name)) throw new ArgumentException($"'{name}' is not a tool name (^[a-z][a-z0-9_]{{0,31}}$)", nameof(name));
            tools[name] = handler ?? throw new ArgumentNullException(nameof(handler));
            client?.Tool(name, handler);
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
        /// never said.
        /// </summary>
        public bool Context(LinkData data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!LinkClient.ContextFits(data))
            {
                Once("big-context", $"Astra Link: the context is larger than {LinkClient.MaxContextBytes} bytes; it is never said");
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
        /// context and a standing quiet, then start it. A client attached before is closed.</summary>
        internal void Attach(LinkClient c)
        {
            if (c == null) throw new ArgumentNullException(nameof(c));
            Detach();
            client = c;
            foreach (var t in tools) c.Tool(t.Key, t.Value);
            c.GrantsChanged += OnGrants;
            c.Ended += OnEnded;
            if (context != null) c.Context(context);
            if (quiet) c.Quiet(true);
            c.Start();
        }

        /// <summary>Once a frame on the main thread: the calls that arrived run, the events are raised.</summary>
        internal void Pump() => client?.Pump();

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

        void OnGrants(LinkGrants g) => GrantsChanged?.Invoke(g);

        void OnEnded(string why) => Ended?.Invoke(why);

        void Once(string key, string line)
        {
            if (warned.Add(key)) Warn?.Invoke(line);
        }
    }
}
