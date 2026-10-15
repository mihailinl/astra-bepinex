// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using Astra.Bridge.Capture;

/// <summary>
/// A synthetic game rig for the capture tests, in Unity's axes (facing +Z, the character's left at
/// −X): a humanoid T-pose whose every bone carries an arbitrary REST rotation, the way a game's rig
/// does (Blender's bones point along +Y, an FBX import adds its own pre-rotations…), so a test that
/// passes cannot be passing because the rest happened to be the identity.
/// </summary>
static class CaptureRig
{
    public static readonly (string Bone, Vec3 Pos)[] TPose =
    {
        ("hips", new Vec3(0, 1.0, 0)),
        ("spine", new Vec3(0, 1.1, 0)),
        ("chest", new Vec3(0, 1.25, 0)),
        ("neck", new Vec3(0, 1.45, 0)),
        ("head", new Vec3(0, 1.55, 0)),
        ("leftUpperLeg", new Vec3(-0.1, 0.95, 0)),
        ("leftLowerLeg", new Vec3(-0.1, 0.5, 0)),
        ("leftFoot", new Vec3(-0.1, 0.08, 0)),
        ("leftToes", new Vec3(-0.1, 0.0, 0.12)),
        ("rightUpperLeg", new Vec3(0.1, 0.95, 0)),
        ("rightLowerLeg", new Vec3(0.1, 0.5, 0)),
        ("rightFoot", new Vec3(0.1, 0.08, 0)),
        ("rightToes", new Vec3(0.1, 0.0, 0.12)),
        ("leftShoulder", new Vec3(-0.05, 1.4, 0)),
        ("leftUpperArm", new Vec3(-0.15, 1.4, 0)),
        ("leftLowerArm", new Vec3(-0.45, 1.4, 0)),
        ("leftHand", new Vec3(-0.7, 1.4, 0)),
        ("leftMiddleProximal", new Vec3(-0.8, 1.4, 0)),
        ("rightShoulder", new Vec3(0.05, 1.4, 0)),
        ("rightUpperArm", new Vec3(0.15, 1.4, 0)),
        ("rightLowerArm", new Vec3(0.45, 1.4, 0)),
        ("rightHand", new Vec3(0.7, 1.4, 0)),
        ("rightMiddleProximal", new Vec3(0.8, 1.4, 0)),
    };

    /// <summary>A rotation that differs for every <paramref name="seed"/>: an angle up to ~170°
    /// about an axis that wanders.</summary>
    public static Quat Arbitrary(int seed, double maxDegrees = 170)
    {
        double a = seed * 1.7 + 0.3;
        var axis = new Vec3(Math.Sin(a), Math.Cos(a * 1.3), Math.Sin(a * 0.7 + 1));
        return Quat.AngleAxis(maxDegrees * (0.2 + 0.8 * Math.Abs(Math.Sin(seed * 2.3 + 0.5))), axis);
    }

    /// <summary>The T-pose rig, each bone with its own arbitrary rest rotation; optionally turned
    /// about +Y by <paramref name="yaw"/> degrees (a rig that faces elsewhere in its root's frame).</summary>
    public static List<RestBone> Rest(double yaw = 0, Func<string, Vec3, Vec3> move = null)
    {
        var turn = Quat.AngleAxis(yaw, new Vec3(0, 1, 0));
        var rest = new List<RestBone>();
        int seed = 0;
        foreach (var (bone, pos) in TPose)
        {
            var p = move != null ? move(bone, pos) : pos;
            rest.Add(new RestBone(bone, turn * Arbitrary(seed++), turn.Rotate(p)));
        }
        return rest;
    }

    public static CaptureSkeleton Skeleton(List<RestBone> rest = null)
    {
        var sk = CaptureSkeleton.Build(rest ?? Rest(), out string why);
        if (sk == null) throw new InvalidOperationException(why);
        return sk;
    }

    /// <summary>The LIVE rotations (relative to the root) that put <paramref name="sk"/>'s rig into
    /// the normalized pose <paramref name="local"/>: N(bone) = N(parent) · local, and live = N · rest.</summary>
    public static Quat[] Pose(CaptureSkeleton sk, List<RestBone> rest, Quat[] local)
    {
        int n = sk.Bones.Length;
        var world = new Quat[n];
        var live = new Quat[n];
        for (int i = 0; i < n; i++)
        {
            world[i] = sk.Parents[i] < 0 ? local[i] : world[sk.Parents[i]] * local[i];
            var r = rest.Find(b => b.Name == sk.Bones[i]).Rotation;
            live[i] = (sk.Facing.Inverse * world[i] * sk.Facing * r).Normalized;
        }
        return live;
    }

    public static Quat[] RestRotations(CaptureSkeleton sk, List<RestBone> rest)
    {
        var live = new Quat[sk.Bones.Length];
        for (int i = 0; i < live.Length; i++) live[i] = rest.Find(b => b.Name == sk.Bones[i]).Rotation;
        return live;
    }

    public static Vec3 RestHips(List<RestBone> rest) => rest.Find(b => b.Name == "hips").Position;

    /// <summary>The rest POSITIONS, in <paramref name="sk"/>'s order, moved by <paramref name="offset"/>
    /// (where the live rig stands against its root need not be where its bind pose was).</summary>
    public static Vec3[] RestPositions(CaptureSkeleton sk, List<RestBone> rest, Vec3 offset = default)
    {
        var p = new Vec3[sk.Bones.Length];
        for (int i = 0; i < p.Length; i++) p[i] = rest.Find(b => b.Name == sk.Bones[i]).Position + offset;
        return p;
    }

    /// <summary>A rig that holds still in its rest pose, or plays <see cref="Local"/> when set,
    /// under a root at <see cref="Root"/> (default: the world's origin). The body follows its root
    /// unless <see cref="BodyStill"/>: then it stays put in the WORLD while the root turns (PEAK's
    /// root turning with the camera while the scout holds the wall).</summary>
    public sealed class Sampler : IPoseSampler
    {
        readonly List<RestBone> rest;
        public Sampler(List<RestBone> rest = null)
        {
            this.rest = rest ?? Rest();
            Skeleton = CaptureRig.Skeleton(this.rest);
        }

        public CaptureSkeleton Skeleton { get; }
        public int Samples;
        public bool Gone;
        public Func<int, Quat[]> Local;
        public Func<int, RootPose> Root;
        public bool BodyStill;

        public static readonly RootPose Origin = new RootPose(Quat.Identity, Vec3.Zero, new Vec3(1, 1, 1));

        public bool Sample(Quat[] world, Vec3[] positions, out RootPose root)
        {
            root = Root != null ? Root(Samples) : Origin;
            if (Gone) return false;
            var pose = Local != null ? Pose(Skeleton, rest, Local(Samples)) : RestRotations(Skeleton, rest);
            var at = RestPositions(Skeleton, rest);
            var frame = BodyStill ? Origin : root;
            for (int i = 0; i < pose.Length; i++)
            {
                world[i] = frame.Rotation * pose[i];
                var p = at[i];
                positions[i] = frame.Position + frame.Rotation.Rotate(new Vec3(p.X * frame.Scale.X, p.Y * frame.Scale.Y, p.Z * frame.Scale.Z));
            }
            Samples++;
            return true;
        }
    }

    public static bool Near(Quat a, Quat b, double degrees = 0.01) => Quat.Angle(a, b) < degrees;
    public static bool Near(Vec3 a, Vec3 b, double eps = 1e-6) => (a - b).Length < eps;
}
