// SPDX-License-Identifier: MIT
using System;
using System.Linq;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Xunit;

/// <summary>The capture's pose math (<see cref="CaptureSkeleton"/>, <see cref="Humanoid"/>): a game
/// rig's pose, whatever its bones' rest rotations, becomes the VRM normalized pose a <c>.vrma</c>
/// carries.</summary>
public class CaptureSkeletonTests
{
    /// <summary>Unity's <c>HumanBodyBones</c> names map onto VRM 1.0's — the thumb shifted by one
    /// (Unity's proximal thumb bone is VRM's metacarpal), eyes and jaw left to her own systems.</summary>
    [Fact]
    public void unity_bone_names_become_vrm_names()
    {
        Assert.Equal("hips", Humanoid.FromUnity("Hips"));
        Assert.Equal("upperChest", Humanoid.FromUnity("UpperChest"));
        Assert.Equal("leftUpperArm", Humanoid.FromUnity("LeftUpperArm"));
        Assert.Equal("rightLittleDistal", Humanoid.FromUnity("RightLittleDistal"));
        Assert.Equal("leftThumbMetacarpal", Humanoid.FromUnity("LeftThumbProximal"));
        Assert.Equal("leftThumbProximal", Humanoid.FromUnity("LeftThumbIntermediate"));
        Assert.Equal("rightThumbDistal", Humanoid.FromUnity("RightThumbDistal"));
        Assert.Null(Humanoid.FromUnity("LeftEye"));
        Assert.Null(Humanoid.FromUnity("Jaw"));
        Assert.Null(Humanoid.FromUnity("LastBone"));
        // Every bone has its canonical parent, and the walk ends at the hips.
        foreach (var b in Humanoid.Bones.Skip(1)) Assert.True(Humanoid.IsBone(Humanoid.Parent(b)), b);
        Assert.Equal("chest", Humanoid.PresentParent("leftShoulder", new[] { "hips", "spine", "chest" }));
    }

    /// <summary>The rig standing in its rest pose is the normalized rest: every local rotation the
    /// identity, the hips at their T-pose height above the floor.</summary>
    [Fact]
    public void the_rest_pose_is_the_identity_whatever_the_bones_rest_rotations()
    {
        var rest = CaptureRig.Rest();
        var sk = CaptureRig.Skeleton(rest);
        Assert.Equal("hips", sk.Bones[0]);
        Assert.Equal(-1, sk.Parents[0]);
        Assert.Equal(sk.IndexOf("chest"), sk.Parents[sk.IndexOf("leftShoulder")]); // no upperChest: the walk skips it
        var local = new Quat[sk.Bones.Length];
        Assert.True(sk.Normalize(CaptureRig.RestRotations(sk, rest), CaptureRig.RestPositions(sk, rest), local, out var hips));
        foreach (var q in local) Assert.True(CaptureRig.Near(q, Quat.Identity), q.ToString());
        Assert.True(CaptureRig.Near(hips, new Vec3(0, 1.0, 0)), hips.ToString()); // the toes are the floor
        Assert.True(CaptureRig.Near(sk.RestPositions[0], new Vec3(0, 1.0, 0)), "the rest hips too");
        Assert.Null(sk.StraightenedBone);
    }

    /// <summary>A known pose comes back out: the rig is posed so that each bone's normalized local
    /// rotation is a different arbitrary one, and the step recovers every one of them.</summary>
    [Fact]
    public void a_known_pose_round_trips_through_the_rigs_own_rest_rotations()
    {
        var rest = CaptureRig.Rest();
        var sk = CaptureRig.Skeleton(rest);
        var want = Enumerable.Range(0, sk.Bones.Length).Select(i => CaptureRig.Arbitrary(100 + i, 80)).ToArray();
        var live = CaptureRig.Pose(sk, rest, want);
        var got = new Quat[sk.Bones.Length];
        Assert.True(sk.Normalize(live, CaptureRig.RestPositions(sk, rest), got, out _));
        for (int i = 0; i < want.Length; i++)
            Assert.True(CaptureRig.Near(got[i], want[i]), $"{sk.Bones[i]}: {got[i]} vs {want[i]}");
    }

    /// <summary>A rig bound in an A-pose is swung to the T-pose first: standing in its rest, its arm
    /// comes out hanging the A-pose's 40° below the T-pose's horizontal — not horizontal, which would
    /// raise her arms by 40° in every captured frame — and its elbow unbent.</summary>
    [Fact]
    public void an_a_pose_rig_is_measured_from_the_t_pose()
    {
        const double down = 40;
        var dir = new Vec3(-Math.Cos(down * Math.PI / 180), -Math.Sin(down * Math.PI / 180), 0);
        var shoulder = new Vec3(-0.15, 1.4, 0);
        var lengths = new System.Collections.Generic.Dictionary<string, double>
        {
            ["leftLowerArm"] = 0.3, ["leftHand"] = 0.55, ["leftMiddleProximal"] = 0.65,
        };
        var rest = CaptureRig.Rest(0, (bone, p) => lengths.TryGetValue(bone, out var l) ? shoulder + dir * l : p);
        var sk = CaptureRig.Skeleton(rest);
        Assert.Equal("leftUpperArm", sk.StraightenedBone);
        Assert.InRange(sk.Straightened, down - 0.01, down + 0.01);

        var local = new Quat[sk.Bones.Length];
        Assert.True(sk.Normalize(CaptureRig.RestRotations(sk, rest), CaptureRig.RestPositions(sk, rest), local, out _));
        int upper = sk.IndexOf("leftUpperArm");
        // Her T-pose arm (−X) turned by the captured rotations points where the game's arm points.
        var world = Quat.Identity;
        for (int i = upper; i >= 0; i = sk.Parents[i]) world = local[i] * world;
        var arm = world.Rotate(new Vec3(-1, 0, 0));
        Assert.True(CaptureRig.Near(arm, dir, 1e-6), arm.ToString());
        Assert.True(CaptureRig.Near(local[sk.IndexOf("leftLowerArm")], Quat.Identity), "the elbow is straight");
        Assert.True(CaptureRig.Near(local[sk.IndexOf("rightUpperArm")], Quat.Identity), "the other arm was a T already");
    }

    /// <summary>A rig that faces −Z in its root's frame is turned to face +Z: a step toward ITS front
    /// is a step along the clip's +Z, and its rest is still the identity.</summary>
    [Fact]
    public void a_rig_facing_backwards_in_its_root_is_turned_to_face_forward()
    {
        var rest = CaptureRig.Rest(180);
        var sk = CaptureRig.Skeleton(rest);
        var local = new Quat[sk.Bones.Length];
        var at = CaptureRig.RestPositions(sk, rest);
        at[0] = at[0] + new Vec3(0, 0, -0.5); // the hips lean out toward ITS front
        Assert.True(sk.Normalize(CaptureRig.RestRotations(sk, rest), at, local, out var moved));
        foreach (var q in local) Assert.True(CaptureRig.Near(q, Quat.Identity), q.ToString());
        Assert.True(CaptureRig.Near(moved, new Vec3(0, 1.0, 0.5)), moved.ToString());
    }

    /// <summary>A hand is never "straightened" toward its fingers' bases: that line is not the hand's
    /// axis (PEAK's scouts bend it 66° off), and swinging the hand along it would bend her wrist in
    /// every frame of the clip.</summary>
    [Fact]
    public void a_hand_is_not_swung_toward_its_fingers()
    {
        var rest = CaptureRig.Rest(0, (bone, p) => bone == "leftMiddleProximal" ? p + new Vec3(0, -0.08, 0.05) : p);
        var sk = CaptureRig.Skeleton(rest);
        Assert.Null(sk.StraightenedBone);
        var local = new Quat[sk.Bones.Length];
        Assert.True(sk.Normalize(CaptureRig.RestRotations(sk, rest), CaptureRig.RestPositions(sk, rest), local, out _));
        Assert.True(CaptureRig.Near(local[sk.IndexOf("leftHand")], Quat.Identity));
    }

    /// <summary>The hips are placed against the FEET, never the root: a live rig that stands
    /// elsewhere against its root than its bind pose did (PEAK's scout: 2.9 units below) still
    /// stands at its rest height; one that crouches has its hips lower by the crouch; one that leaves
    /// the ground with its feet (a jump) does not rise — the clip plays in place.</summary>
    [Fact]
    public void the_hips_stand_on_the_feet_wherever_the_root_is()
    {
        var rest = CaptureRig.Rest();
        var sk = CaptureRig.Skeleton(rest);
        var rot = CaptureRig.RestRotations(sk, rest);
        var local = new Quat[sk.Bones.Length];
        Assert.True(sk.Normalize(rot, CaptureRig.RestPositions(sk, rest, new Vec3(0.3, -2.86, 0.7)), local, out var hips));
        Assert.True(CaptureRig.Near(hips, new Vec3(0, 1.0, 0)), $"elsewhere against the root: {hips}");
        var crouch = CaptureRig.RestPositions(sk, rest);
        crouch[0] = crouch[0] + new Vec3(0, -0.3, 0);
        Assert.True(sk.Normalize(rot, crouch, local, out hips));
        Assert.True(CaptureRig.Near(hips, new Vec3(0, 0.7, 0)), $"crouched: {hips}");
        Assert.True(sk.Normalize(rot, CaptureRig.RestPositions(sk, rest, new Vec3(0, 0.5, 0)), local, out hips));
        Assert.True(CaptureRig.Near(hips, new Vec3(0, 1.0, 0)), $"in the air: {hips}");
    }

    /// <summary>A rig that cannot be a VRM humanoid is refused with the reason: a required bone
    /// missing (named), a name that is not a bone, a rig lying down in its root's frame.</summary>
    [Fact]
    public void a_rig_that_is_not_a_humanoid_is_refused_with_the_reason()
    {
        var rest = CaptureRig.Rest();
        rest.RemoveAll(b => b.Name == "leftFoot");
        Assert.Null(CaptureSkeleton.Build(rest, out string why));
        Assert.Contains("leftFoot", why);

        rest = CaptureRig.Rest();
        rest.Add(new RestBone("tail", Quat.Identity, Vec3.Zero));
        Assert.Null(CaptureSkeleton.Build(rest, out why));
        Assert.Contains("tail", why);

        var lying = Quat.AngleAxis(90, new Vec3(1, 0, 0));
        rest = CaptureRig.Rest().Select(b => new RestBone(b.Name, lying * b.Rotation, lying.Rotate(b.Position))).ToList();
        Assert.Null(CaptureSkeleton.Build(rest, out why));
        Assert.Contains("upright", why);
    }

    /// <summary>Unity's left-handed axes become glTF's by mirroring X: a turn about Y or Z changes
    /// its sense, a turn about X does not, and a position's X flips.</summary>
    [Fact]
    public void unity_axes_become_gltf_axes_by_a_mirror_of_x()
    {
        var q = new Quat(0.1, 0.2, 0.3, 0.9).Normalized;
        var g = q.ToGltf();
        Assert.Equal((q.X, -q.Y, -q.Z, q.W), (g.X, g.Y, g.Z, g.W));
        // The mirror commutes with rotating: mirror(q · v) == mirror(q) · mirror(v).
        var v = new Vec3(0.3, -0.7, 0.2);
        Assert.True(CaptureRig.Near(Quat.ToGltfPosition(q.Rotate(v)), g.Rotate(Quat.ToGltfPosition(v)), 1e-12));
        Assert.Equal(new Vec3(-1, 2, 3), Quat.ToGltfPosition(new Vec3(1, 2, 3)));
    }
}
