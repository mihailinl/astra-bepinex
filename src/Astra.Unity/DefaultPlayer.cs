// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using Astra.Sdk;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The stock player LOCATOR, in rungs: the object the player named in the config; else the
    /// object tagged <c>Player</c> nearest the camera (in a multiplayer game, that is YOU); else a
    /// cheap SCORE over the scene — the camera's own ancestors (a first-person rig: the camera
    /// rides the player) first, then the nearest <see cref="CharacterController"/> or humanoid
    /// <see cref="Animator"/> within reach, preferring one in front of the camera; else the ground
    /// under the camera. Re-scored every ~2 s, never a per-frame scene scan.
    /// </summary>
    sealed class DefaultPlayer
    {
        /// <summary>How far a scored candidate (never the <c>Player</c> tag, which has its own
        /// range) may be from the camera.</summary>
        const float ScoreRange = 15f;

        GameObject player;
        CharacterController controller;
        Collider collider;
        string searchedFor;
        float nextSearch;
        static readonly HashSet<int> NoIgnore = new HashSet<int>();

        /// <summary>The last pick, for the one-line log when it changes (Driver owns the logging).</summary>
        public string Description = "the ground under the camera";

        /// <summary>The search as an optional part: its first failure switches it off, said once.</summary>
        readonly Feature search = new Feature("the default player search");

        /// <summary>The player: this locator's answer — or, once a failure has switched its search off
        /// (<see cref="Feature"/>, warned through <paramref name="warn"/>), <paramref name="previous"/>,
        /// the player picked before.</summary>
        public PlayerInfo? Pick(Camera cam, string configured, int mask, PlayerInfo? previous, Action<string> warn)
        {
            // Run every frame: the search's inputs and answer go through fields, and its body is made
            // once, so no closure is made per frame.
            picked = previous;
            askCam = cam;
            askConfigured = configured;
            askMask = mask;
            search.Run(locate ??= LocateAsked, warn);
            askCam = null;
            return picked;
        }

        PlayerInfo? picked;
        Camera askCam;
        string askConfigured;
        int askMask;
        Action locate;

        void LocateAsked() => picked = Locate(askCam, askConfigured, askMask);

        [MethodImpl(MethodImplOptions.NoInlining)]
        PlayerInfo? Locate(Camera cam, string configured, int mask)
        {
            // An EMPTY result waits its 2 s too: a scene with no player to find (a menu, a loading
            // screen) must not be scanned object by object every frame. A pick that went inactive is
            // looked for again at once; one that was destroyed reads as no pick, and waits.
            if (configured != searchedFor || Time.unscaledTime > nextSearch || (player != null && !player.activeInHierarchy))
            {
                nextSearch = Time.unscaledTime + 2f;
                searchedFor = configured;
                var found = Find(cam, configured, out string why);
                if (found != player)
                {
                    player = found;
                    controller = found != null ? Compat.Get<CharacterController>(found) : null;
                    collider = found != null && controller == null ? Compat.Get<Collider>(found) : null;
                    Description = found != null ? $"'{found.name}' ({why})" : "the ground under the camera";
                }
            }
            if (player != null) return BuildInfo(player, controller, collider);
            var eye = cam.transform.position;
            var feet = Compat.Raycast(eye, Vector3.down, 3f, mask, NoIgnore, out var ground) ? ground.point : eye + Vector3.down * 1.6f;
            return new PlayerInfo { Feet = feet, Grounded = true };
        }

        /// <summary>Feet/Forward/Grounded/Height from whichever of a <see cref="CharacterController"/>
        /// or a plain <see cref="Collider"/> the chosen object carries (neither: its transform
        /// position, an unknown height, and standing).</summary>
        static PlayerInfo BuildInfo(GameObject go, CharacterController cc, Collider col)
        {
            Vector3 feet = cc != null ? BoundsFeet(cc.bounds) : col != null ? BoundsFeet(col.bounds) : go.transform.position;
            float height = cc != null ? cc.height * cc.transform.lossyScale.y : col != null ? col.bounds.size.y : 0f;
            return new PlayerInfo
            {
                Feet = feet,
                Forward = FollowBrain.Horizontal(go.transform.forward),
                Root = go, // the candidate's OWN hierarchy, never the scene root
                // A CharacterController knows only if the game moves it with Move(); a game that
                // moves the player otherwise (its own Rigidbody, the transform) leaves isGrounded
                // false forever — so ground right under the feet counts too.
                Grounded = cc == null || cc.isGrounded || GroundUnder(feet),
                Height = height,
            };
        }

        static Vector3 BoundsFeet(Bounds b) => new Vector3(b.center.x, b.min.y, b.center.z);

        /// <summary>Walkable ground within 0.3 m under <paramref name="feet"/> (the ray starts inside the
        /// player's own collider, which a ray never hits).</summary>
        static bool GroundUnder(Vector3 feet) =>
            Physics.Raycast(feet + Vector3.up * 0.1f, Vector3.down, out var hit, 0.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
            && hit.normal.y > 0.5f;

        static GameObject Find(Camera cam, string configured, out string why)
        {
            if (!string.IsNullOrEmpty(configured)) { why = "configured"; return GameObject.Find(configured); }
            var tagged = TaggedPlayer(cam);
            if (tagged != null) { why = "tagged Player"; return tagged; }
            var ancestor = AncestorPlayer(cam, out why);
            if (ancestor != null) return ancestor;
            var cc = NearestController(cam, ScoreRange);
            if (cc != null) { why = "nearest CharacterController"; return cc; }
            var humanoid = NearestHumanoid(cam, ScoreRange);
            if (humanoid != null) { why = "nearest humanoid Animator ahead of the camera"; return humanoid; }
            why = null;
            return null;
        }

        static GameObject TaggedPlayer(Camera cam)
        {
            GameObject found = null;
            float best = 15f;
            foreach (var g in Compat.WithTag("Player"))
            {
                if (!g.activeInHierarchy) continue;
                float d = Vector3.Distance(g.transform.position, cam.transform.position);
                if (d < best)
                {
                    best = d;
                    found = g;
                }
            }
            return found;
        }

        /// <summary>First person: the camera itself rides the player. Walk UP from the camera (never
        /// down into the whole scene) for the nearest ancestor with a <see cref="CharacterController"/>,
        /// a <see cref="Rigidbody"/>+<see cref="Collider"/>, or a humanoid <see cref="Animator"/>.</summary>
        static GameObject AncestorPlayer(Camera cam, out string why)
        {
            for (var t = cam.transform.parent; t != null; t = t.parent)
            {
                var go = t.gameObject;
                if (Compat.Get<CharacterController>(go) != null)
                {
                    why = "the camera's ancestor has a CharacterController";
                    return go;
                }
                if (Compat.Get<Rigidbody>(go) != null && Compat.Get<Collider>(go) != null)
                {
                    why = "the camera's ancestor has a Rigidbody + Collider";
                    return go;
                }
                var anim = Compat.Get<Animator>(go);
                if (anim != null && anim.isHuman)
                {
                    why = "the camera's ancestor has a humanoid Animator";
                    return go;
                }
            }
            why = null;
            return null;
        }

        /// <summary>Third person: every <see cref="CharacterController"/> within <paramref name="maxDist"/>
        /// of the camera, preferring one AHEAD of it, then the nearest. Scanned only when this
        /// locator re-searches (every ~2 s), never per frame.</summary>
        static GameObject NearestController(Camera cam, float maxDist)
        {
            GameObject found = null;
            float bestScore = float.MaxValue;
            var pos = cam.transform.position;
            var fwd = cam.transform.forward;
            foreach (var cc in Compat.FindAll<CharacterController>())
            {
                if (cc == null || !cc.enabled || !cc.gameObject.activeInHierarchy) continue;
                var to = cc.transform.position - pos;
                float dist = to.magnitude;
                if (dist > maxDist) continue;
                float score = (float)LocatorScoring.CandidateScore(dist, Vector3.Dot(fwd, to) > 0, maxDist);
                if (score < bestScore)
                {
                    bestScore = score;
                    found = cc.gameObject;
                }
            }
            return found;
        }

        /// <summary>No <see cref="CharacterController"/> anywhere (root-motion humanoids): the
        /// nearest humanoid <see cref="Animator"/> ahead of the camera within <paramref name="maxDist"/>.</summary>
        static GameObject NearestHumanoid(Camera cam, float maxDist)
        {
            GameObject found = null;
            float bestDist = float.MaxValue;
            var pos = cam.transform.position;
            var fwd = cam.transform.forward;
            foreach (var anim in Compat.FindAll<Animator>())
            {
                if (anim == null || !anim.enabled || !anim.isHuman || !anim.gameObject.activeInHierarchy) continue;
                var to = anim.transform.position - pos;
                if (Vector3.Dot(fwd, to) <= 0) continue;
                float dist = to.magnitude;
                if (dist > maxDist || dist >= bestDist) continue;
                bestDist = dist;
                found = anim.gameObject;
            }
            return found;
        }
    }
}
