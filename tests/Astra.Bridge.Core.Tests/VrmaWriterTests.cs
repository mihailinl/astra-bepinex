// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Xunit;

/// <summary>The <c>.vrma</c> a capture writes (<see cref="VrmaWriter"/>), read back with an
/// independent GLB reader: the container, the VRM humanoid map, the normalized skeleton and every
/// key of a known pose.</summary>
public class VrmaWriterTests
{
    /// <summary>A GLB's JSON (parsed) and binary chunk; asserts the container rules on the way.</summary>
    public static (JsonElement Json, byte[] Bin) ReadGlb(byte[] glb)
    {
        Assert.True(glb.Length >= 28);
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0)); // "glTF"
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        int jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        Assert.Equal(0x4E4F534Au, BitConverter.ToUInt32(glb, 16)); // "JSON"
        Assert.Equal(0, jsonLength % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength)).RootElement;
        int at = 20 + jsonLength;
        int binLength = (int)BitConverter.ToUInt32(glb, at);
        Assert.Equal(0x004E4942u, BitConverter.ToUInt32(glb, at + 4)); // "BIN\0"
        Assert.Equal(0, binLength % 4);
        Assert.Equal(glb.Length, at + 8 + binLength);
        return (json, glb.Skip(at + 8).Take(binLength).ToArray());
    }

    static float[] Floats(JsonElement json, byte[] bin, int accessor)
    {
        var a = json.GetProperty("accessors")[accessor];
        Assert.Equal(5126, a.GetProperty("componentType").GetInt32());
        int count = a.GetProperty("count").GetInt32();
        int width = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC3" => 3, "VEC4" => 4, var t => throw new Exception(t) };
        var view = json.GetProperty("bufferViews")[a.GetProperty("bufferView").GetInt32()];
        int offset = view.GetProperty("byteOffset").GetInt32();
        Assert.Equal(count * width * 4, view.GetProperty("byteLength").GetInt32());
        var r = new float[count * width];
        Buffer.BlockCopy(bin, offset, r, 0, r.Length * 4);
        return r;
    }

    static CapturedClip KnownClip(out Quat[][] poses, out Vec3[] hips)
    {
        var sk = CaptureRig.Skeleton();
        var clip = new CapturedClip(sk);
        poses = new Quat[3][];
        hips = new Vec3[3];
        for (int f = 0; f < 3; f++)
        {
            poses[f] = Enumerable.Range(0, sk.Bones.Length).Select(i => CaptureRig.Arbitrary(10 * f + i, 60)).ToArray();
            // Frame 1's keys are given with the OTHER sign (the same rotations): the file keeps them in
            // one hemisphere with their neighbours.
            if (f == 1) poses[f] = poses[f].Select(q => new Quat(-q.X, -q.Y, -q.Z, -q.W)).ToArray();
            hips[f] = new Vec3(0.1 * f, 1.0 + 0.05 * f, -0.2 * f);
            Assert.True(clip.Add(f / 30.0, poses[f], hips[f]));
        }
        return clip;
    }

    /// <summary>A known pose survives the file: the container is a valid GLB, the extension names
    /// every bone's node, the skeleton is the normalized one (T-pose offsets, NO rotations), and each
    /// key read back is the pose's rotation in glTF's axes.</summary>
    [Fact]
    public void a_known_pose_round_trips_through_the_file()
    {
        var clip = KnownClip(out var poses, out var hips);
        var sk = clip.Skeleton;
        var (json, bin) = ReadGlb(VrmaWriter.Write(clip, "climb"));

        Assert.Equal("2.0", json.GetProperty("asset").GetProperty("version").GetString());
        Assert.Contains("VRMC_vrm_animation", json.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()));
        var ext = json.GetProperty("extensions").GetProperty("VRMC_vrm_animation");
        Assert.Equal("1.0", ext.GetProperty("specVersion").GetString());
        var bones = ext.GetProperty("humanoid").GetProperty("humanBones");
        var nodes = json.GetProperty("nodes");
        Assert.Equal(sk.Bones.Length, nodes.GetArrayLength());
        for (int i = 0; i < sk.Bones.Length; i++)
        {
            int node = bones.GetProperty(sk.Bones[i]).GetProperty("node").GetInt32();
            Assert.Equal(sk.Bones[i], nodes[node].GetProperty("name").GetString());
            Assert.False(nodes[node].TryGetProperty("rotation", out _), "a normalized skeleton has no rest rotations");
            var t = nodes[node].GetProperty("translation").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var local = sk.Parents[i] < 0 ? sk.RestPositions[i] : sk.RestPositions[i] - sk.RestPositions[sk.Parents[i]];
            Assert.True(CaptureRig.Near(new Vec3(t[0], t[1], t[2]), Quat.ToGltfPosition(local), 1e-9), sk.Bones[i]);
        }
        // The hips are the scene's one root and stand at their T-pose height.
        Assert.Equal(0, json.GetProperty("scenes")[0].GetProperty("nodes")[0].GetInt32());
        Assert.Equal(1.0, nodes[0].GetProperty("translation")[1].GetDouble(), 9);

        var anim = json.GetProperty("animations")[0];
        Assert.Equal("climb", anim.GetProperty("name").GetString());
        var channels = anim.GetProperty("channels");
        var samplers = anim.GetProperty("samplers");
        Assert.Equal(sk.Bones.Length + 1, channels.GetArrayLength());

        var times = Floats(json, bin, 0);
        Assert.Equal(new[] { 0f, (float)(1 / 30.0), (float)(2 / 30.0) }, times);
        var input = json.GetProperty("accessors")[0];
        Assert.Equal(times[0], input.GetProperty("min")[0].GetSingle());
        Assert.Equal(times[2], input.GetProperty("max")[0].GetSingle());

        foreach (var ch in channels.EnumerateArray())
        {
            var target = ch.GetProperty("target");
            int node = target.GetProperty("node").GetInt32();
            var sampler = samplers[ch.GetProperty("sampler").GetInt32()];
            Assert.Equal(0, sampler.GetProperty("input").GetInt32());
            Assert.Equal("LINEAR", sampler.GetProperty("interpolation").GetString());
            var values = Floats(json, bin, sampler.GetProperty("output").GetInt32());
            if (target.GetProperty("path").GetString() == "translation")
            {
                Assert.Equal(0, node);
                for (int f = 0; f < 3; f++)
                    Assert.True(CaptureRig.Near(new Vec3(values[3 * f], values[3 * f + 1], values[3 * f + 2]), Quat.ToGltfPosition(hips[f]), 1e-6));
                continue;
            }
            Assert.Equal("rotation", target.GetProperty("path").GetString());
            Quat prev = default;
            for (int f = 0; f < 3; f++)
            {
                var q = new Quat(values[4 * f], values[4 * f + 1], values[4 * f + 2], values[4 * f + 3]);
                Assert.True(CaptureRig.Near(q, poses[f][node].ToGltf(), 0.01), $"{sk.Bones[node]} frame {f}");
                if (f > 0) Assert.True(q.Dot(prev) >= 0, $"{sk.Bones[node]}: frame {f} flips hemisphere");
                prev = q;
            }
        }
    }

    /// <summary>The longest take a recorder keeps — a full rig, <see cref="CaptureRecorder.MaxSeconds"/>
    /// at <see cref="CaptureRecorder.Rate"/> — is far under the engine's size cap.</summary>
    [Fact]
    public void the_longest_take_is_far_under_the_engines_cap()
    {
        var full = CaptureRig.Rest();
        full.Add(new RestBone("upperChest", Quat.Identity, new Vec3(0, 1.35, 0)));
        // Every finger too: placed along the hand, so the rig is the biggest a capture carries.
        foreach (var b in Humanoid.Bones)
            if (!full.Exists(r => r.Name == b))
            {
                bool left = b.StartsWith("left");
                var hand = full.Find(r => r.Name == (left ? "leftHand" : "rightHand")).Position;
                full.Add(new RestBone(b, Quat.Identity, hand + new Vec3(left ? -0.05 : 0.05, 0, 0.01 * (full.Count % 7))));
            }
        var sk = CaptureRig.Skeleton(full);
        Assert.Equal(Humanoid.Bones.Length, sk.Bones.Length);
        var clip = new CapturedClip(sk);
        var pose = Enumerable.Repeat(Quat.Identity, sk.Bones.Length).ToArray();
        int frames = (int)(CaptureRecorder.MaxSeconds * CaptureRecorder.Rate);
        for (int f = 0; f <= frames; f++) clip.Add(f / CaptureRecorder.Rate, pose, Vec3.Zero);
        var bytes = VrmaWriter.Write(clip, "climb");
        Assert.InRange(bytes.Length, 1, CaptureRecorder.MaxBytes / 8);
    }

    /// <summary>A take is turned so its FIRST frame's body faces the clip's front (+Z): a body that
    /// faced 60° off its root (PEAK's scout faces the wall, its root the camera) climbs facing front,
    /// a turn within the take stays (60° → 90° is still 30° of turning), the hips' place turns with
    /// it, and the limbs — relative to the hips — do not change.</summary>
    [Fact]
    public void a_take_is_turned_to_face_front_at_its_start()
    {
        var sk = CaptureRig.Skeleton();
        var clip = new CapturedClip(sk);
        var up = new Vec3(0, 1, 0);
        var lean = Quat.AngleAxis(20, new Vec3(1, 0, 0)); // leaning into the wall: kept
        int arm = sk.IndexOf("leftUpperArm");
        for (int f = 0; f < 2; f++)
        {
            var pose = Enumerable.Repeat(Quat.Identity, sk.Bones.Length).ToArray();
            pose[0] = Quat.AngleAxis(60 + 30 * f, up) * lean;
            pose[arm] = CaptureRig.Arbitrary(7, 50);
            Assert.True(clip.Add(f / 30.0, pose, new Vec3(0.2, 1.0, 0)));
        }
        Assert.InRange(clip.FaceForward(), 59.99, 60.01);
        Quat Hips(int f) => clip.Rotation(f, 0);
        Assert.True(CaptureRig.Near(Hips(0), lean, 0.01), Hips(0).ToString());
        Assert.True(CaptureRig.Near(Hips(1), Quat.AngleAxis(30, up) * lean, 0.01), Hips(1).ToString());
        Assert.True(CaptureRig.Near(clip.Rotation(0, arm), CaptureRig.Arbitrary(7, 50), 0.01));
        var hipsAt = clip.Hips(0);
        Assert.True(CaptureRig.Near(hipsAt, Quat.AngleAxis(-60, up).Rotate(new Vec3(0.2, 1.0, 0)), 1e-5), hipsAt.ToString());
    }

    /// <summary>A time that does not increase is never added (a glTF animation's times only
    /// increase), and a clip of one frame is not a clip.</summary>
    [Fact]
    public void times_only_increase_and_one_frame_is_not_a_clip()
    {
        var sk = CaptureRig.Skeleton();
        var clip = new CapturedClip(sk);
        var pose = Enumerable.Repeat(Quat.Identity, sk.Bones.Length).ToArray();
        Assert.True(clip.Add(0, pose, Vec3.Zero));
        Assert.Throws<ArgumentException>(() => VrmaWriter.Write(clip, "x"));
        Assert.False(clip.Add(0, pose, Vec3.Zero));
        Assert.False(clip.Add(double.NaN, pose, Vec3.Zero));
        Assert.True(clip.Add(0.1, pose, Vec3.Zero));
        Assert.Equal(2, clip.Count);
    }
}
