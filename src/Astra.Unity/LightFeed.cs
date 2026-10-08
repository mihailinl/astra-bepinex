// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// The game's light where SHE stands, sent as FACTS for the engine to style — this client never
    /// decides a look, only what is physically there:
    /// <list type="bullet">
    /// <item>the SUN — the scene's sun (or its brightest directional light), its colour and strength,
    /// and how much of her it reaches: 5 rays (head, chest, hips, both shoulders) cast toward it
    /// through every collider of the game (a roof, a tunnel, a rock), eased per light toward the
    /// measured value (an older engine applies it unsmoothed, and a raw jump would pop). Up to 3
    /// EXTRA realtime directional lights travel the same way, strongest first — omitted while an
    /// integration has chosen the sun, which then owns every directional light;</item>
    /// <item>the AMBIENT by direction — the game's light probes interpolated at her chest (LOCAL: what
    /// the game's own characters get there), else one scene-wide value (GLOBAL: the scene's ambient
    /// probe, or HDRP's own sky probe), or NONE when a physical pipeline hands back nothing at all —
    /// <c>ambientSrc</c> says which, and how OPEN the sky above her is (<c>open</c>) travels whenever
    /// it is not local, for the engine's own sky estimate;</item>
    /// <item>the LAMPS — the point and spot lights that reach her (a flashlight, a ceiling lamp), the
    /// eight strongest where she stands, none behind a wall, each flagged HELD when it rides the
    /// camera or the player — ranked unheld-first, so a carried light never bumps a bolted one out
    /// just by being brighter this instant. A lamp sent last time (unheld) keeps a slot while it
    /// still clears 70% of the weakest lamp it would have to displace, so two close lamps do not
    /// swap every message, but a genuinely new strongest lamp still gets in. Each pipeline
    /// attenuates its lights its own way; each lamp's strength is sent so that the engine's one law
    /// (<see cref="Lamp"/>) gives at her chest what THIS pipeline gives there.</item>
    /// </list>
    /// HDRP's lights are in physical units: they are brought to the picture's scale with the
    /// camera's exposure (<see cref="Exposure"/>) and, for direct light, its Lambert's 1/π; HDRP's
    /// per-light dimmer is folded in by reflection (<see cref="Compat.LightDimmer"/>), the primary sun
    /// included. Sent at most 20 times a second, and only when something changed.
    /// </summary>
    sealed class LightFeed
    {
        const int MaxLamps = 8;
        const int MaxSuns = 3;

        Light sun;
        float nextSearch, nextSend, nextLampSearch, nextSunScan, nextOpenAt = -1;
        float openShare;
        string last, ambientSrc = "none";
        readonly List<Light> sceneLights = new List<Light>();       // point/spot candidates for Lamps
        readonly List<Light> directionalLights = new List<Light>(); // directional candidates for suns
        readonly List<Candidate> candidates = new List<Candidate>();
        readonly List<Candidate> sunCandidates = new List<Candidate>();
        readonly List<Candidate> priority = new List<Candidate>();  // lamps, hysteresis-ordered
        readonly List<Lamp> lamps = new List<Lamp>();
        readonly List<Sun> suns = new List<Sun>();                  // extra suns besides the primary
        readonly HashSet<int> lastLampIds = new HashSet<int>();
        readonly HashSet<int> newLampIds = new HashSet<int>();
        readonly Dictionary<int, float> sunVisible = new Dictionary<int, float>(); // per-light eased Visible (GetInstanceID)
        readonly Vec3[] cube = new Vec3[6];
        readonly Vector3[] probeDirections =
            { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        readonly Vector3[] points = new Vector3[3];     // head, chest, hips
        readonly Vector3[] fivePoints = new Vector3[5]; // head, chest, hips, +shoulder, -shoulder
        static readonly Vector3[] openRays = BuildOpenRays();

        /// <summary>HDRP: the camera's current exposure multiplier; 0 while unknown (1 elsewhere).</summary>
        public float Exposure { get; set; } = 1;

        /// <summary>True in HDRP: lights in physical units.</summary>
        public bool Physical { get; set; }

        /// <summary>The pipeline's own ambient probe where it does not keep the scene's (HDRP's sky
        /// probe); null = use <c>RenderSettings.ambientProbe</c>.</summary>
        public Func<SphericalHarmonicsL2?> PipelineAmbient { get; set; }

        /// <summary>HDRP's own Fog volume (<see cref="Physical"/> only — Built-in/URP read
        /// <c>RenderSettings.fog</c> directly); null = no adapter. <c>enabled</c> false = no active
        /// fog override.</summary>
        public Func<(bool enabled, float density, Color color)> PipelineFog { get; set; }

        /// <summary>Send <c>light.fog</c> at all — off for an engine whose <c>caps</c> left out
        /// <c>"light.fog"</c>; true (the default) for one that never said <c>caps</c>.</summary>
        public bool SendFog { get; set; } = true;

        /// <summary>URP's <c>2^postExposure</c> multiplier from an active ColorAdjustments override
        /// (1 = no change, every pipeline but URP 17): every light VALUE this feed sends — sun/extra
        /// sun intensities, the ambient scalar and cube, a lamp's intensity, the fog colour — is
        /// scaled by it, because URP applies it to the whole picture after grading, downstream of
        /// every one of these readings.</summary>
        public float PostExposure { get; set; } = 1f;

        /// <summary>What she was lit with last, for the log: one line a player can paste.</summary>
        public string Summary { get; private set; }

        struct Candidate
        {
            public Light Light;
            public float Score;
            public bool Held;
        }

        /// <summary>The message to send now, or null (nothing changed, not time yet).</summary>
        /// <param name="feet">Where she stands.</param>
        /// <param name="height">Her height in the game's units (her size included).</param>
        /// <param name="ignore">Colliders that never shade her (her player's).</param>
        /// <param name="chosen">True when the game's integration chose the sun (then <paramref name="choice"/>
        /// is it, or null for "no sun now"); false for the scene's own.</param>
        /// <param name="choice">The integration's sun.</param>
        /// <param name="camera">The main camera, for a lamp's <c>held</c> (and, nowhere else, its own
        /// light — a flashlight's cone is never read from the camera).</param>
        /// <param name="playerRoot">The local player's root, for <c>held</c> too; null = unknown.</param>
        /// <param name="look">The game integration's taste for her light band (<c>UseLook</c>); null =
        /// the engine's.</param>
        /// <param name="wantSummary">Build <see cref="Summary"/> this call (M5) — false the ~19 times
        /// out of 20 a second its caller would throw the interpolated text away unread.</param>
        public string Message(Vector3 feet, float height, HashSet<int> ignore, bool chosen, Light choice, Camera camera, GameObject playerRoot, Look? look, bool wantSummary = true)
        {
            if (Time.unscaledTime < nextSend) return null;
            nextSend = Time.unscaledTime + 0.05f;
            // Physical units with no exposure to bring them to the picture: send NO world light (she
            // keeps her own) rather than lux taken for display units, which would turn her white.
            if (Physical && !(Exposure > 0))
            {
                const string own = "{\"t\":\"light\"}";
                if (last == own) return null;
                last = own;
                return own;
            }
            points[0] = feet + Vector3.up * (0.92f * height); // head
            points[1] = feet + Vector3.up * (0.72f * height); // chest
            points[2] = feet + Vector3.up * (0.5f * height);  // hips
            var chest = points[1];

            // The directional-light roster: its own cadence, independent of whether the integration
            // chooses the primary sun — "every other" light in the scene does not care who picked it.
            if (Time.unscaledTime > nextSunScan)
            {
                nextSunScan = Time.unscaledTime + 3f;
                FindDirectionals();
            }

            if (chosen)
            {
                // The integration owns the directional lights (its null means "no sun right now"):
                // no extra suns travel besides whatever it chose.
                sun = choice;
                suns.Clear();
            }
            else
            {
                // "No sun" is a cached answer too (M7): with `sun == null` on its own, THIS clause
                // never expired, so a scene with no sun at all re-scanned every call (up to 20 Hz)
                // instead of every 3 s — `nextSearch` must gate a re-search regardless of what the
                // last one found. A currently-held sun going inactive still searches AT ONCE.
                if ((sun != null && !sun.isActiveAndEnabled) || Time.unscaledTime > nextSearch)
                {
                    sun = FindSun();
                    nextSearch = Time.unscaledTime + 3f;
                }
                BuildSuns(chest, height, ignore, camera, playerRoot);
            }
            Sun? s = BuildSun(sun, chest, height, ignore);

            Ambient(chest);
            // A sky estimate from the sun's own openness, where the pipeline gave nothing of its own:
            // that call is the engine's now (it has ambientSrc and open to make it with).
            double? open = ambientSrc != "local" ? (double?)Open(chest, ignore) : null;

            Lamps(chest, ignore, camera, playerRoot);
            Fog? fog = SendFog ? BuildFog() : null;

            // The cube's mean as the one ambient colour too: an engine from before the cube reads that.
            double r = 0, g = 0, b = 0;
            foreach (var f in cube)
            {
                r += f.X / 6;
                g += f.Y / 6;
                b += f.Z / 6;
            }
            string json = Messages.Light(s, new Vec3(Fine(r), Fine(g), Fine(b)), cube, lamps, ambientSrc, open, suns, look, fog);
            if (wantSummary)
            {
                int held = 0;
                foreach (var l in lamps) if (l.Held) held++;
                Summary = $"sun {(s.HasValue ? $"{s.Value.Intensity:0.##} × visible {s.Value.Visible:0.##}" : "none")}" +
                          (suns.Count > 0 ? $", +{suns.Count} sun{(suns.Count == 1 ? "" : "s")}" : "") +
                          $", ambient {(r + g + b) / 3:0.####} ({ambientSrc}" +
                          (ambientSrc != "local" ? $", open {openShare:0.##}" : "") + ")" +
                          $", {lamps.Count} lamp(s) of {candidates.Count}" +
                          (held > 0 ? $" ({held} held)" : "") +
                          (look.HasValue ? $", look {look.Value.Floor:0.##}..{look.Value.Ceiling:0.##}" : "") +
                          (fog.HasValue ? $", fog {fog.Value.Mode} " +
                              (fog.Value.Mode == "linear" ? $"{fog.Value.Start:0.#}..{fog.Value.End:0.#}m" : $"density {fog.Value.Density:0.###}") : "") +
                          (Physical ? $", exposure {Exposure:0.######}" : "") +
                          (PostExposure != 1f ? $", post-exposure ×{PostExposure:0.###}" : "");
            }
            if (json == null || json == last) return null;
            last = json;
            return json;
        }

        /// <summary>Forget what was sent, the lamp hysteresis's memory, the per-light visibility
        /// smoothing and the shadow-caster cache: a new session must be told again, from a clean
        /// slate.</summary>
        public void Reset()
        {
            last = null;
            lastLampIds.Clear();
            sunVisible.Clear();
            Compat.ClearShadowCache();
        }

        /// <summary>What turns one of this pipeline's DIRECT light intensities into the picture's
        /// scale: HDRP's lux and candela times the exposure, over its Lambert's π; 1 elsewhere — and,
        /// on top of either, URP's post-exposure (<see cref="PostExposure"/>), which URP applies
        /// after every one of these readings.</summary>
        float DirectScale => (Physical ? Exposure / Mathf.PI : 1f) * PostExposure;

        /// <summary>The haze her picture, composited after the game's own post-processing, never
        /// sees. Built-in/URP: <c>RenderSettings.fog</c> verbatim (colour, mode, density/extent).
        /// HDRP (<see cref="Physical"/>): its own Fog volume through <see cref="PipelineFog"/>,
        /// already lit and exposed. Every colour takes <see cref="PostExposure"/> like every other
        /// light value. Null when there is no fog, or a misconfigured scene's numbers would not pass
        /// the engine's bounds (end &lt;= start, a non-positive density) — dropping only the fog,
        /// never the rest of the message.</summary>
        Fog? BuildFog()
        {
            if (Physical)
            {
                // Named explicitly: a bare `var` here would merge the ternary's branches into an
                // UNNAMED tuple type (the literal fallback carries no names), and `.enabled` below
                // would not compile.
                (bool enabled, float density, Color color) f = PipelineFog != null ? PipelineFog() : default;
                if (!f.enabled || !(f.density > 0)) return null;
                return new Fog
                {
                    Color = new Vec3(Fine(f.color.r * PostExposure), Fine(f.color.g * PostExposure), Fine(f.color.b * PostExposure)),
                    Mode = "exp",
                    Density = Quantise(f.density),
                };
            }
            if (!RenderSettings.fog) return null;
            Color c = RenderSettings.fogColor.linear;
            var color = new Vec3(Fine(c.r * PostExposure), Fine(c.g * PostExposure), Fine(c.b * PostExposure));
            switch (RenderSettings.fogMode)
            {
                case UnityEngine.FogMode.Linear:
                    double start = RenderSettings.fogStartDistance, end = RenderSettings.fogEndDistance;
                    return end > start && start >= 0
                        ? new Fog { Color = color, Mode = "linear", Start = Quantise(start), End = Quantise(end) }
                        : (Fog?)null;
                case UnityEngine.FogMode.ExponentialSquared:
                    return RenderSettings.fogDensity > 0 ? new Fog { Color = color, Mode = "exp2", Density = Quantise(RenderSettings.fogDensity) } : (Fog?)null;
                default: // Exponential
                    return RenderSettings.fogDensity > 0 ? new Fog { Color = color, Mode = "exp", Density = Quantise(RenderSettings.fogDensity) } : (Fog?)null;
            }
        }

        /// <summary>The sun struct for one directional light (the primary or an extra): its colour
        /// times HDRP's per-light dimmer (<see cref="Compat.LightDimmer"/>), normalised, and how much
        /// of her it reaches (<see cref="SunVisible"/>). Null when it contributes nothing.</summary>
        Sun? BuildSun(Light l, Vector3 chest, float height, HashSet<int> ignore)
        {
            if (l == null) return null;
            var toward = -l.transform.forward;
            var c = Rgb(l) * Compat.LightDimmer(l);
            float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (peak <= 1e-4f) return null;
            return new Sun
            {
                Dir = Wire.Of(toward.normalized),
                Color = new Vec3(c.r / peak, c.g / peak, c.b / peak),
                Intensity = Quantise(peak * DirectScale),
                Visible = Quantise(Eased(l, SunVisible(chest, height, toward, ignore))),
            };
        }

        /// <summary>Eases a light's raw <see cref="SunVisible"/> toward what was just measured, at most
        /// 0.15 per message — kept PER LIGHT (the primary sun and each extra sun have their own
        /// memory, by <c>GetInstanceID</c>) so an older engine, which applies <c>visible</c> unsmoothed,
        /// does not see it pop. Seeded with the raw value the first time a light is seen.</summary>
        float Eased(Light l, float raw)
        {
            int id = l.GetInstanceID();
            float v = sunVisible.TryGetValue(id, out float previous) ? Mathf.MoveTowards(previous, raw, 0.15f) : raw;
            sunVisible[id] = v;
            return v;
        }

        /// <summary>How much of a directional light reaches her: the share of 5 rays — head, chest,
        /// hips and both shoulders — that meet no shadow caster between the point and the light. The
        /// shoulders sit 0.18·height off chest along the horizontal axis PERPENDICULAR to the light's
        /// OWN horizontal direction (world X when it is straight up) — never the camera's, so turning
        /// the camera never changes what "both shoulders" means.</summary>
        float SunVisible(Vector3 chest, float height, Vector3 toward, HashSet<int> ignore)
        {
            if (toward.y <= -0.05f) return 0;
            var flat = new Vector2(toward.x, toward.z);
            var side = flat.sqrMagnitude > 1e-6f ? new Vector3(-flat.y, 0, flat.x).normalized : Vector3.right;
            fivePoints[0] = points[0];
            fivePoints[1] = points[1];
            fivePoints[2] = points[2];
            fivePoints[3] = chest + side * (0.18f * height);
            fivePoints[4] = chest - side * (0.18f * height);
            int lit = 0;
            foreach (var p in fivePoints)
                if (!Compat.Shadowed(p, toward, 500f, ignore)) lit++;
            return lit / (float)fivePoints.Length;
        }

        /// <summary>Every enabled, non-baked directional light in a loaded scene — the candidates for
        /// <see cref="suns"/> (and the primary sun's own rival pool, through <see cref="FindSun"/>'s
        /// separate scan). Rescanned every 3 s.</summary>
        void FindDirectionals()
        {
            directionalLights.Clear();
            foreach (var l in Compat.FindAll<Light>())
                if (l != null && l.isActiveAndEnabled && l.type == LightType.Directional && l.gameObject.scene.IsValid() && !InProbes(l))
                    directionalLights.Add(l);
        }

        /// <summary>Up to <see cref="MaxSuns"/> extra directional lights besides the primary
        /// <see cref="sun"/>, strongest first (ranked fresh every call — the roster is cached, the
        /// scoring is not). Skips a light riding the camera or the player (a viewmodel light, never
        /// a fact about the world) and one with <c>cullingMask == 0</c> (lighting nothing).</summary>
        void BuildSuns(Vector3 chest, float height, HashSet<int> ignore, Camera camera, GameObject playerRoot)
        {
            sunCandidates.Clear();
            foreach (var l in directionalLights)
            {
                if (l == null || l == sun || !l.isActiveAndEnabled || l.cullingMask == 0) continue;
                var t = l.transform;
                if (camera != null && t.IsChildOf(camera.transform)) continue;
                if (playerRoot != null && t.IsChildOf(playerRoot.transform)) continue;
                var c = Rgb(l) * Compat.LightDimmer(l);
                float score = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (score > 1e-4f) sunCandidates.Add(new Candidate { Light = l, Score = score });
            }
            sunCandidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            suns.Clear();
            for (int i = 0; i < sunCandidates.Count && suns.Count < MaxSuns; i++)
            {
                var built = BuildSun(sunCandidates[i].Light, chest, height, ignore);
                if (built.HasValue) suns.Add(built.Value);
            }
        }

        /// <summary>The ambient cube at <paramref name="at"/>, in the bridge's axes (z flips: Unity's
        /// +Z face is the bridge's −Z) — and where it came from (<see cref="ambientSrc"/>): LOCAL light
        /// probes interpolated here, GLOBAL one scene-wide value (<c>RenderSettings.ambientProbe</c>,
        /// or HDRP's own sky probe via <see cref="PipelineAmbient"/>), or NONE — a physical pipeline
        /// (HDRP) that gave neither, so the cube reads all zero.</summary>
        void Ambient(Vector3 at)
        {
            SphericalHarmonicsL2 sh;
            var probes = LightmapSettings.lightProbes;
            if (probes != null && probes.count > 0)
            {
                LightProbes.GetInterpolatedProbe(at, null, out sh);
                ambientSrc = "local";
            }
            else
            {
                var pipeline = PipelineAmbient?.Invoke();
                if (pipeline.HasValue)
                {
                    sh = pipeline.Value;
                    ambientSrc = "global";
                }
                else
                {
                    sh = RenderSettings.ambientProbe;
                    ambientSrc = Physical ? "none" : "global";
                }
            }
            var c = Compat.EvaluateSh(sh, probeDirections);
            float scale = (Physical ? Exposure : 1f) * PostExposure;
            Vec3 Face(int i) => new Vec3(Fine(Mathf.Max(0, c[i].r) * scale), Fine(Mathf.Max(0, c[i].g) * scale), Fine(Mathf.Max(0, c[i].b) * scale));
            cube[0] = Face(0);
            cube[1] = Face(1);
            cube[2] = Face(2);
            cube[3] = Face(3);
            cube[4] = Face(5); // the bridge's +Z is Unity's −Z
            cube[5] = Face(4);
            // A physical pipeline's sky probe that answered with nothing (HDRP with no sky, Lethal
            // Company) is no reading, not a black sky: the engine estimates one from the sun.
            if (Physical && ambientSrc == "global" && Dark(cube)) ambientSrc = "none";
        }

        static bool Dark(Vec3[] faces)
        {
            foreach (var f in faces)
                if (0.2126 * f.X + 0.7152 * f.Y + 0.0722 * f.Z > 1e-4) return false;
            return true;
        }

        static Vector3[] BuildOpenRays()
        {
            var rays = new Vector3[9];
            rays[0] = Vector3.up;
            float horizontal = Mathf.Cos(45f * Mathf.Deg2Rad), vertical = Mathf.Sin(45f * Mathf.Deg2Rad);
            for (int i = 0; i < 8; i++)
            {
                float azimuth = i * 45f * Mathf.Deg2Rad;
                rays[1 + i] = new Vector3(horizontal * Mathf.Sin(azimuth), vertical, horizontal * Mathf.Cos(azimuth));
            }
            return rays;
        }

        /// <summary>How much of the sky above <paramref name="at"/> is open: the share of 9 rays — one
        /// straight up, eight at 45° elevation every 45° of azimuth — that meet no shadow caster within
        /// 60 units. Recomputed at most every 0.25 s: a per-message freshness would cost it nothing it
        /// needs.</summary>
        double Open(Vector3 at, HashSet<int> ignore)
        {
            if (Time.unscaledTime >= nextOpenAt)
            {
                nextOpenAt = Time.unscaledTime + 0.25f;
                int open = 0;
                foreach (var d in openRays)
                    if (!Compat.Shadowed(at, d, 60f, ignore)) open++;
                openShare = open / (float)openRays.Length;
            }
            return openShare;
        }

        /// <summary>The eight strongest point and spot lights at her chest, none behind a wall.
        /// Candidates are ranked unheld-first (by score), held ones last, so a carried light never
        /// bumps a bolted one out of the top 8 just by being brighter this instant; the fresh top 8
        /// of that order is the start. A lamp sent last time (unheld, and not already in the top 8)
        /// then displaces the weakest unheld lamp in the top 8 that is not itself an incumbent,
        /// PROVIDED it still clears 70% of that lamp's score — so two close lamps do not swap every
        /// message, but a lamp that beats an incumbent by more than 1/0.7 gets in at once.</summary>
        void Lamps(Vector3 chest, HashSet<int> ignore, Camera camera, GameObject playerRoot)
        {
            if (Time.unscaledTime > nextLampSearch)
            {
                nextLampSearch = Time.unscaledTime + 2f;
                sceneLights.Clear();
                foreach (var l in Compat.FindAll<Light>())
                    if (l != null && (l.type == LightType.Point || l.type == LightType.Spot) && l.gameObject.scene.IsValid())
                        sceneLights.Add(l);
            }
            candidates.Clear();
            foreach (var l in sceneLights)
            {
                if (l == null || !l.isActiveAndEnabled || l.intensity <= 0 || l.range <= 0 || l.cullingMask == 0 || InProbes(l)) continue;
                float d = Vector3.Distance(l.transform.position, chest);
                if (d >= l.range) continue;
                var c = Rgb(l);
                float score = Falloff(l, d) * Cone(l, chest) * (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) * DirectScale;
                // Held is decided HERE, before ranking: the rank and the hysteresis swap below both need it.
                if (score > 0.01f) candidates.Add(new Candidate { Light = l, Score = score, Held = Held(l, camera, playerRoot) });
            }
            // Unheld first (by score), held last (by score): held lamps never compete with the world's
            // own lamps for a slot, they only take one left over.
            candidates.Sort((a, b) => a.Held != b.Held ? (a.Held ? 1 : -1) : b.Score.CompareTo(a.Score));

            priority.Clear();
            int fresh = Mathf.Min(MaxLamps, candidates.Count);
            for (int i = 0; i < fresh; i++) priority.Add(candidates[i]);

            // Every candidate OUTSIDE the fresh top 8: an unheld one sent last time (an incumbent) may
            // reclaim a slot from the weakest unheld lamp in the top 8 that is not itself an incumbent.
            for (int i = fresh; i < candidates.Count; i++)
            {
                var incumbent = candidates[i];
                if (incumbent.Held || !lastLampIds.Contains(incumbent.Light.GetInstanceID())) continue;
                int weakest = -1;
                for (int j = 0; j < priority.Count; j++)
                {
                    if (priority[j].Held || lastLampIds.Contains(priority[j].Light.GetInstanceID())) continue;
                    if (weakest < 0 || priority[j].Score < priority[weakest].Score) weakest = j;
                }
                if (weakest >= 0 && incumbent.Score >= 0.7f * priority[weakest].Score) priority[weakest] = incumbent;
            }

            lamps.Clear();
            newLampIds.Clear();
            foreach (var cand in priority)
            {
                if (lamps.Count == MaxLamps) break;
                var l = cand.Light;
                var at = l.transform.position;
                // A wall between it and her: rays FROM the lamp to her head and chest. Cast that way, the
                // lamp's own fixture (a shade, a globe, the hand that holds a flashlight) cannot hide it:
                // a ray never hits a collider it starts inside.
                float reach = 0;
                for (int i = 0; i < 2; i++)
                {
                    var to = points[i] - at;
                    float dist = to.magnitude;
                    if (dist <= 1e-3f || !Compat.Shadowed(at, to / dist, dist, ignore)) reach += 0.5f;
                }
                if (reach <= 0) continue;
                float d = Vector3.Distance(at, chest);
                float window = Window(d, l.range);
                var c = Rgb(l);
                float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (window <= 1e-4f || peak <= 1e-4f) continue;
                // Its strength in the engine's law (inverse square, windowed to zero at the range),
                // set so the engine gives at her chest what this pipeline gives there. The cone is the
                // engine's to apply, per pixel.
                float intensity = Falloff(l, d) * d * d / window * peak * DirectScale * reach;
                var lamp = new Lamp
                {
                    Pos = Wire.Of(at),
                    Range = Quantise(l.range),
                    Color = new Vec3(Quantise(c.r / peak), Quantise(c.g / peak), Quantise(c.b / peak)),
                    Intensity = Quantise(intensity),
                    Held = cand.Held,
                };
                if (l.type == LightType.Spot)
                {
                    lamp.Spot = true;
                    lamp.Aim = Wire.Of(l.transform.forward);
                    lamp.CosOuter = Quantise(Mathf.Cos(0.5f * l.spotAngle * Mathf.Deg2Rad));
                    lamp.CosInner = Quantise(Mathf.Cos(0.5f * InnerAngle(l) * Mathf.Deg2Rad));
                }
                lamps.Add(lamp);
                newLampIds.Add(l.GetInstanceID());
            }
            lastLampIds.Clear();
            lastLampIds.UnionWith(newLampIds);
        }

        /// <summary>Is this lamp carried — on the camera, or on the player's own root? No distance
        /// rule any more: it flagged a wall sconce the player merely walked past, and a teammate's
        /// own light.</summary>
        static bool Held(Light l, Camera camera, GameObject playerRoot)
        {
            var t = l.transform;
            if (camera != null && t.IsChildOf(camera.transform)) return true;
            return playerRoot != null && t.IsChildOf(playerRoot.transform);
        }

        /// <summary>The light (per unit of intensity) this pipeline delivers at distance
        /// <paramref name="d"/> — what the game's own characters get from it there.</summary>
        float Falloff(Light l, float d)
        {
            if (Physical || GraphicsSettings.currentRenderPipeline != null)
                return Window(d, l.range) / Mathf.Max(d * d, 1e-4f); // URP and HDRP: inverse square, smooth window
            float q = d / l.range;
            return 1f / (1f + 25f * q * q) * Mathf.Clamp01((1 - q) * 10f); // Built-in's falloff, to zero at the range
        }

        /// <summary>The engine's window: the inverse square reaches zero at the range.</summary>
        static float Window(float d, float range)
        {
            float q = d / range;
            float w = Mathf.Clamp01(1 - q * q * q * q);
            return w * w;
        }

        /// <summary>A spot's angular falloff at <paramref name="p"/> (1 for a point light).</summary>
        static float Cone(Light l, Vector3 p)
        {
            if (l.type != LightType.Spot) return 1;
            float cosOuter = Mathf.Cos(0.5f * l.spotAngle * Mathf.Deg2Rad);
            float cosInner = Mathf.Cos(0.5f * InnerAngle(l) * Mathf.Deg2Rad);
            float c = Vector3.Dot(l.transform.forward, (p - l.transform.position).normalized);
            float t = Mathf.Clamp01((c - cosOuter) / Mathf.Max(cosInner - cosOuter, 1e-4f));
            return t * t * (3 - 2 * t);
        }

        /// <summary>The spot's inner angle: its own where the pipeline has one, else three quarters of the outer.</summary>
        static float InnerAngle(Light l)
        {
            float inner = l.innerSpotAngle;
            return inner > 0 && inner < l.spotAngle ? inner : 0.75f * l.spotAngle;
        }

        /// <summary>A light's colour TIMES its intensity as the game renders it: its colour temperature
        /// where the project uses them; in a linear project, linear — and where the project keeps
        /// gamma intensities (Built-in's default, <c>lightsUseLinearIntensity</c> off) Unity takes the
        /// whole product to linear, so a light of 3 lights things like 3^2.2.</summary>
        static Color Rgb(Light l)
        {
            var c = l.color;
            if (GraphicsSettings.lightsUseColorTemperature && l.useColorTemperature)
                c *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature);
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) return c * l.intensity;
            return GraphicsSettings.lightsUseLinearIntensity ? c.linear * l.intensity : (c * l.intensity).linear;
        }

        /// <summary>Is this light's light already in the light probes she is lit by? A BAKED light (and,
        /// under Subtractive lighting, a mixed one other than the sun) lights a moving thing only
        /// through them — counted again as a lamp, it would light her twice.</summary>
        static bool InProbes(Light l)
        {
            var baking = l.bakingOutput;
            if (!baking.isBaked) return false;
            return baking.lightmapBakeType == LightmapBakeType.Baked
                || (baking.lightmapBakeType == LightmapBakeType.Mixed && baking.mixedLightingMode == MixedLightingMode.Subtractive && l.type != LightType.Directional);
        }

        /// <summary>The scene's declared sun, else its brightest directional light — picked from the
        /// already-scanned <see cref="directionalLights"/> roster (M7) rather than a fresh
        /// <c>FindObjectsOfTypeAll</c> here: that roster is rescanned on its own 3 s cadence
        /// (<see cref="FindDirectionals"/>), so a scene with no sun at all no longer pays for a whole
        /// new scan every time this is asked.</summary>
        Light FindSun()
        {
            var declared = RenderSettings.sun;
            if (declared != null && declared.isActiveAndEnabled && declared.type == LightType.Directional && !InProbes(declared)) return declared;
            Light best = null;
            foreach (var l in directionalLights)
            {
                // The roster can be up to 3 s stale (destroyed, disabled, moved out of probes since):
                // re-check what changing state the roster itself does not re-verify until its own scan.
                if (l == null || !l.isActiveAndEnabled || InProbes(l)) continue;
                if (best == null || l.intensity > best.intensity) best = l;
            }
            return best;
        }

        /// <summary>Two decimals: a light that flickers in the fifth digit is not a change worth a message.</summary>
        static double Quantise(double v) => Math.Round(v, 2);

        /// <summary>Four decimals for the ambient: a cave's is a few thousandths in linear light, and two
        /// decimals would round it to black.</summary>
        static double Fine(double v) => Math.Round(v, 4);
    }
}
