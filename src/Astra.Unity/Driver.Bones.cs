// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Astra.Bridge;
using Astra.Sdk;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// Her BONES (engine #80, <c>hello.bones</c>, flag 256): the integration's list said at every hello,
    /// each frame's section read with the picture it was made for (<see cref="Compositor.TryMainBone"/>),
    /// and the items an integration holds in her hand placed there — at the moment the compositor takes
    /// the main view's picture, which on Built-in is the main camera's pre-cull, before the game's scene
    /// is rendered: the item is drawn in lockstep with her arm.
    /// </summary>
    public sealed partial class Driver
    {
        // The integration whose bones the NEXT hello says (the link asks on its own thread).
        volatile GameIntegration bonesOf;
        // The list the last hello said: a frame's section indexes it.
        volatile List<string> bonesSent;
        // The list of the session the ring is open for (main thread).
        List<string> sessionBones = new List<string>();
        // Whose held items were placed last: another integration's are hidden when it takes over.
        GameIntegration heldOwner;
        readonly HashSet<HeldItem> heldFaulted = new HashSet<HeldItem>();

        /// <summary><c>hello.bones</c>, asked by the link at every hello (its own thread).</summary>
        IList<string> BonesForHello()
        {
            var g = bonesOf;
            var sent = Messages.BonesForHello(g?.BonesForHello());
            if (g != null) g.BonesSaid = true;
            bonesSent = sent;
            return sent;
        }

        /// <summary>A new session's ring is open: the list and revision its frames' bones belong to.</summary>
        void BonesSession()
        {
            if (compositor == null) return;
            int rev = link?.Hello?.BonesRev ?? 0;
            sessionBones = rev != 0 ? bonesSent ?? new List<string>() : new List<string>();
            compositor.BonesRev = sessionBones.Count > 0 ? rev : 0;
        }

        /// <summary>Where <paramref name="bone"/> is in the main view's picture taken last, in Unity's
        /// world: the engine's glTF axes with z flipped, as her caster is — the position's z, and the
        /// quaternion's x and y.</summary>
        bool TryBone(string bone, out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            int index = sessionBones.IndexOf(bone);
            if (compositor == null || index < 0 || !compositor.TryMainBone(index, out var b) || !b.Valid) return false;
            float x = -b.X, y = -b.Y, z = b.Z, w = b.W;
            float norm = Mathf.Sqrt(x * x + y * y + z * z + w * w);
            if (!(norm > 1e-4f)) return false;
            rotation = new Quaternion(x / norm, y / norm, z / norm, w / norm);
            position = new Vector3((float)b.Position.X, (float)b.Position.Y, -(float)b.Position.Z);
            return true;
        }

        /// <summary>The compositor took the main view's picture (or has none): move every held item to
        /// its bone in it, or hide it. An item whose GameObject the game destroyed is let go; one whose
        /// placement throws is hidden and let go, once, with the cause in the log.</summary>
        void PlaceHeld()
        {
            var g = integration;
            if (g == null || g.Held.Count == 0) return;
            bool drawn = compositor != null && compositor.Show;
            for (int i = g.Held.Count - 1; i >= 0; i--)
            {
                var h = g.Held[i];
                if (h.Item == null)
                {
                    g.Held.RemoveAt(i);
                    continue;
                }
                try
                {
                    if (drawn && TryBone(h.Bone, out var p, out var r))
                    {
                        h.Item.transform.SetPositionAndRotation(p + r * h.GripPosition, r * h.GripRotation);
                        h.Show(true);
                    }
                    else h.Show(false);
                }
                catch (Exception e)
                {
                    g.Held.RemoveAt(i);
                    if (heldFaulted.Add(h))
                        log.LogError($"the item '{h.Item.name}' in her hand could not be placed and is let go: {e}");
                    try { h.Show(false); } catch { /* it is let go either way */ }
                }
            }
        }

        /// <summary>Once a frame: while she is not drawn there is no picture to hold an item in, so every
        /// held item is hidden; another integration's are hidden when this one takes over.</summary>
        void StepHeld()
        {
            if (!ReferenceEquals(heldOwner, integration))
            {
                HideHeld(heldOwner);
                heldOwner = integration;
            }
            if (!Runtime.Drawn) HideHeld(integration);
        }

        static void HideHeld(GameIntegration g)
        {
            if (g == null) return;
            foreach (var h in g.Held)
                if (h.Shown && h.Item != null) h.Show(false);
        }
    }
}
