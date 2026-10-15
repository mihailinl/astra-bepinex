// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using Astra.Bridge.Capture;
using UnityEngine;

namespace Astra.Sdk
{
    public sealed partial class GameIntegration
    {
        /// <summary>What <see cref="CaptureWhile"/> registered, in order.</summary>
        internal readonly List<CaptureRequest> Captures = new List<CaptureRequest>();

        /// <summary>
        /// <b>Capture the game's own animation for her</b> while the declared true/false parameter
        /// <paramref name="param"/> is true: the player's character is sampled 30 times a second, and
        /// when <paramref name="param"/> turns false the take is offered to Astra as a <c>.vrma</c>
        /// clip named <paramref name="clip"/> — "the game's own <c>climb</c>, as her move for
        /// <c>climbing</c>". The player answers in Astra ([Use for climbing] / [Keep] / [Discard]).
        /// <code>
        /// astra.DeclareParam("climbing", ParamType.Bool);
        /// astra.OnFrame(f => f.Params.Set("climbing", PeakPlayer.Climbing));
        /// astra.CaptureWhile("climbing", "climb", () => PeakPlayer.Animator, PeakPlayer.Bones);
        /// </code>
        /// <para>
        /// Nothing is recorded unless the PLAYER switched capture on for your integration in Astra (off
        /// by default; the engine's hello reply then names its capture inbox). A captured clip never
        /// leaves the player's machine: Astra refuses to export it. A take lasts 1.5 to 60 seconds, at
        /// most three are offered per game run, and a take the engine refuses is removed again.
        /// </para>
        /// </summary>
        /// <param name="param">A parameter declared with <see cref="DeclareParam"/> as
        /// <see cref="ParamType.Bool"/> and set every frame in <see cref="FrameContext.Params"/>: the
        /// capture follows the very fact her animation set reads.</param>
        /// <param name="clip">The clip's name (<c>climb</c>): 1–64 of <c>A-Z a-z 0-9 _ . -</c>, not
        /// starting with a dot. Astra files it as <c>captured-climb</c>, so it replaces nothing she
        /// plays until the player says so.</param>
        /// <param name="player">The player's <see cref="Animator"/> right now, or null when there is
        /// none (a menu, a loading screen). Asked when a take starts.</param>
        /// <param name="bones">For a rig whose Animator is NOT humanoid (a Generic rig, no Avatar): the
        /// game's bone for each VRM humanoid bone, by transform NAME under the Animator
        /// (<c>["leftUpperArm"] = "Arm_L"</c>). At least the hips, spine, head, both arms (upper, lower,
        /// hand) and both legs (upper, lower, foot); add the chest, neck, shoulders, toes and fingers
        /// you have. Null for a humanoid Animator, whose bones Unity knows. The rest pose is the rig's
        /// bind pose (its skinned meshes'), straightened to a T-pose.</param>
        /// <exception cref="ArgumentException"><paramref name="param"/> is not declared as a bool, is
        /// captured already, or <paramref name="clip"/> or <paramref name="bones"/> is not one Astra
        /// takes.</exception>
        public GameIntegration CaptureWhile(string param, string clip, Func<Animator> player, IDictionary<string, string> bones = null)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            bool declaredBool = false;
            foreach (var d in Declared.ToArray())
                if (d.Name == param && d.Type == ParamType.Bool) declaredBool = true;
            if (!declaredBool)
                throw new ArgumentException($"'{param}' is not a parameter this integration declared as a bool (DeclareParam(\"{param}\", ParamType.Bool) first)", nameof(param));
            if (!CaptureRecorder.ValidClipName(clip))
                throw new ArgumentException($"'{clip}' is not a clip name Astra takes (1-64 of A-Z a-z 0-9 _ . -, not starting with '.')", nameof(clip));
            foreach (var c in Captures)
                if (c.Param == param) throw new ArgumentException($"'{param}' is captured already (as '{c.Clip}')", nameof(param));
            Dictionary<string, string> map = null;
            if (bones != null)
            {
                map = new Dictionary<string, string>();
                foreach (var kv in bones)
                {
                    if (!Humanoid.IsBone(kv.Key))
                        throw new ArgumentException($"'{kv.Key}' is not a VRM humanoid bone a capture carries (hips, spine, leftUpperArm…; no eyes or jaw)", nameof(bones));
                    if (string.IsNullOrEmpty(kv.Value)) throw new ArgumentException($"the bone '{kv.Key}' names no transform", nameof(bones));
                    map[kv.Key] = kv.Value;
                }
                var missing = new List<string>();
                foreach (var r in Humanoid.Required)
                    if (!map.ContainsKey(r)) missing.Add(r);
                if (missing.Count > 0) throw new ArgumentException("a capture needs the bones " + string.Join(", ", missing), nameof(bones));
            }
            Captures.Add(new CaptureRequest(param, clip, player, map));
            return this;
        }
    }

    /// <summary>One <see cref="GameIntegration.CaptureWhile"/>: what to capture, while what, from where.</summary>
    internal sealed class CaptureRequest
    {
        public readonly string Param;
        public readonly string Clip;
        public readonly Func<Animator> Player;
        /// <summary>VRM bone → transform name, for a Generic rig; null for a humanoid Animator.</summary>
        public readonly Dictionary<string, string> Bones;

        public CaptureRequest(string param, string clip, Func<Animator> player, Dictionary<string, string> bones)
        {
            Param = param;
            Clip = clip;
            Player = player;
            Bones = bones;
        }
    }
}
