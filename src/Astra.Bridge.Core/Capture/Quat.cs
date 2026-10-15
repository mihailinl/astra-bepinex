// SPDX-License-Identifier: MIT
using System;

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// A rotation, for the capture's pose math (<see cref="CaptureSkeleton"/>) — the few operations it
    /// needs, with no Unity in it so the math is unit-tested here. Its axes are whatever its caller's
    /// are: the capture works in UNITY's (left-handed, +Y up, +Z forward) and only the VRMA writer
    /// turns a rotation into glTF's (<see cref="ToGltf"/>). Unity's conventions throughout: a product
    /// <c>a * b</c> applies <c>b</c> first, then <c>a</c>.
    /// </summary>
    public struct Quat
    {
        public double X, Y, Z, W;

        public Quat(double x, double y, double z, double w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>The inverse of a UNIT rotation.</summary>
        public Quat Inverse => new Quat(-X, -Y, -Z, W);

        public double Dot(Quat o) => X * o.X + Y * o.Y + Z * o.Z + W * o.W;

        public bool IsFinite => Vec3.Finite(X) && Vec3.Finite(Y) && Vec3.Finite(Z) && Vec3.Finite(W);

        /// <summary>Unit length; identity for a zero (or non-finite) one.</summary>
        public Quat Normalized
        {
            get
            {
                double n = Math.Sqrt(Dot(this));
                if (!(n > 1e-12) || !IsFinite) return Identity;
                return new Quat(X / n, Y / n, Z / n, W / n);
            }
        }

        /// <summary>The same rotation with the other sign when that one is nearer <paramref name="to"/>:
        /// a track whose neighbouring keys are in one hemisphere interpolates the short way.</summary>
        public Quat Near(Quat to) => Dot(to) < 0 ? new Quat(-X, -Y, -Z, -W) : this;

        /// <summary><paramref name="v"/> rotated.</summary>
        public Vec3 Rotate(Vec3 v)
        {
            // t = 2 q.xyz × v; v' = v + w t + q.xyz × t
            double tx = 2 * (Y * v.Z - Z * v.Y);
            double ty = 2 * (Z * v.X - X * v.Z);
            double tz = 2 * (X * v.Y - Y * v.X);
            return new Vec3(
                v.X + W * tx + (Y * tz - Z * ty),
                v.Y + W * ty + (Z * tx - X * tz),
                v.Z + W * tz + (X * ty - Y * tx));
        }

        /// <summary>A rotation of <paramref name="degrees"/> about <paramref name="axis"/>.</summary>
        public static Quat AngleAxis(double degrees, Vec3 axis)
        {
            double len = axis.Length;
            if (!(len > 1e-12)) return Identity;
            double half = degrees * Math.PI / 360;
            double s = Math.Sin(half) / len;
            return new Quat(axis.X * s, axis.Y * s, axis.Z * s, Math.Cos(half));
        }

        /// <summary>The shortest rotation turning direction <paramref name="from"/> onto
        /// <paramref name="to"/> (neither need be unit; identity when either is zero).</summary>
        public static Quat FromTo(Vec3 from, Vec3 to)
        {
            double lf = from.Length, lt = to.Length;
            if (!(lf > 1e-12) || !(lt > 1e-12)) return Identity;
            var a = from * (1 / lf);
            var b = to * (1 / lt);
            double d = a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            if (d < -0.999999)
            {
                // Opposite: half a turn about any axis perpendicular to a.
                var axis = Cross(Math.Abs(a.X) < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0), a);
                return AngleAxis(180, axis);
            }
            var c = Cross(a, b);
            return new Quat(c.X, c.Y, c.Z, 1 + d).Normalized;
        }

        /// <summary>The angle between two rotations, in degrees (0..180).</summary>
        public static double Angle(Quat a, Quat b)
        {
            double d = Math.Min(1, Math.Abs(a.Normalized.Dot(b.Normalized)));
            return 2 * Math.Acos(d) * 180 / Math.PI;
        }

        /// <summary>
        /// This rotation in glTF's axes, from Unity's: glTF is right-handed, Unity left-handed, and the
        /// two agree on +Y up and +Z forward (a VRM 1.0 model faces +Z, a Unity character faces +Z), so
        /// the change is a mirror of X — which turns a rotation (x, y, z, w) into (x, −y, −z, w).
        /// </summary>
        public Quat ToGltf() => new Quat(X, -Y, -Z, W);

        /// <summary>A position in glTF's axes, from Unity's: X mirrored (<see cref="ToGltf"/>).</summary>
        public static Vec3 ToGltfPosition(Vec3 p) => new Vec3(-p.X, p.Y, p.Z);

        internal static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public override string ToString() => $"({X:0.####}, {Y:0.####}, {Z:0.####}, {W:0.####})";
    }
}
