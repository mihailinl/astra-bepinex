// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;
using UnityEngine.Rendering;

namespace Astra.Unity
{
    /// <summary>
    /// The game's light where SHE stands, sent so the engine lights her like everything around her:
    /// <list type="bullet">
    /// <item>the SUN — the scene's sun (or its brightest directional light), its colour and strength,
    /// and how much of her it reaches: rays from her head, chest and hips toward it through every
    /// collider of the game (a roof, a tunnel, a rock);</item>
    /// <item>the AMBIENT by direction — the game's light probes interpolated at her chest (what the
    /// game's own characters get there: a tunnel is dark, a lit floor lights her from below), else
    /// the scene's ambient probe — as an "ambient cube": the light on a surface facing each of the
    /// six axes;</item>
    /// <item>the LAMPS — the point and spot lights that reach her (a flashlight, a ceiling lamp), the
    /// eight strongest where she stands, none behind a wall. Each pipeline attenuates its lights its
    /// own way; each lamp's strength is sent so that the engine's one law (<see cref="Lamp"/>) gives
    /// at her chest what THIS pipeline gives there.</item>
    /// </list>
    /// HDRP's lights are in physical units: they are brought to the picture's scale with the
    /// camera's exposure (<see cref="Exposure"/>) and, for direct light, its Lambert's 1/π. Sent at
    /// most 20 times a second, and only when something changed.
    /// </summary>
    sealed class LightFeed
    {
        const int MaxLamps = 8;

        Light sun;
        float nextSearch, nextSend, nextLampSearch;
        float visible = 1;
        string last;
        readonly List<Light> sceneLights = new List<Light>();
        readonly List<Candidate> candidates = new List<Candidate>();
        readonly List<Lamp> lamps = new List<Lamp>();
        readonly Vec3[] cube = new Vec3[6];
        readonly Vector3[] probeDirections =
            { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        readonly Vector3[] points = new Vector3[3];

        /// <summary>HDRP: the camera's current exposure multiplier; 0 while unknown (1 elsewhere).</summary>
        public float Exposure { get; set; } = 1;

        /// <summary>True in HDRP: lights in physical units.</summary>
        public bool Physical { get; set; }

        struct Candidate
        {
            public Light Light;
            public float Score;
        }

        /// <summary>The message to send now, or null (nothing changed, not time yet).</summary>
        /// <param name="feet">Where she stands.</param>
        /// <param name="height">Her height in the game's units (her size included).</param>
        /// <param name="ignore">Colliders that never shade her (her player's).</param>
        /// <param name="chosen">True when the game's integration chose the sun (then <paramref name="choice"/>
        /// is it, or null for "no sun now"); false for the scene's own.</param>
        /// <param name="choice">The integration's sun.</param>
        public string Message(Vector3 feet, float height, HashSet<int> ignore, bool chosen, Light choice)
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

            if (chosen)
            {
                sun = choice;
            }
            else if (sun == null || !sun.isActiveAndEnabled || Time.unscaledTime > nextSearch)
            {
                sun = FindSun();
                nextSearch = Time.unscaledTime + 3f;
            }
            Sun? s = null;
            if (sun != null)
            {
                var toward = -sun.transform.forward;
                float lit = 0;
                if (toward.y > -0.05f)
                    foreach (var p in points)
                        if (!Compat.Raycast(p, toward, 500f, Physics.DefaultRaycastLayers, ignore, out _)) lit += 1f / points.Length;
                visible = Mathf.MoveTowards(visible, lit, 0.15f);
                var c = Rgb(sun);
                float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (peak > 1e-4f)
                {
                    s = new Sun
                    {
                        Dir = Wire.Of(toward.normalized),
                        Color = new Vec3(c.r / peak, c.g / peak, c.b / peak),
                        Intensity = Quantise(peak * DirectScale),
                        Visible = Quantise(visible),
                    };
                }
            }

            Ambient(chest);
            Lamps(chest, ignore);
            // The cube's mean as the one ambient colour too: an engine from before the cube reads that.
            double r = 0, g = 0, b = 0;
            foreach (var f in cube)
            {
                r += f.X / 6;
                g += f.Y / 6;
                b += f.Z / 6;
            }
            string json = Messages.Light(s, new Vec3(Fine(r), Fine(g), Fine(b)), cube, lamps);
            if (json == null || json == last) return null;
            last = json;
            return json;
        }

        /// <summary>Forget what was sent: a new session must be told again.</summary>
        public void Reset() => last = null;

        /// <summary>What turns one of this pipeline's DIRECT light intensities into the picture's
        /// scale: HDRP's lux and candela times the exposure, over its Lambert's π; 1 elsewhere.</summary>
        float DirectScale => Physical ? Exposure / Mathf.PI : 1f;

        /// <summary>The ambient cube at <paramref name="at"/>, in the bridge's axes (z flips: Unity's
        /// +Z face is the bridge's −Z).</summary>
        void Ambient(Vector3 at)
        {
            SphericalHarmonicsL2 sh;
            var probes = LightmapSettings.lightProbes;
            if (probes != null && probes.count > 0) LightProbes.GetInterpolatedProbe(at, null, out sh);
            else sh = RenderSettings.ambientProbe;
            var c = Compat.EvaluateSh(sh, probeDirections);
            float scale = Physical ? Exposure : 1f;
            Vec3 Face(int i) => new Vec3(Fine(Mathf.Max(0, c[i].r) * scale), Fine(Mathf.Max(0, c[i].g) * scale), Fine(Mathf.Max(0, c[i].b) * scale));
            cube[0] = Face(0);
            cube[1] = Face(1);
            cube[2] = Face(2);
            cube[3] = Face(3);
            cube[4] = Face(5); // the bridge's +Z is Unity's −Z
            cube[5] = Face(4);
        }

        /// <summary>The eight strongest point and spot lights at her chest, none behind a wall.</summary>
        void Lamps(Vector3 chest, HashSet<int> ignore)
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
                if (score > 0.01f) candidates.Add(new Candidate { Light = l, Score = score });
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
            lamps.Clear();
            foreach (var cand in candidates)
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
                    if (dist <= 1e-3f || !Compat.Raycast(at, to / dist, dist, Physics.DefaultRaycastLayers, ignore, out _)) reach += 0.5f;
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
                };
                if (l.type == LightType.Spot)
                {
                    lamp.Spot = true;
                    lamp.Aim = Wire.Of(l.transform.forward);
                    lamp.CosOuter = Quantise(Mathf.Cos(0.5f * l.spotAngle * Mathf.Deg2Rad));
                    lamp.CosInner = Quantise(Mathf.Cos(0.5f * InnerAngle(l) * Mathf.Deg2Rad));
                }
                lamps.Add(lamp);
            }
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

        static Light FindSun()
        {
            var declared = RenderSettings.sun;
            if (declared != null && declared.isActiveAndEnabled && declared.type == LightType.Directional && !InProbes(declared)) return declared;
            Light best = null;
            foreach (var l in Compat.FindAll<Light>())
            {
                if (l == null || !l.isActiveAndEnabled || l.type != LightType.Directional || !l.gameObject.scene.IsValid() || InProbes(l)) continue;
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
