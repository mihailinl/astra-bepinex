// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge
{
    /// <summary>
    /// A flat set of facts for an Astra Link message — an event's or a trigger's <c>data</c>, the
    /// <c>context</c>, a tool's result: names to strings, numbers or booleans, in the order set
    /// (AstraPlugins <c>link-v1.md</c> §6: every schema is flat). What reaches the AI (events, context)
    /// is held by the daemon to names a schema lists ahead of time, numbers and booleans, never text.
    /// </summary>
    public sealed class LinkData
    {
        enum Kind { Text, Number, Flag }

        struct Entry
        {
            public string Key;
            public Kind Kind;
            public string Text;
            public double Number;
        }

        readonly List<Entry> entries = new List<Entry>();

        public int Count => entries.Count;

        /// <summary>A name (an <c>enum</c> value of its schema) or, where a schema allows it, a string.</summary>
        public LinkData Set(string key, string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            return Put(new Entry { Key = key, Kind = Kind.Text, Text = value });
        }

        /// <summary>A finite number (a non-finite one is refused here, not on the wire).</summary>
        public LinkData Set(string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new ArgumentOutOfRangeException(nameof(value), $"{key}: not a finite number");
            return Put(new Entry { Key = key, Kind = Kind.Number, Number = value });
        }

        public LinkData Set(string key, bool value) => Put(new Entry { Key = key, Kind = Kind.Flag, Number = value ? 1 : 0 });

        LinkData Put(Entry e)
        {
            if (string.IsNullOrEmpty(e.Key)) throw new ArgumentException("a fact needs a name", nameof(e.Key));
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Key == e.Key)
                {
                    entries[i] = e;
                    return this;
                }
            entries.Add(e);
            return this;
        }

        /// <summary>Write the facts as members of the object <paramref name="w"/> has open.</summary>
        internal void WriteTo(JsonWriter w)
        {
            foreach (var e in entries)
            {
                if (e.Kind == Kind.Text) w.Str(e.Key, e.Text);
                else if (e.Kind == Kind.Number) w.Num(e.Key, e.Number);
                else w.Bool(e.Key, e.Number != 0);
            }
        }

        /// <summary>A copy, so a caller may go on changing its own.</summary>
        public LinkData Copy()
        {
            var c = new LinkData();
            c.entries.AddRange(entries);
            return c;
        }
    }

    /// <summary>The arguments of one tool call, as the daemon sent them (already valid against the
    /// tool's declared schema: flat strings, numbers and booleans).</summary>
    public sealed class LinkArgs
    {
        readonly Dictionary<string, object> values;

        internal LinkArgs(Dictionary<string, object> values) => this.values = values ?? new Dictionary<string, object>();

        public bool Has(string key) => values.ContainsKey(key);

        public IEnumerable<string> Keys => values.Keys;

        /// <summary>The string under <paramref name="key"/>, or null.</summary>
        public string Str(string key) => values.TryGetValue(key, out var v) ? v as string : null;

        /// <summary>The number under <paramref name="key"/>, or null.</summary>
        public double? Num(string key) => values.TryGetValue(key, out var v) && v is double d ? d : (double?)null;

        /// <summary>The boolean under <paramref name="key"/>, or null.</summary>
        public bool? Bool(string key) => values.TryGetValue(key, out var v) && v is bool b ? b : (bool?)null;
    }

    /// <summary>The codes a game refuses a call with (<c>link-v1.md</c> §5, "Refusal codes"). The model
    /// is handed the code, never text from the game.</summary>
    public static class LinkRefusal
    {
        /// <summary>She cannot be shown: no compositor, hidden by the player, no link to the engine.</summary>
        public const string NotHolding = "not_holding";
        /// <summary>The main menu, a loading screen.</summary>
        public const string NotInWorld = "not_in_world";
        public const string PlayerDead = "player_dead";
        /// <summary>A cutscene, a teleport.</summary>
        public const string Busy = "busy";
        /// <summary>The thing the call names is not there.</summary>
        public const string UnknownTarget = "unknown_target";
        /// <summary>Valid against the schema, but no sense in the game.</summary>
        public const string BadArgs = "bad_args";
        /// <summary>The handler threw (the foundation's own; the log says why).</summary>
        public const string Failed = "failed";
        /// <summary>No handler for the tool the daemon called (the foundation's own).</summary>
        public const string UnknownTool = "unknown_tool";
        /// <summary>The result was larger than a result may be (the foundation's own).</summary>
        public const string TooLarge = "too_large";

        /// <summary>The shape every code has: <c>^[a-z][a-z0-9_]{0,31}$</c>.</summary>
        public static bool IsCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length > 32 || code[0] < 'a' || code[0] > 'z') return false;
            foreach (char c in code)
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_')) return false;
            return true;
        }
    }

    /// <summary>What a tool handler answers: done (with facts, or none), or refused with a code.</summary>
    public sealed class LinkResult
    {
        public bool Ok { get; }
        public LinkData Data { get; }
        public string Error { get; }

        LinkResult(bool ok, LinkData data, string error)
        {
            Ok = ok;
            Data = data;
            Error = error;
        }

        public static LinkResult Done(LinkData data = null) => new LinkResult(true, data, null);

        /// <summary>Refused with <paramref name="code"/> (<see cref="LinkRefusal"/>, or another of the same
        /// shape).</summary>
        /// <exception cref="ArgumentException">The code is not <c>^[a-z][a-z0-9_]{0,31}$</c>.</exception>
        public static LinkResult Refuse(string code)
        {
            if (!LinkRefusal.IsCode(code)) throw new ArgumentException($"'{code}' is not a refusal code (^[a-z][a-z0-9_]{{0,31}}$)", nameof(code));
            return new LinkResult(false, null, code);
        }
    }

    /// <summary>What the user has granted this session (<c>welcome</c>, then every <c>grants</c>): the
    /// intersection of the signed declaration and the user's switches on the game's row.</summary>
    public sealed class LinkGrants
    {
        public static readonly LinkGrants None = new LinkGrants(null, null, null, false, false, false, false);

        /// <summary>The tools' names as the AI sees them (<c>game_&lt;hash8&gt;_&lt;name&gt;</c>).</summary>
        public IReadOnlyList<string> Tools { get; }
        public IReadOnlyList<string> Triggers { get; }
        public IReadOnlyList<string> Events { get; }
        public bool Context { get; }
        public bool Skill { get; }
        public bool Speaks { get; }
        public bool Talk { get; }

        LinkGrants(List<string> tools, List<string> triggers, List<string> events, bool context, bool skill, bool speaks, bool talk)
        {
            Tools = tools ?? new List<string>();
            Triggers = triggers ?? new List<string>();
            Events = events ?? new List<string>();
            Context = context;
            Skill = skill;
            Speaks = speaks;
            Talk = talk;
        }

        /// <summary>The set a <c>granted</c> object names; what it leaves out is not granted.</summary>
        internal static LinkGrants From(object granted)
        {
            if (!(granted is Dictionary<string, object> g)) return None;
            return new LinkGrants(Strings(g, "tools"), Strings(g, "triggers"), Strings(g, "events"),
                Flag(g, "context"), Flag(g, "skill"), Flag(g, "speaks"), Flag(g, "talk"));
        }

        public bool HasEvent(string word) => Contains(Events, word);
        public bool HasTrigger(string name) => Contains(Triggers, name);

        /// <summary>Whether the tool the game declared as <paramref name="name"/> is granted.</summary>
        public bool HasTool(string name)
        {
            foreach (var t in Tools)
                if (t == name || LinkClient.LocalToolName(t) == name) return true;
            return false;
        }

        public override string ToString() =>
            $"tools [{string.Join(", ", Tools)}], triggers [{string.Join(", ", Triggers)}], events [{string.Join(", ", Events)}], " +
            $"context {Context}, skill {Skill}, speaks {Speaks}, talk {Talk}";

        static bool Contains(IReadOnlyList<string> list, string s)
        {
            foreach (var x in list)
                if (x == s) return true;
            return false;
        }

        static List<string> Strings(Dictionary<string, object> g, string key)
        {
            var list = new List<string>();
            if (g.TryGetValue(key, out var v) && v is List<object> items)
                foreach (var i in items)
                    if (i is string s) list.Add(s);
            return list;
        }

        static bool Flag(Dictionary<string, object> g, string key) => g.TryGetValue(key, out var v) && v is bool b && b;
    }
}
