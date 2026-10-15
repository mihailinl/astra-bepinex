// SPDX-License-Identifier: MIT

namespace Astra.Bridge.Capture
{
    /// <summary>Where a capture reads the game's pose from: a rig the foundation found.</summary>
    public interface IPoseSampler
    {
        /// <summary>The rig's skeleton (built once, from its rest pose relative to its root).</summary>
        CaptureSkeleton Skeleton { get; }

        /// <summary>This frame's pose IN THE WORLD: each bone's rotation and position
        /// (<see cref="CaptureSkeleton.Bones"/>' order, Unity's axes) and the character root's own pose.
        /// False when there is no pose now (the character is gone).</summary>
        bool Sample(Quat[] world, Vec3[] positions, out RootPose root);
    }

    /// <summary>
    /// The character root's pose in the world (its transform: rotation, position, scale). A take is
    /// measured in the root's frame AS IT WAS WHEN THE TAKE BEGAN (<see cref="ToTakeFrame"/>), never in
    /// its frame of the moment: a game that turns its character's root with the camera (PEAK, while
    /// the body faces the wall it climbs) would otherwise put the player's mouse into the clip.
    /// </summary>
    public struct RootPose
    {
        public Quat Rotation;
        public Vec3 Position;
        /// <summary>The root's scale (its <c>lossyScale</c>): positions are measured in the root's own
        /// units, as its rest pose is.</summary>
        public Vec3 Scale;

        public RootPose(Quat rotation, Vec3 position, Vec3 scale)
        {
            Rotation = rotation;
            Position = position;
            Scale = scale;
        }

        public bool IsFinite => Rotation.IsFinite && Position.IsFinite && Scale.IsFinite
            && System.Math.Abs(Scale.X) > 1e-9 && System.Math.Abs(Scale.Y) > 1e-9 && System.Math.Abs(Scale.Z) > 1e-9;

        /// <summary>
        /// World rotations and positions → the take's frame: the root's rotation <paramref name="frozen"/>
        /// (taken when the take began), its units, and — for positions, whose differences are all a
        /// take uses — its position now (<paramref name="now"/>). Writes <paramref name="liveOut"/> and
        /// <paramref name="posOut"/>.
        /// </summary>
        public static void ToTakeFrame(Quat frozen, RootPose now, Quat[] world, Vec3[] positions, int count, Quat[] liveOut, Vec3[] posOut)
        {
            var inv = frozen.Normalized.Inverse;
            for (int i = 0; i < count; i++)
            {
                liveOut[i] = inv * world[i];
                var d = inv.Rotate(positions[i] - now.Position);
                posOut[i] = new Vec3(d.X / now.Scale.X, d.Y / now.Scale.Y, d.Z / now.Scale.Z);
            }
        }
    }
}
