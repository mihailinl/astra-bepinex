// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// The VRM 1.0 humanoid a captured clip is written in (<c>VRMC_vrm_animation</c>'s
    /// <c>humanBones</c>): the bone names, the canonical parent of each (three-vrm's
    /// <c>VRMHumanBoneParentMap</c>, the one the engine's <c>anim/vrma.rs</c> walks), the ones a
    /// capture cannot do without, and Unity's names for them (<c>HumanBodyBones</c>). Eyes and jaw
    /// are left out on purpose: her gaze and her mouth are her own (look-at, lip-sync), never a game's.
    /// </summary>
    public static class Humanoid
    {
        /// <summary>Every bone a capture may carry, parents before children.</summary>
        public static readonly string[] Bones =
        {
            "hips", "spine", "chest", "upperChest", "neck", "head",
            "leftUpperLeg", "leftLowerLeg", "leftFoot", "leftToes",
            "rightUpperLeg", "rightLowerLeg", "rightFoot", "rightToes",
            "leftShoulder", "leftUpperArm", "leftLowerArm", "leftHand",
            "rightShoulder", "rightUpperArm", "rightLowerArm", "rightHand",
            "leftThumbMetacarpal", "leftThumbProximal", "leftThumbDistal",
            "leftIndexProximal", "leftIndexIntermediate", "leftIndexDistal",
            "leftMiddleProximal", "leftMiddleIntermediate", "leftMiddleDistal",
            "leftRingProximal", "leftRingIntermediate", "leftRingDistal",
            "leftLittleProximal", "leftLittleIntermediate", "leftLittleDistal",
            "rightThumbMetacarpal", "rightThumbProximal", "rightThumbDistal",
            "rightIndexProximal", "rightIndexIntermediate", "rightIndexDistal",
            "rightMiddleProximal", "rightMiddleIntermediate", "rightMiddleDistal",
            "rightRingProximal", "rightRingIntermediate", "rightRingDistal",
            "rightLittleProximal", "rightLittleIntermediate", "rightLittleDistal",
        };

        /// <summary>The bones a capture cannot do without — VRM 1.0's required ones: a body whose
        /// trunk, head, arms and legs are all there.</summary>
        public static readonly string[] Required =
        {
            "hips", "spine", "head",
            "leftUpperLeg", "leftLowerLeg", "leftFoot", "rightUpperLeg", "rightLowerLeg", "rightFoot",
            "leftUpperArm", "leftLowerArm", "leftHand", "rightUpperArm", "rightLowerArm", "rightHand",
        };

        static readonly Dictionary<string, string> parents = BuildParents();
        static readonly HashSet<string> known = new HashSet<string>(Bones);

        static Dictionary<string, string> BuildParents()
        {
            var p = new Dictionary<string, string>
            {
                ["spine"] = "hips", ["chest"] = "spine", ["upperChest"] = "chest", ["neck"] = "upperChest", ["head"] = "neck",
            };
            foreach (var side in new[] { "left", "right" })
            {
                p[side + "UpperLeg"] = "hips";
                p[side + "LowerLeg"] = side + "UpperLeg";
                p[side + "Foot"] = side + "LowerLeg";
                p[side + "Toes"] = side + "Foot";
                p[side + "Shoulder"] = "upperChest";
                p[side + "UpperArm"] = side + "Shoulder";
                p[side + "LowerArm"] = side + "UpperArm";
                p[side + "Hand"] = side + "LowerArm";
                p[side + "ThumbMetacarpal"] = side + "Hand";
                p[side + "ThumbProximal"] = side + "ThumbMetacarpal";
                p[side + "ThumbDistal"] = side + "ThumbProximal";
                foreach (var finger in new[] { "Index", "Middle", "Ring", "Little" })
                {
                    p[side + finger + "Proximal"] = side + "Hand";
                    p[side + finger + "Intermediate"] = side + finger + "Proximal";
                    p[side + finger + "Distal"] = side + finger + "Intermediate";
                }
            }
            return p;
        }

        /// <summary>Is <paramref name="bone"/> one of <see cref="Bones"/>?</summary>
        public static bool IsBone(string bone) => bone != null && known.Contains(bone);

        /// <summary>The canonical parent of <paramref name="bone"/>; null for <c>hips</c> (and for a
        /// name that is not a bone).</summary>
        public static string Parent(string bone) => bone != null && parents.TryGetValue(bone, out var p) ? p : null;

        /// <summary>
        /// The VRM name of a Unity <c>HumanBodyBones</c> value, by its enum NAME (<c>"LeftUpperArm"</c>
        /// → <c>"leftUpperArm"</c>); null for one a capture does not carry (eyes, jaw,
        /// <c>LastBone</c>). Unity's three thumb bones are VRM 1.0's metacarpal, proximal and distal
        /// (Unity's <c>LeftThumbProximal</c> is the one at the hand).
        /// </summary>
        public static string FromUnity(string humanBodyBone)
        {
            if (string.IsNullOrEmpty(humanBodyBone)) return null;
            string name = char.ToLowerInvariant(humanBodyBone[0]) + humanBodyBone.Substring(1);
            if (name.EndsWith("ThumbProximal", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Proximal".Length) + "Metacarpal";
            else if (name.EndsWith("ThumbIntermediate", StringComparison.Ordinal)) name = name.Substring(0, name.Length - "Intermediate".Length) + "Proximal";
            return IsBone(name) ? name : null;
        }

        /// <summary>The nearest ancestor of <paramref name="bone"/> along the canonical parents that
        /// <paramref name="present"/> holds; null when none (always for <c>hips</c>).</summary>
        public static string PresentParent(string bone, ICollection<string> present)
        {
            for (string p = Parent(bone); p != null; p = Parent(p))
                if (present.Contains(p)) return p;
            return null;
        }
    }
}
