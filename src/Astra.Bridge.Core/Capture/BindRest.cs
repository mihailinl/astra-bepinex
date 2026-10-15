// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge.Capture
{
    /// <summary>One skinned mesh of a character, as the rest pose is read from it: the bind pose of each
    /// humanoid bone it skins, as a 4×4 matrix in the character root's frame (column-major: the
    /// columns are the bone's X, Y and Z axes and its position).</summary>
    public sealed class BindMesh
    {
        public string Name;
        /// <summary>The mesh's own transform hangs below a humanoid bone: it moves with the animation,
        /// so its frame now is not its frame at bind time — used only for a bone no other mesh has.</summary>
        public bool UnderBone;
        /// <summary>VRM bone → its bind matrix (16 numbers, column-major) in the root's frame.</summary>
        public readonly Dictionary<string, double[]> Bones = new Dictionary<string, double[]>();
    }

    /// <summary>
    /// The rest pose of a game's rig from its skinned meshes' bind poses (<see cref="Choose"/>), and a
    /// bind matrix turned into a rotation honestly (<see cref="FromMatrix"/>). No Unity here.
    /// </summary>
    public static class BindRest
    {
        /// <summary>Two meshes whose bind poses of one bone differ by more than this (degrees) are
        /// named in a warning: the rest came from the first.</summary>
        public const double Disagree = 3;

        /// <summary>
        /// The rest of each of <paramref name="wanted"/>'s bones: from the mesh that skins the MOST of
        /// them (a body, not a glove or a hat bound some other way), a mesh that moves with a bone
        /// last; a bone that mesh lacks from the next one. <paramref name="warnings"/> says where two
        /// meshes disagree on a bone by more than <see cref="Disagree"/>°, and where a bind matrix is
        /// mirrored or skewed; <paramref name="missing"/> lists the bones no mesh skins;
        /// <paramref name="source"/> names the mesh(es) the rest came from.
        /// </summary>
        public static List<RestBone> Choose(IList<BindMesh> meshes, IEnumerable<string> wanted,
            out List<string> warnings, out List<string> missing, out string source)
        {
            warnings = new List<string>();
            missing = new List<string>();
            var wantedList = new List<string>(wanted);
            var ranked = new List<BindMesh>(meshes);
            int Count(BindMesh m)
            {
                int c = 0;
                foreach (var b in wantedList)
                    if (m.Bones.ContainsKey(b)) c++;
                return c;
            }
            // Stable: the most bones first, a mesh that moves with a bone after every one that does not.
            var order = new List<(BindMesh Mesh, int Index)>();
            for (int i = 0; i < ranked.Count; i++) order.Add((ranked[i], i));
            order.Sort((a, b) =>
            {
                if (a.Mesh.UnderBone != b.Mesh.UnderBone) return a.Mesh.UnderBone ? 1 : -1;
                int byCount = Count(b.Mesh).CompareTo(Count(a.Mesh));
                return byCount != 0 ? byCount : a.Index.CompareTo(b.Index);
            });

            var rest = new List<RestBone>();
            var used = new List<string>();
            var said = new HashSet<string>();
            foreach (var bone in wantedList)
            {
                BindMesh from = null;
                Quat rot = Quat.Identity;
                Vec3 pos = default;
                foreach (var (mesh, _) in order)
                {
                    if (!mesh.Bones.TryGetValue(bone, out var m)) continue;
                    var r = FromMatrix(m, out var p, out bool mirrored, out double skew);
                    if (from == null)
                    {
                        from = mesh;
                        rot = r;
                        pos = p;
                        if (mirrored) warnings.Add($"the bind pose of {bone} in '{mesh.Name}' is mirrored: its rest is taken unmirrored, and the capture may turn it");
                        else if (skew > 0.05) warnings.Add($"the bind pose of {bone} in '{mesh.Name}' is skewed (a non-uniform scale): its rest is the nearest rotation");
                        continue;
                    }
                    double off = Quat.Angle(rot, r);
                    if (off > Disagree && said.Add(mesh.Name + "\n" + from.Name))
                        warnings.Add($"'{mesh.Name}' and '{from.Name}' disagree on the rest of {bone} by {off:0}°: the rest is '{from.Name}''s");
                }
                if (from == null)
                {
                    missing.Add(bone);
                    continue;
                }
                if (!used.Contains(from.Name)) used.Add(from.Name);
                rest.Add(new RestBone(bone, rot, pos));
            }
            source = string.Join(", ", used);
            return rest;
        }

        /// <summary>
        /// A bind matrix → the bone's rotation and position. The axes are made orthonormal first
        /// (Z kept, Y made square to it, X = Y × Z), so a non-uniform scale (<paramref name="skew"/>: how
        /// far from square the matrix's axes were) still gives the nearest rotation; a MIRRORED matrix
        /// (its X axis flipped: a negative determinant) gives the rotation of its unmirrored twin, and
        /// says so — a rotation cannot hold a mirror.
        /// </summary>
        public static Quat FromMatrix(double[] m, out Vec3 position, out bool mirrored, out double skew)
        {
            var x = new Vec3(m[0], m[1], m[2]);
            var y = new Vec3(m[4], m[5], m[6]);
            var z = new Vec3(m[8], m[9], m[10]);
            position = new Vec3(m[12], m[13], m[14]);
            mirrored = Dot(Quat.Cross(x, y), z) < 0;
            var xn = Unit(x);
            var yn = Unit(y);
            var zn = Unit(z);
            skew = Math.Abs(Dot(xn, yn)) + Math.Abs(Dot(yn, zn)) + Math.Abs(Dot(xn, zn));
            var zo = zn;
            var yo = Unit(yn - zo * Dot(yn, zo));
            var xo = Quat.Cross(yo, zo);
            return FromAxes(xo, yo, zo);
        }

        /// <summary>The rotation whose X, Y and Z axes are these (orthonormal, right-handed in the
        /// algebra: X = Y × Z).</summary>
        public static Quat FromAxes(Vec3 x, Vec3 y, Vec3 z)
        {
            double m00 = x.X, m10 = x.Y, m20 = x.Z;
            double m01 = y.X, m11 = y.Y, m21 = y.Z;
            double m02 = z.X, m12 = z.Y, m22 = z.Z;
            double trace = m00 + m11 + m22;
            Quat q;
            if (trace > 0)
            {
                double s = 0.5 / Math.Sqrt(trace + 1);
                q = new Quat((m21 - m12) * s, (m02 - m20) * s, (m10 - m01) * s, 0.25 / s);
            }
            else if (m00 > m11 && m00 > m22)
            {
                double s = 2 * Math.Sqrt(1 + m00 - m11 - m22);
                q = new Quat(0.25 * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s);
            }
            else if (m11 > m22)
            {
                double s = 2 * Math.Sqrt(1 + m11 - m00 - m22);
                q = new Quat((m01 + m10) / s, 0.25 * s, (m12 + m21) / s, (m02 - m20) / s);
            }
            else
            {
                double s = 2 * Math.Sqrt(1 + m22 - m00 - m11);
                q = new Quat((m02 + m20) / s, (m12 + m21) / s, 0.25 * s, (m10 - m01) / s);
            }
            return q.Normalized;
        }

        static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        static Vec3 Unit(Vec3 v)
        {
            double l = v.Length;
            return l > 1e-12 ? v * (1 / l) : v;
        }
    }
}
