// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge.Capture
{
    /// <summary>One humanoid bone of a game's rig at REST, in its character root's frame (Unity's
    /// axes): its world rotation and position relative to that root.</summary>
    public struct RestBone
    {
        /// <summary>The VRM humanoid name (<see cref="Humanoid.Bones"/>).</summary>
        public string Name;
        public Quat Rotation;
        public Vec3 Position;

        public RestBone(string name, Quat rotation, Vec3 position)
        {
            Name = name;
            Rotation = rotation;
            Position = position;
        }
    }

    /// <summary>
    /// A game character's humanoid skeleton, made ready to be captured: which VRM bones it has, its
    /// rest pose turned into the VRM T-pose, and the step that turns one frame of the game's pose into
    /// VRM NORMALIZED local rotations — the space a <c>.vrma</c> carries (three-vrm's normalized
    /// humanoid: every bone's rest rotation is the identity, the body faces +Z, arms along X, legs
    /// down). No Unity here: the foundation reads the rig's transforms and hands this plain numbers.
    /// <para>
    /// The game's pose is taken DIRECTLY, bone by bone, against its rest — never through a muscle
    /// round trip (<c>HumanPoseHandler</c>), which needs a Unity <c>Avatar</c> many games do not have
    /// (a Generic rig: PEAK's scouts) and drifts. For bone <c>B</c> with live rotation <c>L</c> and rest
    /// rotation <c>R</c> (both relative to the character's root), its normalized world rotation is
    /// <c>N = L · R⁻¹</c> and its normalized LOCAL rotation <c>N(parent)⁻¹ · N</c>, the parent being
    /// the nearest humanoid ancestor the rig has (three-vrm's normalization).
    /// </para>
    /// <para>
    /// The rest is made a T-pose first, so a rig bound in an A-pose does not raise her arms by its
    /// angle: the facing is turned to +Z (from the side-to-side axis of the legs), and the arms and
    /// legs are swung straight (upper arm, forearm and hand along ∓X, thigh and shin down), each with
    /// what hangs below it. The clip's origin is the floor under the rest hips, so its rest hips
    /// height is the hips above the feet whatever the root's own origin.
    /// </para>
    /// </summary>
    public sealed class CaptureSkeleton
    {
        /// <summary>The VRM bones this rig has, parents first (<see cref="Humanoid.Bones"/>' order);
        /// index 0 is <c>hips</c>.</summary>
        public readonly string[] Bones;

        /// <summary>For each bone, the index of its nearest humanoid ancestor in <see cref="Bones"/>;
        /// −1 for the hips.</summary>
        public readonly int[] Parents;

        /// <summary>Each bone's T-pose position, in the clip's frame (Unity's axes): the floor under
        /// the rest hips is the origin, +Z the body's front.</summary>
        public readonly Vec3[] RestPositions;

        /// <summary>The turn about +Y that faces the rig to +Z (applied to every live frame).</summary>
        public readonly Quat Facing;

        /// <summary>How far the rest pose was swung to a T-pose: the largest swing, in degrees, and the
        /// bone it was for (null when nothing moved more than a degree).</summary>
        public readonly double Straightened;
        public readonly string StraightenedBone;

        readonly Quat[] restInverse;
        readonly Vec3 origin;

        /// <summary>The bone's index in <see cref="Bones"/>, or −1.</summary>
        public int IndexOf(string bone) => Array.IndexOf(Bones, bone);

        CaptureSkeleton(string[] bones, int[] parents, Quat[] rest, Vec3[] positions, Quat facing, Vec3 origin, double straightened, string straightenedBone)
        {
            Bones = bones;
            Parents = parents;
            Facing = facing;
            this.origin = origin;
            Straightened = straightened;
            StraightenedBone = straightenedBone;
            restInverse = new Quat[rest.Length];
            RestPositions = new Vec3[positions.Length];
            for (int i = 0; i < rest.Length; i++)
            {
                restInverse[i] = rest[i].Inverse;
                RestPositions[i] = positions[i] - origin;
            }
        }

        /// <summary>The direction each straightened bone points at in a T-pose (Unity's axes, facing
        /// +Z: the character's left is −X), and the bone(s) whose position gives its own direction.</summary>
        static readonly (string Bone, Vec3 Target, string[] Toward)[] Straighten =
        {
            ("leftUpperArm", new Vec3(-1, 0, 0), new[] { "leftLowerArm" }),
            ("leftLowerArm", new Vec3(-1, 0, 0), new[] { "leftHand" }),
            ("leftHand", new Vec3(-1, 0, 0), new[] { "leftMiddleProximal", "leftIndexProximal", "leftRingProximal" }),
            ("rightUpperArm", new Vec3(1, 0, 0), new[] { "rightLowerArm" }),
            ("rightLowerArm", new Vec3(1, 0, 0), new[] { "rightHand" }),
            ("rightHand", new Vec3(1, 0, 0), new[] { "rightMiddleProximal", "rightIndexProximal", "rightRingProximal" }),
            ("leftUpperLeg", new Vec3(0, -1, 0), new[] { "leftLowerLeg" }),
            ("leftLowerLeg", new Vec3(0, -1, 0), new[] { "leftFoot" }),
            ("rightUpperLeg", new Vec3(0, -1, 0), new[] { "rightLowerLeg" }),
            ("rightLowerLeg", new Vec3(0, -1, 0), new[] { "rightFoot" }),
        };

        /// <summary>
        /// The skeleton of a rig whose rest bones are <paramref name="rest"/>; null with
        /// <paramref name="why"/> when it cannot be captured: a required bone missing
        /// (<see cref="Humanoid.Required"/>), a name that is not a VRM bone or given twice, a non-finite
        /// number, or a rig that is not upright in its root's frame.
        /// </summary>
        public static CaptureSkeleton Build(IList<RestBone> rest, out string why)
        {
            var given = new Dictionary<string, RestBone>();
            foreach (var b in rest)
            {
                if (!Humanoid.IsBone(b.Name)) { why = $"'{b.Name}' is not a VRM humanoid bone"; return null; }
                if (given.ContainsKey(b.Name)) { why = $"the bone '{b.Name}' is given twice"; return null; }
                if (!b.Rotation.IsFinite || !b.Position.IsFinite || !(Math.Sqrt(b.Rotation.Dot(b.Rotation)) > 1e-6))
                {
                    why = $"the rest pose of '{b.Name}' is not a finite rotation and position";
                    return null;
                }
                given[b.Name] = b;
            }
            var missing = new List<string>();
            foreach (var r in Humanoid.Required)
                if (!given.ContainsKey(r)) missing.Add(r);
            if (missing.Count > 0)
            {
                why = "it lacks the humanoid bones " + string.Join(", ", missing);
                return null;
            }

            var names = new List<string>();
            foreach (var b in Humanoid.Bones)
                if (given.ContainsKey(b)) names.Add(b);
            int n = names.Count;
            var parents = new int[n];
            var rot = new Quat[n];
            var pos = new Vec3[n];
            for (int i = 0; i < n; i++)
            {
                string p = Humanoid.PresentParent(names[i], given.Keys);
                parents[i] = p == null ? -1 : names.IndexOf(p);
                rot[i] = given[names[i]].Rotation.Normalized;
                pos[i] = given[names[i]].Position;
            }

            // Face +Z: the legs' side-to-side axis (left thigh minus right thigh) points at −X.
            var lateral = pos[names.IndexOf("leftUpperLeg")] - pos[names.IndexOf("rightUpperLeg")];
            var facing = Yaw(lateral, new Vec3(-1, 0, 0));
            for (int i = 0; i < n; i++)
            {
                rot[i] = facing * rot[i];
                pos[i] = facing.Rotate(pos[i]);
            }
            var up = pos[names.IndexOf("head")] - pos[0];
            if (!(up.Length > 1e-6) || up.Y < up.Length * 0.5)
            {
                why = "its head is not above its hips in its root's frame (the rig is not upright)";
                return null;
            }

            // Swing the limbs straight, each with what hangs below it.
            double most = 0;
            string mostBone = null;
            foreach (var (bone, target, toward) in Straighten)
            {
                int b = names.IndexOf(bone);
                if (b < 0) continue;
                int c = -1;
                foreach (var t in toward)
                    if ((c = names.IndexOf(t)) >= 0) break;
                if (c < 0) continue;
                var swing = Quat.FromTo(pos[c] - pos[b], target);
                double angle = Quat.Angle(Quat.Identity, swing);
                if (angle > most)
                {
                    most = angle;
                    mostBone = bone;
                }
                for (int d = 0; d < n; d++)
                {
                    if (d != b && !Descends(parents, d, b)) continue;
                    rot[d] = (swing * rot[d]).Normalized;
                    if (d != b) pos[d] = pos[b] + swing.Rotate(pos[d] - pos[b]);
                }
            }

            // The clip's origin: the floor (the lowest foot or toe) under the rest hips.
            double floor = double.MaxValue;
            foreach (var f in new[] { "leftFoot", "rightFoot", "leftToes", "rightToes" })
            {
                int i = names.IndexOf(f);
                if (i >= 0) floor = Math.Min(floor, pos[i].Y);
            }
            var origin = new Vec3(pos[0].X, floor, pos[0].Z);
            if (!(pos[0].Y - floor > 1e-3))
            {
                why = "its hips are not above its feet at rest";
                return null;
            }
            why = null;
            return new CaptureSkeleton(names.ToArray(), parents, rot, pos, facing, origin, most > 1 ? most : 0, most > 1 ? mostBone : null);
        }

        /// <summary>Does bone <paramref name="d"/> hang below bone <paramref name="b"/>?</summary>
        static bool Descends(int[] parents, int d, int b)
        {
            for (int p = parents[d]; p >= 0; p = parents[p])
                if (p == b) return true;
            return false;
        }

        /// <summary>The turn about +Y taking <paramref name="from"/>'s horizontal part onto
        /// <paramref name="to"/>'s (identity when either has none).</summary>
        static Quat Yaw(Vec3 from, Vec3 to)
        {
            var a = new Vec3(from.X, 0, from.Z);
            var b = new Vec3(to.X, 0, to.Z);
            if (!(a.Length > 1e-9) || !(b.Length > 1e-9)) return Quat.Identity;
            double dot = (a.X * b.X + a.Z * b.Z) / (a.Length * b.Length);
            if (dot < -0.999999) return Quat.AngleAxis(180, new Vec3(0, 1, 0));
            return Quat.FromTo(a, b);
        }

        /// <summary>
        /// One frame of the game's pose → VRM normalized local rotations. <paramref name="live"/> holds
        /// each bone's rotation relative to the character's root (Unity's axes), in
        /// <see cref="Bones"/>' order; <paramref name="hips"/> the hips' position relative to that root.
        /// Writes <paramref name="local"/> (one per bone) and returns the hips' position in the clip's
        /// frame (Unity's axes). False, writing nothing, when a number is not finite.
        /// </summary>
        public bool Normalize(Quat[] live, Vec3 hips, Quat[] local, out Vec3 hipsOut)
        {
            hipsOut = default;
            if (live == null || local == null || live.Length < Bones.Length || local.Length < Bones.Length || !hips.IsFinite) return false;
            var world = new Quat[Bones.Length];
            for (int i = 0; i < Bones.Length; i++)
            {
                if (!live[i].IsFinite) return false;
                world[i] = (Facing * live[i].Normalized * restInverse[i]).Normalized;
            }
            for (int i = 0; i < Bones.Length; i++)
                local[i] = Parents[i] < 0 ? world[i] : (world[Parents[i]].Inverse * world[i]).Normalized;
            hipsOut = Facing.Rotate(hips) - origin;
            return true;
        }
    }
}
