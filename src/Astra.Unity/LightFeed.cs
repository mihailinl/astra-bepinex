// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The game's sun and sky, sent so the engine lights her like everything around her: the
    /// scene's sun (or its brightest directional light), its colour and strength, whether it
    /// reaches her (a ray from her chest toward it, through the game's colliders), and the ambient
    /// colour of the scene's light probe. Sent ~10 times a second, and only when it changed.
    /// </summary>
    sealed class LightFeed
    {
        Light sun;
        float nextSearch, nextSend;
        float visible = 1;
        string last;

        /// <summary>The message to send now, or null (nothing changed, not time yet).</summary>
        /// <param name="chest">Where on her the sun is tested for (a ray toward it).</param>
        /// <param name="mask">The layers that shade her.</param>
        /// <param name="ignore">Colliders that never shade her (her player's).</param>
        /// <param name="chosen">True when the game's integration chose the sun (then <paramref name="choice"/>
        /// is it, or null for "no sun now"); false for the scene's own.</param>
        /// <param name="choice">The integration's sun.</param>
        public string Message(Vector3 chest, int mask, HashSet<int> ignore, bool chosen, Light choice)
        {
            if (Time.unscaledTime < nextSend) return null;
            nextSend = Time.unscaledTime + 0.1f;
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
                bool lit = toward.y > -0.05f && !Compat.Raycast(chest, toward, 500f, mask, ignore, out _);
                visible = Mathf.MoveTowards(visible, lit ? 1 : 0, 0.25f);
                var c = QualitySettings.activeColorSpace == ColorSpace.Linear ? sun.color.linear : sun.color;
                float peak = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                if (peak > 1e-4f)
                {
                    s = new Sun
                    {
                        Dir = Wire.Of(toward.normalized),
                        Color = new Vec3(c.r / peak, c.g / peak, c.b / peak),
                        Intensity = Quantise(sun.intensity * peak),
                        Visible = Quantise(visible),
                    };
                }
            }
            var a = Compat.Ambient() * RenderSettings.ambientIntensity;
            var ambient = new Vec3(Quantise(a.r), Quantise(a.g), Quantise(a.b));
            string json = Messages.Light(s, ambient);
            if (json == null || json == last) return null;
            last = json;
            return json;
        }

        /// <summary>Forget what was sent: a new session must be told again.</summary>
        public void Reset() => last = null;

        static Light FindSun()
        {
            var declared = RenderSettings.sun;
            if (declared != null && declared.isActiveAndEnabled && declared.type == LightType.Directional) return declared;
            Light best = null;
            foreach (var l in Compat.FindAll<Light>())
            {
                if (l == null || !l.isActiveAndEnabled || l.type != LightType.Directional || !l.gameObject.scene.IsValid()) continue;
                if (best == null || l.intensity > best.intensity) best = l;
            }
            return best;
        }

        /// <summary>Two decimals: a sun that flickers in the fifth digit is not a change worth a message.</summary>
        static double Quantise(double v) => System.Math.Round(v, 2);
    }
}
