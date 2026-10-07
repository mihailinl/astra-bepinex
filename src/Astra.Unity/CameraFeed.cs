// SPDX-License-Identifier: MIT
using System;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>Unity's world (left-handed, +Y up) → the bridge's (glTF: right-handed, +Y up): z flips.
    /// Mirroring the world and the camera together leaves the picture the same.</summary>
    static class Wire
    {
        public static Vec3 Of(Vector3 v) => new Vec3(v.x, v.y, -v.z);
    }

    /// <summary>One camera we sent, kept to composite the picture rendered for it.</summary>
    struct Shot
    {
        public long Id;
        public Vector3 Pos;
        public Vector3 Right, Up, Fwd;
        /// <summary>tan of the half field of view, horizontally and vertically.</summary>
        public float TanX, TanY;
    }

    /// <summary>
    /// Sends the game's camera, once per frame, and remembers what it sent: the picture that comes
    /// back says which camera it was drawn for, and the compositor needs that camera's pose.
    /// </summary>
    sealed class CameraFeed
    {
        readonly Shot[] history = new Shot[64];
        long next;

        /// <summary>Build the <c>cam</c> message for <paramref name="cam"/> now (null if it cannot be sent).</summary>
        public string Message(Camera cam, int maxHeight)
        {
            var t = cam.transform;
            float aspect = cam.aspect;
            if (!(aspect > 0.01f) || cam.orthographic) return null;
            int h = Math.Max(16, Math.Min(cam.pixelHeight, maxHeight));
            int w = Math.Max(16, (int)Math.Round(h * aspect));
            float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var shot = new Shot
            {
                Id = ++next,
                Pos = t.position,
                Right = t.right,
                Up = t.up,
                Fwd = t.forward,
                TanX = tanY * aspect,
                TanY = tanY,
            };
            string json = Messages.Cam(shot.Id, Wire.Of(shot.Pos), Wire.Of(shot.Fwd), Wire.Of(shot.Up), cam.fieldOfView, w, h);
            if (json != null) history[shot.Id % history.Length] = shot;
            return json;
        }

        public bool Find(long id, out Shot shot)
        {
            shot = history[((id % history.Length) + history.Length) % history.Length];
            return shot.Id == id && id != 0;
        }
    }
}
