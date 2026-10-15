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
    /// numbers for <see cref="CaptureSkeleton"/> (Astra.Bridge.Core), which does the math.
    /// <para>
    /// The bones: a humanoid Animator's own (<c>GetBoneTransform</c>), or — for a Generic rig, which
    /// has no Avatar to ask — the integration's names, looked up under the Animator. The rest: the
    /// BIND pose of the character's skinned meshes (a bone's <c>bindposes</c> entry, or, when the
    /// animated bone is not itself skinned, the skinned bone of the same name — a game that drives a
    /// ragdoll copy of its animated rig skins the copy), brought into the Animator's frame. Every
    /// rotation is relative to the Animator's transform, and the hips are placed against the feet
    /// (<see cref="CaptureSkeleton.Normalize"/>), so the character's own travel through the world is
    /// not in the clip: it plays in place.
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

        /// <summary>The rig of <paramref name="animator"/>, or null with <paramref name="why"/> (a
        /// Generic rig without names, a bone not found, no bind pose for one, a rig that is not a VRM
        /// humanoid). <paramref name="names"/>: VRM bone → transform name, or null for a humanoid.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // a member the game stripped fails HERE, inside the capture's Feature
        public static PoseRig Find(Animator animator, Dictionary<string, string> names, out string why, out string report)
        {
            report = null;
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
                var missing = new List<string>();
                foreach (var kv in names)
                {
                    var t = FindUnder(animator.transform, kv.Value);
                    if (t != null) map[kv.Key] = t;
                    else missing.Add($"{kv.Key} ('{kv.Value}')");
                }
                if (missing.Count > 0)
                {
                    why = $"no transform under '{animator.name}' for {string.Join(", ", missing)}";
                    return null;
                }
            }

            // Every skinned bone of the character, by transform and by name, with its bind pose.
            // Keyed by instance id, never by the Transform object: an IL2CPP game's wrapper objects are
            // not the same managed object for the same native one.
            var byBone = new Dictionary<int, Matrix4x4>();
            var byName = new Dictionary<string, Matrix4x4>();
            var meshOf = new Dictionary<string, string>(); // a bone's name → the mesh its bind pose came from
            var top = animator.transform.root;
            int meshes = 0;
            foreach (var smr in top.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                var skinned = smr.bones;
                var bind = mesh.bindposes;
                if (skinned == null || bind == null) continue;
                meshes++;
                // The mesh's frame, from where it is now: a renderer does not move against its root.
                var meshToWorld = smr.transform.localToWorldMatrix;
                int n = Math.Min(skinned.Length, bind.Length);
                for (int i = 0; i < n; i++)
                {
                    var b = skinned[i];
                    if (b == null) continue;
                    var world = meshToWorld * bind[i].inverse;
                    int id = b.GetInstanceID();
                    if (!byBone.ContainsKey(id)) byBone[id] = world;
                    if (!byName.ContainsKey(b.name))
                    {
                        byName[b.name] = world;
                        meshOf[b.name] = smr.name;
                    }
                }
            }

            var toRoot = animator.transform.worldToLocalMatrix;
            var rest = new List<RestBone>();
            var order = new List<Transform>();
            var noBind = new List<string>();
            int itself = 0;
            var named = new HashSet<string>();
            foreach (var kv in map)
            {
                if (byBone.TryGetValue(kv.Value.GetInstanceID(), out var world)) itself++;
                else if (byName.TryGetValue(kv.Value.name, out world)) named.Add(meshOf[kv.Value.name]);
                else
                {
                    noBind.Add($"{kv.Key} ('{kv.Value.name}')");
                    continue;
                }
                var m = toRoot * world;
                var rot = Quaternion.LookRotation(m.GetColumn(2), m.GetColumn(1));
                var pos = (Vector3)m.GetColumn(3);
                rest.Add(new RestBone(kv.Key, Q(rot), V(pos)));
                order.Add(kv.Value);
            }
            var skeleton = CaptureSkeleton.Build(rest, out why);
            if (skeleton == null)
            {
                if (noBind.Count > 0) why += $" (no bind pose in its {meshes} skinned meshes for {string.Join(", ", noBind)})";
                return null;
            }
            var ordered = new Transform[skeleton.Bones.Length];
            for (int i = 0; i < ordered.Length; i++) ordered[i] = order[rest.FindIndex(r => r.Name == skeleton.Bones[i])];
            report = $"{skeleton.Bones.Length} bones of '{animator.name}' ({(names == null ? "humanoid" : "named by the integration")}), rest from {meshes} skinned meshes"
                + $" ({itself} skinned themselves" + (named.Count > 0 ? $", {skeleton.Bones.Length - itself} through a skinned bone of their name in {string.Join(", ", named)})" : ")")
                + $", rest hips {skeleton.RestPositions[0].Y:0.###} above the feet"
                + (skeleton.StraightenedBone != null ? $", straightened to a T-pose (most: {skeleton.StraightenedBone} by {skeleton.Straightened:0}°)" : "")
                + (noBind.Count > 0 ? $"; left out (no bind pose): {string.Join(", ", noBind)}" : "");
            why = null;
            return new PoseRig(animator, ordered, skeleton);
        }

        /// <summary>Is this rig still the one <paramref name="animator"/> drives, all of it alive?</summary>
        public bool Alive(Animator animator)
        {
            if (animator == null || animator != Animator || root == null) return false;
            foreach (var b in bones)
                if (b == null) return false;
            return true;
        }

        public bool Sample(Quat[] live, Vec3[] positions)
        {
            if (root == null) return false;
            var inv = Quaternion.Inverse(root.rotation);
            for (int i = 0; i < bones.Length; i++)
            {
                var b = bones[i];
                if (b == null) return false;
                live[i] = Q(inv * b.rotation);
                positions[i] = V(root.InverseTransformPoint(b.position));
            }
            return true;
        }

        /// <summary>Where the hips and feet are NOW against the rest, in the clip's frame — a test aid's
        /// diagnostic line (<c>ASTRA_CAPTURE_TEST</c>).</summary>
        public string DescribeNow()
        {
            var live = new Quat[bones.Length];
            var positions = new Vec3[bones.Length];
            if (!Sample(live, positions)) return "no pose now";
            var local = new Quat[bones.Length];
            if (!Skeleton.Normalize(live, positions, local, out var clipHips)) return "no finite pose now";
            string Foot(string bone)
            {
                int i = Skeleton.IndexOf(bone);
                if (i < 0) return "-";
                var p = root.InverseTransformPoint(bones[i].position);
                return $"({p.x:0.##}, {p.y:0.##}, {p.z:0.##})";
            }
            var h = root.InverseTransformPoint(bones[0].position);
            return $"hips now ({h.x:0.##}, {h.y:0.##}, {h.z:0.##}) in '{root.name}', feet {Foot("leftFoot")} {Foot("rightFoot")}; "
                + $"in the clip: hips ({clipHips.X:0.##}, {clipHips.Y:0.##}, {clipHips.Z:0.##}), rest ({Skeleton.RestPositions[0].X:0.##}, {Skeleton.RestPositions[0].Y:0.##}, {Skeleton.RestPositions[0].Z:0.##})";
        }

        /// <summary>The first transform named <paramref name="name"/> under <paramref name="t"/>
        /// (itself included), depth first.</summary>
        static Transform FindUnder(Transform t, string name)
        {
            if (t.name == name) return t;
            for (int i = 0; i < t.childCount; i++)
            {
                var found = FindUnder(t.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        static Quat Q(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);
        static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);
    }
}
