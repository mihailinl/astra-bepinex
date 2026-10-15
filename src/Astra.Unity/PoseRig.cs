// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using Astra.Bridge.Capture;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The player's rig, read for a capture (<see cref="Astra.Sdk.GameIntegration.CaptureWhile"/>):
    /// which transform is each VRM humanoid bone, the rig's REST pose, and each frame's pose — plain
    /// numbers for Astra.Bridge.Core's capture (<see cref="BindRest"/>, <see cref="CaptureSkeleton"/>,
    /// <see cref="RootPose"/>), which does the math.
    /// <para>
    /// The bones: a humanoid Animator's own (<c>GetBoneTransform</c>), or — for a Generic rig, which
    /// has no Avatar to ask — the integration's names, looked up under the Animator. The rest: the
    /// BIND pose of the skinned meshes UNDER THE ANIMATOR (a bone's <c>bindposes</c> entry, or, when the
    /// animated bone is not itself skinned, the skinned bone of the same name — a game that drives a
    /// ragdoll copy of its animated rig skins the copy), from the mesh that skins the most of them
    /// (<see cref="BindRest.Choose"/>), brought into the Animator's frame. A humanoid's Avatar T-pose
    /// (<c>humanDescription.skeleton</c>) is not used: the bind pose straightened to a T-pose is.
    /// A rig whose Animator has "Optimize Game Objects" on has no bone transforms to read: it cannot be
    /// captured, and the log says which bones are missing.
    /// </para>
    /// </summary>
    sealed class PoseRig : IPoseSampler
    {
        readonly Transform root;
        readonly Transform[] bones;
        public CaptureSkeleton Skeleton { get; }
        /// <summary>The Animator this rig was read from.</summary>
        public readonly Animator Animator;

        PoseRig(Animator animator, Transform[] bones, CaptureSkeleton skeleton)
        {
            Animator = animator;
            root = animator.transform;
            this.bones = bones;
            Skeleton = skeleton;
        }

#if ASTRA_NO_CAPTURE_RIG
        /// <summary>This build's game stripped what a capture reads (see the IL2CPP project's checks):
        /// the call fails the way a member stripped at run time does, so the capture's Feature goes off.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static PoseRig Find(Animator animator, Dictionary<string, string> names, out string why, out string report, out List<string> warnings) =>
            throw new MissingMethodException("this plugin was built against an interop that lacks SkinnedMeshRenderer.bones, Mesh.bindposes, Animator.isHuman or Animator.GetBoneTransform");
#else
        /// <summary>The rig of <paramref name="animator"/>, or null with <paramref name="why"/> (a
        /// Generic rig without names, a bone not found, no bind pose for one, a rig that is not a VRM
        /// humanoid). <paramref name="names"/>: VRM bone → transform name, or null for a humanoid.
        /// <paramref name="warnings"/>: what is doubtful about the rest (meshes that disagree, a
        /// mirrored or skewed bind pose).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // a member the game stripped fails HERE, inside the capture's Feature
        public static PoseRig Find(Animator animator, Dictionary<string, string> names, out string why, out string report, out List<string> warnings)
        {
            report = null;
            warnings = new List<string>();
            var map = new Dictionary<string, Transform>();
            if (names == null)
            {
                if (!animator.isHuman)
                {
                    why = $"'{animator.name}' is not a humanoid rig (a Generic Animator, no Avatar): the integration has to name its bones (CaptureWhile's bones)";
                    return null;
                }
                for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
                {
                    string vrm = Humanoid.FromUnity(((HumanBodyBones)i).ToString());
                    if (vrm == null) continue;
                    var t = animator.GetBoneTransform((HumanBodyBones)i);
                    if (t != null) map[vrm] = t;
                }
            }
            else
            {
                // One walk of the hierarchy, the first of each name depth-first.
                var byName = new Dictionary<string, Transform>();
                Index(animator.transform, byName);
                var missing = new List<string>();
                foreach (var kv in names)
                {
                    if (byName.TryGetValue(kv.Value, out var t)) map[kv.Key] = t;
                    else missing.Add($"{kv.Key} ('{kv.Value}')");
                }
                if (missing.Count > 0)
                {
                    why = $"no transform under '{animator.name}' for {string.Join(", ", missing)} (an Animator with \"Optimize Game Objects\" has none)";
                    return null;
                }
            }

            // Every skinned mesh UNDER THE ANIMATOR, with the bind pose of each mapped bone it skins.
            var mapped = new HashSet<int>();
            foreach (var t in map.Values) mapped.Add(t.GetInstanceID());
            var toRoot = animator.transform.worldToLocalMatrix;
            var meshes = new List<BindMesh>();
            foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var skinned = smr.bones;
                var bind = mesh.bindposes;
                if (skinned == null || bind == null) continue;
                int n = Math.Min(skinned.Length, bind.Length);
                var byId = new Dictionary<int, int>();
                var byBoneName = new Dictionary<string, int>();
                for (int i = 0; i < n; i++)
                {
                    var b = skinned[i];
                    if (b == null) continue;
                    byId[b.GetInstanceID()] = i;
                    if (!byBoneName.ContainsKey(b.name)) byBoneName[b.name] = i;
                }
                var bm = new BindMesh { Name = smr.name, UnderBone = HangsBelow(smr.transform, animator.transform, mapped) };
                // The mesh's frame, from where it is now: a mesh that does not move with a bone does
                // not move against the root (one that does is used last: BindRest).
                var meshToRoot = toRoot * smr.transform.localToWorldMatrix;
                foreach (var kv in map)
                {
                    if (!byId.TryGetValue(kv.Value.GetInstanceID(), out int i) && !byBoneName.TryGetValue(kv.Value.name, out i)) continue;
                    var m = meshToRoot * bind[i].inverse;
                    bm.Bones[kv.Key] = new double[]
                    {
                        m.m00, m.m10, m.m20, m.m30,
                        m.m01, m.m11, m.m21, m.m31,
                        m.m02, m.m12, m.m22, m.m32,
                        m.m03, m.m13, m.m23, m.m33,
                    };
                }
                if (bm.Bones.Count > 0) meshes.Add(bm);
            }
            var rest = BindRest.Choose(meshes, map.Keys, out warnings, out var noBind, out string source);
            var skeleton = CaptureSkeleton.Build(rest, out why);
            if (skeleton == null)
            {
                if (noBind.Count > 0) why += $" (no bind pose under '{animator.name}' for {string.Join(", ", noBind)})";
                return null;
            }
            var ordered = new Transform[skeleton.Bones.Length];
            for (int i = 0; i < ordered.Length; i++) ordered[i] = map[skeleton.Bones[i]];
            report = $"{skeleton.Bones.Length} bones of '{animator.name}' ({(names == null ? "humanoid" : "named by the integration")}), rest from '{source}' ({meshes.Count} skinned meshes under it)"
                + $", rest hips {skeleton.RestPositions[0].Y:0.###} above the feet"
                + (skeleton.StraightenedBone != null ? $", straightened to a T-pose (most: {skeleton.StraightenedBone} by {skeleton.Straightened:0}°)" : "")
                + (noBind.Count > 0 ? $"; left out (no bind pose): {string.Join(", ", noBind)}" : "");
            why = null;
            return new PoseRig(animator, ordered, skeleton);
        }
#endif

        /// <summary>Does <paramref name="t"/> hang below one of the <paramref name="mapped"/> bones
        /// (before reaching <paramref name="top"/>)?</summary>
        static bool HangsBelow(Transform t, Transform top, HashSet<int> mapped)
        {
            for (var p = t.parent; p != null && p != top; p = p.parent)
                if (mapped.Contains(p.GetInstanceID())) return true;
            return false;
        }

        /// <summary>Every transform under <paramref name="t"/> (itself included) by name, the first of
        /// each name depth-first.</summary>
        static void Index(Transform t, Dictionary<string, Transform> byName)
        {
            if (!byName.ContainsKey(t.name)) byName[t.name] = t;
            for (int i = 0; i < t.childCount; i++) Index(t.GetChild(i), byName);
        }

        /// <summary>Is this rig still the one <paramref name="animator"/> drives, all of it alive?</summary>
        public bool Alive(Animator animator)
        {
            if (animator == null || animator != Animator || root == null) return false;
            foreach (var b in bones)
                if (b == null) return false;
            return true;
        }

        public bool Sample(Quat[] world, Vec3[] positions, out RootPose rootPose)
        {
            rootPose = default;
            if (root == null) return false;
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) return false;
                world[i] = Q(b.rotation);
                positions[i] = V(b.position);
            }
            rootPose = new RootPose(Q(root.rotation), V(root.position), V(root.lossyScale));
            return true;
        }

        /// <summary>Every bone's world rotation now, into <paramref name="into"/> (made the right size) —
        /// the draw-order check's reading.</summary>
        public void Rotations(ref Quaternion[] into)
        {
            if (into == null || into.Length != bones.Length) into = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) into[i] = bones[i] != null ? bones[i].rotation : Quaternion.identity;
        }

        /// <summary>Where the hips stand NOW against the rest, in the clip's frame — a test aid's
        /// diagnostic line (<c>ASTRA_CAPTURE_TEST</c>).</summary>
        public string DescribeNow()
        {
            int n = bones.Length;
            var world = new Quat[n];
            var at = new Vec3[n];
            if (!Sample(world, at, out var rootPose)) return "no pose now";
            var live = new Quat[n];
            var pos = new Vec3[n];
            RootPose.ToTakeFrame(rootPose.Rotation, rootPose, world, at, n, live, pos);
            var local = new Quat[n];
            if (!Skeleton.Normalize(live, pos, local, out var clipHips)) return "no finite pose now";
            return $"in the clip now: hips ({clipHips.X:0.##}, {clipHips.Y:0.##}, {clipHips.Z:0.##}), rest ({Skeleton.RestPositions[0].X:0.##}, {Skeleton.RestPositions[0].Y:0.##}, {Skeleton.RestPositions[0].Z:0.##})";
        }

        static Quat Q(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);
        static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);
    }
}
