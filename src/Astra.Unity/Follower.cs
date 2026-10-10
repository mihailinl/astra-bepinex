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
            // BESIDE you — on the side she is already on, a little behind. Standing, she stays put
            // however you look around.
            if (!following && flat > s.FollowStart) following = true;
            var want = Vector3.zero;
            if (following)
            {
                var moving = new Vector3(playerVel.x, 0, playerVel.z);
                var dir = moving.magnitude > 1f ? moving.normalized : ahead;
                var right = new Vector3(dir.z, 0, -dir.x);
                float side = Vector3.Dot(her.Position - target, right) >= 0 ? 1 : -1;
                var spot = target + right * (side * s.KeepDistance * 0.8f) - dir * (s.KeepDistance * 0.6f);
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
            f.TrySpotNear(target, ahead, f.Follow.KeepDistance, out var at);
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
}
