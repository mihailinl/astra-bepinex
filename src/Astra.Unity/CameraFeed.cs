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

    /// <summary>
    /// A camera's pose as it RENDERS — read off its view matrix, never its transform: a mirror's
    /// camera may be handed an explicit <c>worldToCameraMatrix</c>, usually a REFLECTED one, and its
    /// transform is then wherever the mirror's script left it. A reflected view is taken apart into
    /// the proper camera behind the mirror, which the engine can draw her from, and
    /// <see cref="Mirrored"/>: the picture that camera renders is the proper one flipped left to
    /// right. For any other camera this is its transform's pose.
    /// <para>A plain camera's view matrix is ITSELF a reflection (OpenGL's convention, looking down
    /// −z: determinant −1), so a reflected view is the one with a POSITIVE determinant. Testing for a
    /// negative one took every camera for a mirror and drew her mirrored about the screen's centre
    /// (seen in the testbed, 2026-10-07).</para>
    /// </summary>
    struct CameraPose
    {
        public Vector3 Pos, Right, Up, Fwd;
        public bool Mirrored;

        public static CameraPose Of(Camera cam)
        {
            var w = cam.worldToCameraMatrix; // view space: x right, y up, looking down −z
            bool mirrored = w.determinant > 0;
            if (mirrored) w.SetRow(0, -w.GetRow(0));
            var c = w.inverse;
            Vector3 right = c.GetColumn(0), up = c.GetColumn(1), back = c.GetColumn(2);
            return new CameraPose { Pos = c.GetColumn(3), Right = right.normalized, Up = up.normalized, Fwd = -back.normalized, Mirrored = mirrored };
        }
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

        /// <summary>Build the <c>cam</c> message for <paramref name="cam"/>, as <paramref name="view"/>,
        /// now (null if it cannot be sent). Its field of view and aspect are read off the camera's
        /// PROJECTION, which a game may set explicitly (a mirror's camera borrows the player camera's).</summary>
        public string Message(Camera cam, int maxHeight, int view = 0)
        {
            var pose = CameraPose.Of(cam);
            var p = cam.projectionMatrix;
            // A projection may flip an axis (a mirror done in the projection): the lens is the same.
            if (cam.orthographic || !(Mathf.Abs(p.m00) > 1e-4f) || !(Mathf.Abs(p.m11) > 1e-4f)) return null;
            float tanY = 1f / Mathf.Abs(p.m11), tanX = 1f / Mathf.Abs(p.m00);
            float aspect = tanX / tanY;
            int h = Math.Max(16, Math.Min(cam.pixelHeight, maxHeight));
            int w = Math.Max(16, (int)Math.Round(h * aspect));
            var shot = new Shot
            {
                Id = ++next,
                Pos = pose.Pos,
                Right = pose.Right,
                Up = pose.Up,
                Fwd = pose.Fwd,
                TanX = tanX,
                TanY = tanY,
            };
            double fovY = 2 * Math.Atan(tanY) * (180 / Math.PI);
            string json = Messages.Cam(shot.Id, Wire.Of(shot.Pos), Wire.Of(shot.Fwd), Wire.Of(shot.Up), fovY, w, h, null, view);
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
