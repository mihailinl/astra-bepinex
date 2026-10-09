// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Astra.Bridge;
using Astra.Sdk;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The stock BRAIN: a companion that keeps near the player through the GAME's own physics —
    /// Unity's colliders are the same in every Unity game, so this needs no per-game code. She stays
    /// put while you are close, walks after you when you go (to a spot beside you, never between you
    /// and a camera that follows you), slides along walls, steps up and down, falls off ledges, and
    /// appears near you when you are far or she is stuck. An integration's brain may call it for the
    /// frames it has nothing better to do (<see cref="FrameContext.Default"/>).
    /// </summary>
    sealed class FollowBrain : IBrain
    {
        const float Gravity = 9.81f;
        const float Radius = 0.25f;
        const float Height = 1.6f;
        const float StepUp = 0.45f;   // her capsule starts this high, so a stair or a kerb never blocks her
        const float StepDown = 0.6f;  // she follows the ground this far down before she counts as falling
        const float Skin = 0.02f;

        Vector3 vel;
        bool following, havePlayer;
        float airTime, stuckTime;
        Vector3 lastPlayer, playerVel;
        int lastFrame = -10;

        public void Step(FrameContext f)
        {
            float dt = Mathf.Min(f.DeltaTime, 0.1f);
            if (dt <= 0 || f.Player == null) return;
            // Skipped for a frame or more (an integration's brain had her — a climb, a leap)? What
            // it remembers is stale: the player's "speed" would be a whole climb in one frame.
            if (Time.frameCount != lastFrame + 1) Reseed();
            lastFrame = Time.frameCount;
            var her = f.Her;
            var s = f.Follow;
            var player = f.Player.Value;
            var target = player.Feet;
            var ahead = Horizontal(player.Forward);
            if (ahead == Vector3.zero) ahead = Horizontal(f.Camera.transform.forward);
            if (ahead == Vector3.zero) ahead = Vector3.forward;
            if (!havePlayer) lastPlayer = target;
            havePlayer = true;
            playerVel = Vector3.Lerp(playerVel, (target - lastPlayer) / dt, 1 - Mathf.Exp(-dt * 8));
            lastPlayer = target;

            var toPlayer = target - her.Position;
            float flat = new Vector3(toPlayer.x, 0, toPlayer.z).magnitude;
            // Too far, stuck, or far above/below a player who STANDS (one mid-jump or mid-climb lands
            // somewhere first — a brain that knows the game handles that); and whatever the player
            // does, never a fall out of the world: more than the teleport distance below or above,
            // or falling for 3 s (Extermination Ship: placed during a cutscene over nothing, she fell
            // for 250 m under a player its own physics moves, whom a CharacterController never calls
            // grounded).
            if (!her.Placed || flat > s.TeleportDistance || Mathf.Abs(toPlayer.y) > s.TeleportDistance
                || (player.Grounded && Mathf.Abs(toPlayer.y) > 8f) || stuckTime > 2f || airTime > 3f)
            {
                Teleport(f, target, ahead);
                return;
            }

            // Follow with hysteresis: she starts when you are FollowStart away, and walks to a spot
            // BESIDE you — on the side she is already on, a little behind; in a FIRST-PERSON game a
            // little AHEAD instead, where you can see her (behind, she was never on screen — the
            // owner in ULTRAKILL). Standing, she stays put however you look around.
            if (!following && flat > s.FollowStart) following = true;
            var want = Vector3.zero;
            if (following)
            {
                var moving = new Vector3(playerVel.x, 0, playerVel.z);
                var dir = moving.magnitude > 1f ? moving.normalized : ahead;
                var right = new Vector3(dir.z, 0, -dir.x);
                float side = Vector3.Dot(her.Position - target, right) >= 0 ? 1 : -1;
                var spot = target + right * (side * s.KeepDistance * 0.8f) + dir * (s.KeepDistance * (f.FirstPerson ? 0.8f : -0.6f));
                var toSpot = new Vector3(spot.x - her.Position.x, 0, spot.z - her.Position.z);
                float d = toSpot.magnitude;
                if (d < 0.25f && moving.magnitude < 0.3f) following = false;
                else if (d > 1e-3f)
                    want = toSpot / d * Mathf.Min(s.MaxSpeed, Mathf.Max(d * 1.5f, moving.magnitude));
            }
            var horizontal = Vector3.MoveTowards(new Vector3(vel.x, 0, vel.z), want, 10f * dt);
            var moved = Slide(f, horizontal * dt);
            stuckTime = horizontal.magnitude > 0.5f && moved.magnitude < 0.1f * horizontal.magnitude * dt ? stuckTime + dt : 0;
            her.Position += moved;
            vel = new Vector3(moved.x / dt, vel.y, moved.z / dt);
            Ground(f, dt);
            her.Airborne = airTime > 0.12f;

            // Face where she goes; standing, turn toward you once you are well off to her side.
            float speed = new Vector3(vel.x, 0, vel.z).magnitude;
            var face = speed > 0.3f ? new Vector3(vel.x, 0, vel.z) : new Vector3(toPlayer.x, 0, toPlayer.z);
            if (face.sqrMagnitude > 1e-4f && (speed > 0.3f || Vector3.Angle(her.Facing, face) > 35f))
                her.Facing = Vector3.RotateTowards(her.Facing, face.normalized, 4.7f * dt, 0f);
        }

        /// <summary>Move her capsule by <paramref name="step"/>, sliding along what it meets.</summary>
        Vector3 Slide(FrameContext f, Vector3 step)
        {
            var moved = Vector3.zero;
            var at = f.Her.Position;
            for (int i = 0; i < 2 && step.sqrMagnitude > 1e-8f; i++)
            {
                float len = step.magnitude;
                var dir = step / len;
                var p1 = at + moved + Vector3.up * (StepUp + Radius);
                var p2 = at + moved + Vector3.up * (Height - Radius);
                if (!f.CapsuleCast(p1, p2, Radius, dir, len + Skin, out var hit))
                    return moved + step;
                float free = Mathf.Max(0, hit.distance - Skin);
                moved += dir * free;
                var n = new Vector3(hit.normal.x, 0, hit.normal.z);
                if (n.sqrMagnitude < 1e-6f) break;
                n.Normalize();
                var rest = step - dir * free;
                step = rest - n * Vector3.Dot(rest, n);
            }
            return moved;
        }

        /// <summary>Stick to the ground below her, or fall.</summary>
        void Ground(FrameContext f, float dt)
        {
            var her = f.Her;
            float reach = StepUp + StepDown + Mathf.Max(0, -vel.y * dt);
            bool hit = f.Raycast(her.Position + Vector3.up * StepUp, Vector3.down, reach, out var ground);
            if (hit && vel.y <= 0.01f && ground.normal.y > 0.5f)
            {
                her.Position.y = ground.point.y;
                vel.y = 0;
                airTime = 0;
                return;
            }
            vel.y -= Gravity * dt;
            her.Position.y += vel.y * dt;
            airTime += dt;
            if (hit && her.Position.y < ground.point.y)
            {
                her.Position.y = ground.point.y;
                vel.y = 0;
            }
        }

        /// <summary>A spot near the player she can stand on (behind first, then around); only when
        /// there is none at all, the player's own feet.</summary>
        void Teleport(FrameContext f, Vector3 target, Vector3 ahead)
        {
            var her = f.Her;
            f.TrySpotNear(target, ahead, f.Follow.KeepDistance, f.FirstPerson, out var at);
            her.Position = at;
            her.Facing = ahead;
            her.Placed = true;
            her.Airborne = false;
            vel = Vector3.zero;
            following = false;
            stuckTime = 0;
            airTime = 0;
        }

        /// <summary><see cref="FrameContext.Hold"/>: stay put, obey gravity, turn toward a point.</summary>
        public void Hold(FrameContext f, Vector3 lookAt)
        {
            float dt = Mathf.Min(f.DeltaTime, 0.1f);
            if (dt <= 0 || !f.Her.Placed) return;
            // Not a Step: the next Step sees the gap and reseeds what it knows of the player.
            vel.x = vel.z = 0;
            following = false;
            Ground(f, dt);
            f.Her.Airborne = airTime > 0.12f;
            var face = lookAt - f.Her.Position;
            face.y = 0;
            if (face.sqrMagnitude > 1e-4f && Vector3.Angle(f.Her.Facing, face) > 20f)
                f.Her.Facing = Vector3.RotateTowards(f.Her.Facing, face.normalized, 4.7f * dt, 0f);
        }

        void Reseed()
        {
            havePlayer = false;
            playerVel = Vector3.zero;
            following = false;
            stuckTime = 0;
            vel.x = vel.z = 0;
        }

        /// <summary>Shared with <see cref="DefaultPlayer"/>: an object's forward, flattened (zero
        /// when it points straight up/down — the caller falls back to the camera's).</summary>
        internal static Vector3 Horizontal(Vector3 v)
        {
            v.y = 0;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }
    }

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

        public PlayerInfo? Locate(Camera cam, string configured, int mask)
        {
            if (player == null || !player.activeInHierarchy || Time.unscaledTime > nextSearch || configured != searchedFor)
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

    /// <summary>
    /// The stock CAMERA locator, when no integration names one: <c>Camera.main</c> while it
    /// renders to the screen; else the enabled camera with no render-texture target and the
    /// highest depth — ties broken by whichever moved most recently (an idle security camera loses
    /// to the one the player is steering), then by the larger viewport. Re-scored every ~2 s and
    /// kept while it still renders to the screen, so a momentary <c>Camera.main</c> hiccup never
    /// flickers her view.
    /// </summary>
    sealed class DefaultCamera
    {
        struct Seen { public Vector3 Pos; public Quaternion Rot; public float Since; }

        Camera current;
        float nextSearch;
        readonly Dictionary<int, Seen> moved = new Dictionary<int, Seen>();

        /// <summary>The last pick, for the one-line log when it changes (Driver owns the logging).</summary>
        public string Description = "none";

        public Camera Locate()
        {
            if (ToScreen(current) && Time.unscaledTime < nextSearch) return current;
            nextSearch = Time.unscaledTime + 2f;
            Camera found;
            string why;
            // Camera.main, unless it is plainly not the world's camera: a UI camera tagged MainCamera,
            // a URP overlay, one not rendering. An orthographic Camera.main stays (an isometric game's).
            var tagged = Camera.main;
            if (ToScreen(tagged) && Rank(tagged) > 0 && (tagged.cullingMask & ~LocatorScoring.UiLayerBit) != 0) { found = tagged; why = "Camera.main"; }
            else { found = Best(); why = found != null && Rank(found) == 2 ? "the camera that draws the world" : "highest-depth camera with no render target"; }
            if (found != current)
            {
                current = found;
                Description = found != null ? $"'{found.name}' ({why})" : "none";
                Inventory = Describe();
            }
            return current;
        }

        /// <summary>Every camera this pick weighed, one per line — logged with a pick, so a tester's log
        /// shows what the scene offered when the pick is wrong.</summary>
        public string Inventory = "";

        /// <summary>A camera began rendering (any camera, every frame): what <see cref="Rank"/> calls
        /// "seen rendering".</summary>
        public void Rendered(Camera c)
        {
            if (c == null) return;
            renderedAt[c.GetInstanceID()] = Time.frameCount;
            Prune();
        }

        struct Weighed { public int FirstFrame; public float LastTime; }

        readonly Dictionary<int, int> renderedAt = new Dictionary<int, int>();
        readonly Dictionary<int, Weighed> weighed = new Dictionary<int, Weighed>();
        readonly Dictionary<int, bool> overlay = new Dictionary<int, bool>();
        int prunedAt;

        /// <summary>
        /// <see cref="LocatorScoring.CameraRank"/> for <paramref name="c"/>. "Rendering" is given the
        /// benefit of the doubt: a camera rendered within 30 frames, or one weighed FRESH within 30
        /// frames — first seen, or back after more than 3 s unweighed (a disabled camera is never
        /// listed). A scene's new Camera.main, or a gameplay camera switched back on after a cutscene,
        /// is picked in the LateUpdate BEFORE its first render, and must not lose to a UI camera.
        /// </summary>
        int Rank(Camera c)
        {
            int id = c.GetInstanceID(), now = Time.frameCount;
            float t = Time.unscaledTime;
            if (!weighed.TryGetValue(id, out var w) || t - w.LastTime > 3f) w.FirstFrame = now;
            w.LastTime = t;
            weighed[id] = w;
            bool rendering = (renderedAt.TryGetValue(id, out int at) && now - at <= 30) || now - w.FirstFrame < 30;
            return LocatorScoring.CameraRank(rendering, Overlay(c), c.orthographic, c.cullingMask);
        }

        /// <summary>Forget cameras long gone — not rendered for 300 frames, not weighed for 10 s — at
        /// most once a second (by frames), never the live ones.</summary>
        void Prune()
        {
            int now = Time.frameCount;
            if (renderedAt.Count + weighed.Count <= 128 || now - prunedAt < 60) return;
            prunedAt = now;
            float t = Time.unscaledTime;
            var stale = new List<int>();
            foreach (var kv in renderedAt)
                if (now - kv.Value > 300) stale.Add(kv.Key);
            foreach (var id in stale) renderedAt.Remove(id);
            stale.Clear();
            foreach (var kv in weighed)
                if (t - kv.Value.LastTime > 10f) stale.Add(kv.Key);
            foreach (var id in stale) weighed.Remove(id);
        }

        /// <summary>A URP Overlay camera (its <c>UniversalAdditionalCameraData.renderType</c>, by
        /// reflection — the plugin does not depend on URP; never known under IL2CPP).</summary>
        bool Overlay(Camera c)
        {
            int id = c.GetInstanceID();
            if (overlay.TryGetValue(id, out bool yes)) return yes;
            yes = Compat.IsUrpOverlay(c);
            if (overlay.Count > 64) overlay.Clear();
            overlay[id] = yes;
            return yes;
        }

        static bool ToScreen(Camera c) => c != null && c.isActiveAndEnabled && c.targetTexture == null;

        Camera Best()
        {
            Camera found = null;
            int bestRank = 0;
            float bestDepth = 0, bestSince = 0, bestArea = 0;
            float now = Time.unscaledTime;
            foreach (var c in Camera.allCameras)
            {
                if (!ToScreen(c)) continue;
                int rank = Rank(c);
                float since = Track(c, now);
                var r = c.pixelRect;
                float area = r.width * r.height;
                if (found != null && !LocatorScoring.BetterCamera(rank, c.depth, since, area, bestRank, bestDepth, bestSince, bestArea))
                    continue;
                found = c;
                bestRank = rank;
                bestDepth = c.depth;
                bestSince = since;
                bestArea = area;
            }
            return found;
        }

        string Describe()
        {
            var lines = new System.Text.StringBuilder();
            foreach (var c in Camera.allCameras)
            {
                if (c == null) continue;
                lines.Append($"\n    '{c.name}': rank {Rank(c)}, depth {c.depth:0.##}, {(c.orthographic ? "orthographic" : $"fov {c.fieldOfView:0}")}, " +
                    $"mask 0x{c.cullingMask:x}, {(c.targetTexture != null ? "into a texture" : "to the screen")}{(Overlay(c) ? ", URP overlay" : "")}" +
                    $"{(c == Camera.main ? ", MainCamera" : "")}");
            }
            return lines.ToString();
        }

        /// <summary>When <paramref name="c"/> last moved (now, the first time it is seen, so a
        /// brand-new camera is not starved behind idle ones).</summary>
        float Track(Camera c, float now)
        {
            int id = c.GetInstanceID();
            var t = c.transform;
            if (moved.TryGetValue(id, out var prev))
            {
                if (prev.Pos != t.position || prev.Rot != t.rotation)
                {
                    moved[id] = new Seen { Pos = t.position, Rot = t.rotation, Since = now };
                    return now;
                }
                return prev.Since;
            }
            if (moved.Count > 64) moved.Clear(); // a scene churning through cameras: forget and restart
            moved[id] = new Seen { Pos = t.position, Rot = t.rotation, Since = now };
            return now;
        }
    }
}
