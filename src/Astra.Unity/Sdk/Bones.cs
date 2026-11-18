// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Sdk
{
    public sealed partial class GameIntegration
    {
        // Her bones this integration asks the pose of (hello.bones), in order. Read on the link's own
        // thread at every hello, so every touch locks it.
        readonly List<string> boneNames = new List<string>();
        // Astra was told this integration's bones (a hello said them): one named after is streamed only
        // from the next connection.
        internal volatile bool BonesSaid;
        // The items it holds in her hand. Main thread only.
        internal readonly List<HeldItem> Held = new List<HeldItem>();

        /// <summary>
        /// Ask for the pose of her BONES beside every picture of her (<c>hello.bones</c>): VRM 1.0
        /// humanoid bone names, camelCase — <c>"rightHand"</c>, <c>"leftHand"</c>, <c>"head"</c>,
        /// <c>"hips"</c>… — at most 8. Each frame then carries where those bones are in THAT picture, so
        /// an item you draw in her hand moves with her arm, never a frame behind it
        /// (<see cref="TryBone"/>, <see cref="HoldItem"/>). Name them where you register: the list is
        /// said to Astra when the game connects, and a bone named later is streamed only from the next
        /// connection (the foundation warns). A name that is no humanoid bone is not streamed — Astra's
        /// engine says so in the log.
        /// </summary>
        /// <exception cref="ArgumentException">More than 8 bones in all.</exception>
        public GameIntegration UseBones(params string[] bones)
        {
            if (bones == null) return this;
            foreach (var bone in bones) Name(bone);
            return this;
        }

        /// <summary>Add <paramref name="bone"/> to the list (no-op when there); true when it is listed.</summary>
        bool Name(string bone)
        {
            if (string.IsNullOrEmpty(bone)) return false;
            lock (boneNames)
            {
                if (boneNames.Contains(bone)) return true;
                if (boneNames.Count >= Messages.MaxBones)
                    throw new ArgumentException($"at most {Messages.MaxBones} bones: '{bone}' would be the {Messages.MaxBones + 1}th", nameof(bone));
                boneNames.Add(bone);
            }
            if (BonesSaid)
                AstraSdk.Warn?.Invoke($"integration '{Id}' asked for her bone '{bone}' after Astra was told its bones: "
                    + "it is streamed only from the next connection. Name every bone where you register (UseBones).");
            return true;
        }

        /// <summary>The list as the next hello says it. Any thread.</summary>
        internal string[] BonesForHello()
        {
            lock (boneNames) return boneNames.ToArray();
        }

        /// <summary>
        /// Where <paramref name="bone"/> (one of <see cref="UseBones"/>) is in the picture of her drawn
        /// into the main camera now: the game's world, and its turn from her rest pose (at rest her
        /// bones' axes are her body's own: up, her facing, her left). False while there is none — no
        /// picture, a character with no body to pose, a bone her body lacks, or one not asked for.
        /// Read it from the foundation's own moment (<see cref="HoldItem"/> is placed there for you):
        /// asked elsewhere it is the pose of the picture taken last.
        /// </summary>
        public bool TryBone(string bone, out Vector3 position, out Quaternion rotation)
        {
            var lookup = Astra.Unity.Runtime.Bone;
            if (lookup != null && !string.IsNullOrEmpty(bone)) return lookup(bone, out position, out rotation);
            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        /// <summary>
        /// Put <paramref name="item"/> in her hand: every picture of her, the foundation places it at
        /// <paramref name="bone"/>'s pose IN THAT PICTURE with your grip offset (a position and a turn in
        /// the bone's frame — the item's own numbers, tuned once, e.g. a handle across her palm), and
        /// activates it; while there is no pose (no picture, her body lacks the bone) it deactivates it.
        /// Give it a GameObject that is only the item's LOOK — no game script, no collider, no network
        /// object: it is switched on and off at will. It is drawn by the game, so the game's depth,
        /// light and shadows treat it as its own. The bone is asked for if it was not
        /// (<see cref="UseBones"/>). <see cref="HeldItem.Release"/> lets go of it.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="bone"/> is empty, or would be a 9th bone.</exception>
        public HeldItem HoldItem(GameObject item, string bone, Vector3 gripPosition, Quaternion gripRotation)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (!Name(bone)) throw new ArgumentException("a bone name is needed (\"rightHand\")", nameof(bone));
            var held = new HeldItem(this, item, bone, gripPosition, gripRotation);
            Held.Add(held);
            held.Show(false); // until a picture of her carries the bone
            return held;
        }
    }

    /// <summary>An item in her hand (<see cref="GameIntegration.HoldItem"/>). Main thread only.</summary>
    public sealed class HeldItem
    {
        readonly GameIntegration owner;

        internal HeldItem(GameIntegration owner, GameObject item, string bone, Vector3 gripPosition, Quaternion gripRotation)
        {
            this.owner = owner;
            Item = item;
            Bone = bone;
            GripPosition = gripPosition;
            GripRotation = gripRotation;
        }

        /// <summary>The item's look, which the foundation moves, activates and deactivates.</summary>
        public GameObject Item { get; }

        /// <summary>The bone it follows.</summary>
        public string Bone { get; }

        /// <summary>Where the item sits in the bone's frame (metres; at rest her bones' axes are her
        /// body's own: +Y up, +Z her facing, +X her left).</summary>
        public Vector3 GripPosition { get; set; }

        /// <summary>How the item is turned in the bone's frame.</summary>
        public Quaternion GripRotation { get; set; }

        /// <summary>It is in her hand in the picture drawn now.</summary>
        public bool Shown { get; private set; }

        /// <summary>Let go of it: deactivated and no longer moved (the GameObject is yours to destroy).</summary>
        public void Release()
        {
            owner.Held.Remove(this);
            Show(false);
        }

        internal void Show(bool on)
        {
            Shown = on;
            if (Item != null && Item.activeSelf != on) Item.SetActive(on);
        }
    }
}
