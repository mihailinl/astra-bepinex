// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge
{
    /// <summary>A vector in the bridge's world: glTF axes — right-handed, +Y up, metres.</summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public bool IsFinite => Finite(X) && Finite(Y) && Finite(Z);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator *(Vec3 a, double k) => new Vec3(a.X * k, a.Y * k, a.Z * k);

        public override string ToString() => $"({X:0.###}, {Y:0.###}, {Z:0.###})";

        internal static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /// <summary>
    /// Animator parameters for <c>avatar.params</c> / <c>param</c>: RAW FACTS about her (how fast she
    /// moves, whether she is off the ground), never decisions — what a fact means is her animation
    /// set's business. Reuse one instance; it keeps its order.
    /// </summary>
    public sealed class ParamSet
    {
        enum Kind { Number, Flag, Default }

        struct Entry
        {
            public string Name;
            public Kind Kind;
            public double Value;
        }

        readonly List<Entry> entries = new List<Entry>();

        public int Count => entries.Count;

        public ParamSet Set(string name, double value) => Put(name, Kind.Number, value);
        public ParamSet Set(string name, bool value) => Put(name, Kind.Flag, value ? 1 : 0);

        /// <summary>Hand the parameter back to her animation set's default (<c>null</c> on the wire).</summary>
        public ParamSet Unset(string name) => Put(name, Kind.Default, 0);

        public void Clear() => entries.Clear();

        /// <summary>Copy every parameter of <paramref name="other"/> into this set (its value wins).</summary>
        public void MergeFrom(ParamSet other)
        {
            foreach (var e in other.entries) Put(e.Name, e.Kind, e.Value);
        }

        internal bool AllFinite()
        {
            foreach (var e in entries)
                if (e.Kind == Kind.Number && !Vec3.Finite(e.Value)) return false;
            return true;
        }

        internal void Write(JsonWriter w, string key)
        {
            w.Open(key);
            foreach (var e in entries)
            {
                switch (e.Kind)
                {
                    case Kind.Number: w.Num(e.Name, e.Value); break;
                    case Kind.Flag: w.Bool(e.Name, e.Value != 0); break;
                    default: w.Null(e.Name); break;
                }
            }
            w.Close();
        }

        ParamSet Put(string name, Kind kind, double value)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Name != name) continue;
                entries[i] = new Entry { Name = name, Kind = kind, Value = value };
                return this;
            }
            entries.Add(new Entry { Name = name, Kind = kind, Value = value });
            return this;
        }
    }

    /// <summary>The game's sun, for <c>light</c>.</summary>
    /// <summary>One point or spot light of the game, as <c>light</c>'s <c>lamps</c> carry it. Its
    /// light at a distance d is <c>Color · Intensity / d² · (1 − (d/Range)⁴)²</c>, times a spot's cone
    /// (full inside <see cref="CosInner"/>, none outside <see cref="CosOuter"/>); <see cref="Intensity"/>
    /// is on the sun's scale.</summary>
    public struct Lamp
    {
        public Vec3 Pos;
        public double Range;
        /// <summary>Linear RGB, normalised (the brightest channel 1).</summary>
        public Vec3 Color;
        public double Intensity;
        /// <summary>A spot (else a point light): where it points, and its cone's cosines.</summary>
        public bool Spot;
        public Vec3 Aim;
        public double CosOuter, CosInner;
        /// <summary>True when the game can tell this light is carried (the camera, the player, or
        /// within arm's reach of either) rather than bolted to the world; omitted on the wire when
        /// false.</summary>
        public bool Held;
    }

    public struct Sun
    {
        /// <summary>Unit direction TOWARD the sun.</summary>
        public Vec3 Dir;
        /// <summary>Linear RGB, normalised (the brightest channel 1).</summary>
        public Vec3 Color;
        /// <summary>Against her own key light: 1 ≈ a daytime sun, 0..16.</summary>
        public double Intensity;
        /// <summary>0..1: how much of her the sun reaches (the game's occlusion — a roof, a rock).</summary>
        public double Visible;
    }

    /// <summary>Where her gaze should keep to, vertically, for <c>light</c>'s <c>look</c>. Builder
    /// support only for now — nothing sends it yet; the engine side is still being built.</summary>
    public struct Look
    {
        public double Floor, Ceiling;
    }

    /// <summary>Where she stands, for <c>avatar</c>.</summary>
    public struct Placement
    {
        /// <summary>The point <see cref="Anchor"/> names — her FEET unless it says otherwise.</summary>
        public Vec3 Pos;
        /// <summary>Her facing.</summary>
        public Vec3 Fwd;
        /// <summary>Optional: completes the basis (a tilted seat tilts her). Null keeps her upright.</summary>
        public Vec3? Up;
        /// <summary>Optional: m/s.</summary>
        public Vec3? Vel;
        /// <summary>Optional: a VRM humanoid bone (<c>"hips"</c>…) or <c>"feet"</c> (the default).</summary>
        public string Anchor;
        /// <summary>Her size in the game's world, 0.05..20; 0 = leave it out (1).</summary>
        public double Scale;
    }

    /// <summary>
    /// The protocol's messages (game → engine), as compact JSON. Each builder returns NULL when its
    /// input would be refused by the engine (a non-finite number, a zero direction, a fov or size out
    /// of range) — and says why in <see cref="Rejection"/> — so a bad frame is skipped here rather
    /// than answered with an error there.
    /// </summary>
    public static class Messages
    {
        public const int ProtocolVersion = 1;

        /// <summary>The highest view a <c>cam</c> may name.</summary>
        public const int MaxView = 7;

        /// <summary>The most lamps one <c>light</c> may carry.</summary>
        public const int MaxLamps = 8;

        /// <summary>The most EXTRA directional lights one <c>light</c> may carry, besides <c>sun</c>.</summary>
        public const int MaxSuns = 3;

        /// <summary>A light level the engine takes: 0..16.</summary>
        static double Level(double v) => Math.Max(0, Math.Min(16, v));

        [ThreadStatic] static JsonWriter writer;
        [ThreadStatic] static string rejection;

        /// <summary>Why the last builder on this thread returned null.</summary>
        public static string Rejection => rejection;

        public static string Hello(string client, string token) => Hello(client, token, null);

        /// <param name="client">Who is asking (a log line on the engine's side).</param>
        /// <param name="token">The engine's bridge token, if it has one.</param>
        /// <param name="shadow">Which of her shadows the game reads: <c>"sun"</c> (her view from the
        /// sun), <c>"caster"</c> (her shadow caster), <c>"both"</c>, <c>"none"</c>; null = not said,
        /// which the engine takes for the sun view. Each costs the engine every frame.</param>
        public static string Hello(string client, string token, string shadow)
        {
            var w = Begin("hello").Num("v", ProtocolVersion).Str("client", client ?? "unknown");
            if (!string.IsNullOrEmpty(token)) w.Str("token", token);
            if (!string.IsNullOrEmpty(shadow)) w.Str("shadow", shadow);
            return End(w);
        }

        /// <summary>The main camera's <c>cam</c> (view 0) — the 0.2 signature, kept so a mod built
        /// against it still binds.</summary>
        public static string Cam(long id, Vec3 pos, Vec3 fwd, Vec3 up, double fovY, int width, int height, double[] echo = null) =>
            Cam(id, pos, fwd, up, fovY, width, height, echo, 0);

        /// <param name="id">The game's frame id; it comes back in the published frame.</param>
        /// <param name="pos">The eye.</param>
        /// <param name="fwd">Where it looks.</param>
        /// <param name="up">Fixes the roll.</param>
        /// <param name="fovY">VERTICAL field of view, degrees, 1..179.</param>
        /// <param name="width">Picture width wanted (the engine scales an oversize one down).</param>
        /// <param name="height">Picture height wanted.</param>
        /// <param name="echo">Up to 3 numbers copied into the frame verbatim (may be null).</param>
        /// <param name="view">Which VIEW this camera is (0..7): each is drawn separately, with the same
        /// pose of her — the main camera is 0, a video camera or a mirror another. The picture's size
        /// follows view 0; another view's width/height give only its aspect.</param>
        public static string Cam(long id, Vec3 pos, Vec3 fwd, Vec3 up, double fovY, int width, int height, double[] echo, int view)
        {
            if (view < 0 || view > MaxView) return Reject($"cam: view {view} is outside 0..{MaxView}");
            if (!pos.IsFinite || !Direction(fwd) || !Direction(up)) return Reject("cam: a non-finite or zero vector");
            if (!(fovY >= 1 && fovY <= 179)) return Reject($"cam: fovY {fovY} is outside 1..179");
            if (width < 1 || height < 1 || width > 8192 || height > 8192) return Reject($"cam: size {width}x{height}");
            int n = echo == null ? 0 : Math.Min(echo.Length, 3);
            for (int i = 0; i < n; i++)
                if (!Vec3.Finite(echo[i])) return Reject("cam: a non-finite echo");
            var w = Begin("cam").Num("id", id).Vec("pos", pos).Vec("fwd", fwd).Vec("up", up)
                .Num("fovY", fovY).Num("w", width).Num("h", height);
            if (n > 0) w.Nums("echo", echo, n);
            if (view != 0) w.Num("view", view);
            return End(w);
        }

        public static string Avatar(Placement p, ParamSet parameters = null)
        {
            if (!p.Pos.IsFinite || !Direction(p.Fwd)) return Reject("avatar: a non-finite position or a zero facing");
            if (p.Up.HasValue && !Direction(p.Up.Value)) return Reject("avatar: a non-finite or zero up");
            if (p.Vel.HasValue && !p.Vel.Value.IsFinite) return Reject("avatar: a non-finite velocity");
            if (p.Scale != 0 && !(p.Scale >= 0.05 && p.Scale <= 20)) return Reject($"avatar: scale {p.Scale} is outside 0.05..20");
            if (parameters != null && !parameters.AllFinite()) return Reject("avatar: a non-finite parameter");
            var w = Begin("avatar").Num("id", 0).Vec("pos", p.Pos).Vec("fwd", p.Fwd);
            if (p.Up.HasValue) w.Vec("up", p.Up.Value);
            if (p.Vel.HasValue) w.Vec("vel", p.Vel.Value);
            if (!string.IsNullOrEmpty(p.Anchor)) w.Str("anchor", p.Anchor);
            if (p.Scale != 0) w.Num("scale", p.Scale);
            if (parameters != null && parameters.Count > 0) parameters.Write(w, "params");
            return End(w);
        }

        /// <summary>Parameters on their own.</summary>
        public static string Param(ParamSet parameters)
        {
            if (!parameters.AllFinite()) return Reject("param: a non-finite parameter");
            var w = Begin("param");
            parameters.Write(w, "set");
            return End(w);
        }

        /// <summary>Enter a state of her animation set by name (and hold it until the graph leaves).</summary>
        public static string Play(string state, string expectedSet = null)
        {
            var w = Begin("play").Str("state", state);
            if (!string.IsNullOrEmpty(expectedSet)) w.Str("set", expectedSet);
            return End(w);
        }

        /// <summary>Raise a token into her graph.</summary>
        public static string Trigger(string name, string expectedSet = null)
        {
            var w = Begin("trigger").Str("name", name);
            if (!string.IsNullOrEmpty(expectedSet)) w.Str("set", expectedSet);
            return End(w);
        }

        /// <summary>Hand her back to her graph's default state.</summary>
        public static string Resume() => End(Begin("resume"));

        /// <summary>The light of the game's world around her; both parts null hands her own light back.</summary>
        public static string Light(Sun? sun, Vec3? ambient) => Light(sun, ambient, null, null);

        /// <param name="sun">The key light; null = hers.</param>
        /// <param name="ambient">One ambient colour; null = hers (or the cube's, for an engine that knows it).</param>
        /// <param name="cube">The ambient on a surface facing +X, −X, +Y, −Y, +Z, −Z (the bridge's axes):
        /// six colours, or null.</param>
        /// <param name="lamps">At most eight point and spot lights, strongest first; null or empty = none.</param>
        public static string Light(Sun? sun, Vec3? ambient, Vec3[] cube, IList<Lamp> lamps) =>
            Light(sun, ambient, cube, lamps, null, null, null, null);

        /// <param name="sun">The key light; null = hers.</param>
        /// <param name="ambient">One ambient colour; null = hers (or the cube's, for an engine that knows it).</param>
        /// <param name="cube">The ambient on a surface facing +X, −X, +Y, −Y, +Z, −Z (the bridge's axes):
        /// six colours, or null.</param>
        /// <param name="lamps">At most eight point and spot lights, strongest first; null or empty = none.</param>
        /// <param name="ambientSrc">Where <paramref name="cube"/> came from: <c>"local"</c> (light
        /// probes at her own point — what the game's own characters get there), <c>"global"</c> (one
        /// scene-wide value), <c>"none"</c> (a physical pipeline, HDRP, with neither — the cube is all
        /// zero); null = not said.</param>
        /// <param name="open">0..1: the share of the sky above her that is open (no shadow caster
        /// within 60 units), for an engine filling in its own sky estimate when
        /// <paramref name="ambientSrc"/> is not local; null = not said.</param>
        /// <param name="suns">Up to three EXTRA realtime directional lights besides <paramref name="sun"/>,
        /// strongest first, each validated and clamped the same way; null or empty = none.</param>
        /// <param name="look">Where her gaze should keep to, vertically; null = not said (builder
        /// support only — nothing sends this yet).</param>
        public static string Light(Sun? sun, Vec3? ambient, Vec3[] cube, IList<Lamp> lamps, string ambientSrc, double? open, IList<Sun> suns, Look? look)
        {
            var w = Begin("light");
            if (sun.HasValue)
            {
                var s = sun.Value;
                if (!Direction(s.Dir) || !s.Color.IsFinite || !Vec3.Finite(s.Intensity) || !Vec3.Finite(s.Visible))
                    return Reject("light: a non-finite sun");
                w.Open("sun");
                WriteSunFields(w, s);
                w.Close();
            }
            if (suns != null && suns.Count > 0)
            {
                if (suns.Count > MaxSuns) return Reject($"light: {suns.Count} extra suns, at most {MaxSuns}");
                w.OpenArray("suns");
                foreach (var extra in suns)
                {
                    if (!Direction(extra.Dir) || !extra.Color.IsFinite || !Vec3.Finite(extra.Intensity) || !Vec3.Finite(extra.Visible))
                        return Reject("light: a non-finite extra sun");
                    w.Item();
                    WriteSunFields(w, extra);
                    w.Close();
                }
                w.CloseArray();
            }
            if (ambient.HasValue)
            {
                if (!ambient.Value.IsFinite) return Reject("light: a non-finite ambient");
                w.Vec("ambient", new Vec3(Level(ambient.Value.X), Level(ambient.Value.Y), Level(ambient.Value.Z)));
            }
            if (cube != null)
            {
                if (cube.Length != 6) return Reject("light: an ambient cube has six faces");
                var faces = new double[18];
                for (int i = 0; i < 6; i++)
                {
                    if (!cube[i].IsFinite) return Reject("light: a non-finite ambient cube");
                    faces[3 * i] = Level(cube[i].X);
                    faces[3 * i + 1] = Level(cube[i].Y);
                    faces[3 * i + 2] = Level(cube[i].Z);
                }
                w.Nums("ambientCube", faces, 18);
            }
            if (ambientSrc != null)
            {
                if (ambientSrc != "local" && ambientSrc != "global" && ambientSrc != "none")
                    return Reject($"light: ambientSrc '{ambientSrc}' is none of local/global/none");
                w.Str("ambientSrc", ambientSrc);
            }
            if (open.HasValue)
            {
                if (!Vec3.Finite(open.Value)) return Reject("light: a non-finite open");
                w.Num("open", Math.Round(Math.Max(0, Math.Min(1, open.Value)), 2));
            }
            if (lamps != null && lamps.Count > 0)
            {
                if (lamps.Count > MaxLamps) return Reject($"light: {lamps.Count} lamps, at most {MaxLamps}");
                w.OpenArray("lamps");
                foreach (var l in lamps)
                {
                    if (!l.Pos.IsFinite || !l.Color.IsFinite || !Vec3.Finite(l.Range) || !Vec3.Finite(l.Intensity) || !(l.Range > 0))
                        return Reject("light: a lamp with a non-finite or empty part");
                    w.Item().Vec("pos", l.Pos).Num("range", Math.Min(l.Range, 1e4))
                        .Vec("color", new Vec3(Level(l.Color.X), Level(l.Color.Y), Level(l.Color.Z)))
                        .Num("intensity", Math.Max(0, Math.Min(1e4, l.Intensity)));
                    if (l.Spot)
                    {
                        if (!Direction(l.Aim) || !Vec3.Finite(l.CosOuter) || !Vec3.Finite(l.CosInner)) return Reject("light: a spot with no aim");
                        double outer = Math.Max(-1, Math.Min(1, l.CosOuter)), inner = Math.Max(outer, Math.Min(1, l.CosInner));
                        w.Open("spot").Vec("dir", l.Aim).Nums("cos", new[] { outer, inner }, 2).Close();
                    }
                    if (l.Held) w.Bool("held", true);
                    w.Close();
                }
                w.CloseArray();
            }
            if (look.HasValue)
            {
                var lk = look.Value;
                if (!Vec3.Finite(lk.Floor) || !Vec3.Finite(lk.Ceiling)) return Reject("light: a non-finite look");
                double floor = Math.Max(0.05, Math.Min(0.40, lk.Floor)), ceiling = Math.Max(0.8, Math.Min(2.0, lk.Ceiling));
                w.Open("look").Num("floor", floor).Num("ceiling", ceiling).Close();
            }
            return End(w);
        }

        /// <summary>A sun's 4 fields, shared by <c>sun</c> and every <c>suns</c> item: same units, same
        /// clamps.</summary>
        static void WriteSunFields(JsonWriter w, Sun s)
        {
            w.Vec("dir", s.Dir).Vec("color", s.Color)
                .Num("intensity", Math.Max(0, Math.Min(16, s.Intensity)))
                .Num("visible", Math.Max(0, Math.Min(1, s.Visible)));
        }

        static JsonWriter Begin(string type)
        {
            var w = writer ?? (writer = new JsonWriter());
            w.Reset();
            rejection = null;
            return w.Open().Str("t", type);
        }

        static string End(JsonWriter w) => w.Close().ToString();

        static string Reject(string why)
        {
            rejection = why;
            return null;
        }

        static bool Direction(Vec3 v) => v.IsFinite && v.Length > 1e-9;
    }

    /// <summary>The engine's <c>hello</c> reply: where its frame ring is and how big a picture it carries.</summary>
    public sealed class HelloReply
    {
        public int Version;
        public string Engine;
        public string EngineVersion;
        /// <summary>The ring as the ENGINE names it: <c>/dev/shm/…</c> (Linux), <c>Local\…</c> (Windows),
        /// a temp-dir path (macOS). <see cref="FrameRing.Open"/> turns it into what this process can map.</summary>
        public string Shm;
        public int MaxWidth;
        public int MaxHeight;
        /// <summary>How many views (<c>cam.view</c> 0..Views−1) the engine draws; 0 = an engine from
        /// before views, which takes EVERY <c>cam</c> for the main camera — send it no other view.</summary>
        public int Views;

        /// <summary>Null when <paramref name="m"/> is not a hello.</summary>
        public static HelloReply From(Dictionary<string, object> m)
        {
            if (!(m.TryGetValue("t", out var t) && (t as string) == "hello")) return null;
            return new HelloReply
            {
                Version = Int(m, "v"),
                Engine = m.TryGetValue("engine", out var e) ? e as string : null,
                EngineVersion = m.TryGetValue("ver", out var ver) ? ver as string : null,
                Shm = m.TryGetValue("shm", out var s) ? s as string : null,
                MaxWidth = Int(m, "maxW"),
                MaxHeight = Int(m, "maxH"),
                Views = Int(m, "views"),
            };
        }

        static int Int(Dictionary<string, object> m, string key) =>
            m.TryGetValue(key, out var v) && v is double d ? (int)d : 0;
    }
}
