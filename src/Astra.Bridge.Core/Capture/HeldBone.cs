// SPDX-License-Identifier: MIT

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// A bone of the game's rig that a capture HOLDS STILL: one the game turns for its own reasons
    /// (PEAK's <c>AimJoint</c> turns the upper body with where the player looks) and that is not the
    /// move. Leaving it out of the bone map is not enough — a capture reads world rotations, so its turn
    /// reaches every bone below it. Each frame its turn away from its bind pose (relative to its
    /// parent) is taken back out of everything below it: <see cref="Undo"/>, left-multiplied.
    /// </summary>
    public sealed class HeldBone
    {
        /// <summary>Its name, for the log.</summary>
        public readonly string Name;

        /// <summary>Its rotation relative to its parent at bind time: what "still" means.</summary>
        public readonly Quat BindLocal;

        /// <summary>The captured bones below it (indices in the rig's bone order).</summary>
        public readonly int[] Below;

        /// <summary>The largest turn taken out since <see cref="ResetMost"/>, in degrees.</summary>
        public double Most { get; private set; }

        public HeldBone(string name, Quat bindLocal, int[] below)
        {
            Name = name;
            BindLocal = bindLocal.Normalized;
            Below = below;
        }

        /// <summary>The rotation that takes a bone whose parent is at <paramref name="parentWorld"/>
        /// from where the game turned it (<paramref name="boneWorld"/>) back to its bind pose under
        /// that parent: <c>(parentWorld · bindLocal) · boneWorld⁻¹</c>.</summary>
        public static Quat Undo(Quat parentWorld, Quat boneWorld, Quat bindLocal) =>
            (parentWorld.Normalized * bindLocal * boneWorld.Normalized.Inverse).Normalized;

        /// <summary>
        /// Take this frame's turn out of the bones below: each one's world rotation is left-multiplied
        /// by <see cref="Undo"/>, and its position turned about the held bone's
        /// (<paramref name="bonePos"/>). Returns the correction (for a held bone further down, whose own
        /// parent and rotation it applies to first).
        /// </summary>
        public Quat Apply(Quat parentWorld, Quat boneWorld, Vec3 bonePos, Quat[] world, Vec3[] positions)
        {
            var c = Undo(parentWorld, boneWorld, BindLocal);
            double angle = Quat.Angle(Quat.Identity, c);
            if (angle > Most) Most = angle;
            foreach (int i in Below)
            {
                world[i] = (c * world[i]).Normalized;
                positions[i] = bonePos + c.Rotate(positions[i] - bonePos);
            }
            return c;
        }

        /// <summary>Start measuring <see cref="Most"/> afresh (a new take).</summary>
        public void ResetMost() => Most = 0;
    }
}
