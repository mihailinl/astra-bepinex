// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// A captured take: the skeleton it was sampled from, and one frame per sample — when it was taken
    /// (seconds from the take's start, increasing) and each bone's VRM normalized local rotation with
    /// the hips' position (<see cref="CaptureSkeleton.Normalize"/>, Unity's axes). The frames are kept
    /// in ONE growing buffer (no allocation per sample once it has grown).
    /// </summary>
    public sealed class CapturedClip
    {
        public readonly CaptureSkeleton Skeleton;
        public readonly List<float> Times = new List<float>();
        /// <summary>Per frame, <see cref="Stride"/> floats: x, y, z, w per bone in
        /// <see cref="CaptureSkeleton.Bones"/>' order, then the hips' x, y, z.</summary>
        readonly List<float> data = new List<float>();

        public CapturedClip(CaptureSkeleton skeleton)
        {
            Skeleton = skeleton;
            Stride = 4 * skeleton.Bones.Length + 3;
        }

        /// <summary>Floats per frame.</summary>
        public int Stride { get; }

        public int Count => Times.Count;

        /// <summary>The take's length in seconds (its last sample's time).</summary>
        public double Duration => Times.Count == 0 ? 0 : Times[Times.Count - 1];

        /// <summary>Bone <paramref name="bone"/>'s local rotation in frame <paramref name="frame"/>.</summary>
        public Quat Rotation(int frame, int bone)
        {
            int at = frame * Stride + 4 * bone;
            return new Quat(data[at], data[at + 1], data[at + 2], data[at + 3]);
        }

        /// <summary>The hips' position in frame <paramref name="frame"/>.</summary>
        public Vec3 Hips(int frame)
        {
            int at = frame * Stride + Stride - 3;
            return new Vec3(data[at], data[at + 1], data[at + 2]);
        }

        /// <summary>Add one frame taken at <paramref name="time"/>. Ignored (false) unless the time is
        /// later than the last one's — a glTF animation's times only increase.</summary>
        public bool Add(double time, Quat[] local, Vec3 hips)
        {
            if (!(time >= 0) || (Times.Count > 0 && !(time > Times[Times.Count - 1]))) return false;
            int n = Skeleton.Bones.Length;
            for (int i = 0; i < n; i++)
            {
                var q = local[i];
                data.Add((float)q.X);
                data.Add((float)q.Y);
                data.Add((float)q.Z);
                data.Add((float)q.W);
            }
            data.Add((float)hips.X);
            data.Add((float)hips.Y);
            data.Add((float)hips.Z);
            Times.Add((float)time);
            return true;
        }

        /// <summary>
        /// Turn the whole take about +Y so that its FIRST frame's body (the hips' front) faces +Z, the
        /// clip's front; returns the turn in degrees (0 when the body faces up or down, with no
        /// front to take). The take is measured in its character's frame as it was when the take
        /// began (<see cref="RootPose"/>); its body need not face that root's front (PEAK's scout
        /// faces the wall it climbs, its root the camera). A turn WITHIN the take stays in it.
        /// </summary>
        public double FaceForward()
        {
            if (Count == 0) return 0;
            var front = Rotation(0, 0).Normalized.Rotate(new Vec3(0, 0, 1));
            var flat = new Vec3(front.X, 0, front.Z);
            if (flat.Length < 0.2) return 0;
            var turn = flat.Z / flat.Length < -0.999999 ? Quat.AngleAxis(180, new Vec3(0, 1, 0)) : Quat.FromTo(flat, new Vec3(0, 0, 1));
            for (int f = 0; f < Count; f++)
            {
                int at = f * Stride;
                var q = (turn * Rotation(f, 0)).Normalized;
                data[at] = (float)q.X;
                data[at + 1] = (float)q.Y;
                data[at + 2] = (float)q.Z;
                data[at + 3] = (float)q.W;
                var p = turn.Rotate(Hips(f));
                data[at + Stride - 3] = (float)p.X;
                data[at + Stride - 2] = (float)p.Y;
                data[at + Stride - 1] = (float)p.Z;
            }
            return Quat.Angle(Quat.Identity, turn);
        }
    }
}
