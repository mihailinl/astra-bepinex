// SPDX-License-Identifier: MIT
namespace Astra.Bridge
{
    /// <summary>
    /// What the composite shader (<c>AstraComposite.shader</c>, <c>Her</c>) is handed to find, for each
    /// pixel of the camera presenting now, that point in her picture — drawn for an EARLIER camera. The
    /// shader takes three rows M and an eye e: it looks the present ray r up at M·r, and moves the point p
    /// it found there back into the present view as Mᵀ·p + e, for the depth test.
    /// <para>The rows used to be the rotation between the two views alone. That is exact for a camera
    /// that turns where it stands (first person) and wrong for one that ORBITS the player (third person,
    /// Valheim): there turning the mouse moves the camera by metres, she stands near the centre it turns
    /// about, and a lookup by rotation moved her by the whole angle the camera turned since her picture's
    /// camera — every new picture put her back, so she shook whenever the camera turned, more the older
    /// the picture (Valheim B4, 2026-11-17: 1 frame old at 60 FPS, 6–15 uncapped).</para>
    /// <para><see cref="ThroughHer"/> gives the homography of the plane through her centre, facing the
    /// camera her picture was drawn for: exact for every point at her depth whatever the camera did, off
    /// for the rest of her body only by the parallax of her own depth, and the rotation itself when the
    /// camera did not move. The rows it gives are then no longer a rotation, so Mᵀ is not its inverse:
    /// the eye is corrected so that the point back in the present view is exact at her centre.</para>
    /// </summary>
    public static class Reprojection
    {
        /// <summary>Her centre must be at least this far ahead of the camera she was drawn for, and of
        /// the present eye along it: nearer, the plane through her is no plane to look anything up in.</summary>
        public const double MinDepth = 0.05;

        /// <summary>
        /// Turn the rotation rows <paramref name="r0"/>, <paramref name="r1"/>, <paramref name="r2"/> (the
        /// present view, x right, y up, z forward, to the earlier one) and <paramref name="eye"/> (the
        /// earlier eye in the present view) into the rows and the eye of the plane through
        /// <paramref name="herThen"/> (her centre in the earlier view) facing the earlier camera.
        /// False, leaving all four as they are, when her centre is not ahead of either camera.
        /// </summary>
        public static bool ThroughHer(ref Vec3 r0, ref Vec3 r1, ref Vec3 r2, ref Vec3 eye, Vec3 herThen)
        {
            // pThen = R·(pNow − e). On the plane z_then = D a present point s·r has
            // s = (D + (R·e)_z) / (r2·r), so pThen = H·r up to that scale, with
            // H = R − (R·e) r2ᵀ / (D + (R·e)_z).
            var re = new Vec3(Dot(r0, eye), Dot(r1, eye), Dot(r2, eye));
            double d = herThen.Z, denom = d + re.Z;
            if (!(d >= MinDepth) || !(denom >= MinDepth) || !herThen.IsFinite || !re.IsFinite) return false;
            double k = 1 / denom;
            // Mᵀ·p + e must be Rᵀ·p + e at her centre: with H = R + A, e' = e − Aᵀ·p̄, and every row of A
            // is −k·(R·e)_i·r2ᵀ, so Aᵀ·p̄ = −k·((R·e)·p̄)·r2.
            var eyeOut = eye + r2 * (k * Dot(re, herThen));
            var h0 = r0 - r2 * (re.X * k);
            var h1 = r1 - r2 * (re.Y * k);
            var h2 = r2 * (d * k);
            if (!h0.IsFinite || !h1.IsFinite || !h2.IsFinite || !eyeOut.IsFinite) return false;
            r0 = h0;
            r1 = h1;
            r2 = h2;
            eye = eyeOut;
            return true;
        }

        static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    }
}
