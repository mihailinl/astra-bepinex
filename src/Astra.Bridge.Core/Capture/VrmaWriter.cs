// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// Writes a <see cref="CapturedClip"/> as a <c>.vrma</c>: a binary glTF 2.0 (GLB) carrying
    /// <c>VRMC_vrm_animation</c> 1.0 — the file Astra's engine reads (<c>anim/vrma.rs</c>), and any
    /// VRM Animation reader. Pure C#, no Unity: unit-tested.
    /// <para>
    /// The file's skeleton is the VRM normalized one: a node per captured bone, parented along the
    /// humanoid, with the T-pose offsets as translations and NO rotations — so a reader's rest
    /// transport is the identity and every rotation key IS the bone's normalized local rotation. One
    /// animation: a rotation channel per bone and the hips' translation, all LINEAR over one shared
    /// time accessor. Unity's axes become glTF's here (<see cref="Quat.ToGltf()"/>), and neighbouring
    /// rotation keys are kept in one hemisphere.
    /// </para>
    /// </summary>
    public static class VrmaWriter
    {
        const uint Magic = 0x46546C67;      // "glTF"
        const uint JsonChunk = 0x4E4F534A;  // "JSON"
        const uint BinChunk = 0x004E4942;   // "BIN\0"
        const int Float = 5126;

        /// <summary>The GLB bytes of <paramref name="clip"/>, its animation named
        /// <paramref name="name"/>. Throws <see cref="ArgumentException"/> for a clip with fewer than
        /// two frames.</summary>
        public static byte[] Write(CapturedClip clip, string name)
        {
            if (clip == null || clip.Count < 2) throw new ArgumentException("a clip needs at least two frames", nameof(clip));
            var sk = clip.Skeleton;
            int n = sk.Bones.Length, frames = clip.Count;

            // The binary: times, then one rotation track per bone, then the hips' translations.
            var bin = new MemoryStream();
            var w = new BinaryWriter(bin);
            var views = new List<(int Offset, int Length)>();
            int Begin() => (int)bin.Position;
            void End(int start) => views.Add((start, (int)bin.Position - start));

            int at = Begin();
            foreach (var t in clip.Times) w.Write(t);
            End(at);
            for (int b = 0; b < n; b++)
            {
                at = Begin();
                var prev = Quat.Identity;
                for (int f = 0; f < frames; f++)
                {
                    var q = clip.Rotation(f, b).ToGltf().Near(prev);
                    prev = q;
                    w.Write((float)q.X);
                    w.Write((float)q.Y);
                    w.Write((float)q.Z);
                    w.Write((float)q.W);
                }
                End(at);
            }
            at = Begin();
            for (int f = 0; f < frames; f++)
            {
                var p = Quat.ToGltfPosition(clip.Hips(f));
                w.Write((float)p.X);
                w.Write((float)p.Y);
                w.Write((float)p.Z);
            }
            End(at);
            w.Flush();
            byte[] binary = Pad(bin.ToArray(), 0);

            var j = new JsonWriter().Open();
            j.Open("asset").Str("version", "2.0").Str("generator", "astra-bepinex capture").Close();
            j.Strs("extensionsUsed", new[] { "VRMC_vrm_animation" });
            j.Open("extensions").Open("VRMC_vrm_animation").Str("specVersion", "1.0").Open("humanoid").Open("humanBones");
            for (int b = 0; b < n; b++) j.Open(sk.Bones[b]).Num("node", b).Close();
            j.Close().Close().Close().Close();
            j.Num("scene", 0);
            j.OpenArray("scenes").Item().Nums("nodes", new double[] { 0 }, 1).Close().CloseArray();

            j.OpenArray("nodes");
            for (int b = 0; b < n; b++)
            {
                var local = sk.Parents[b] < 0 ? sk.RestPositions[b] : sk.RestPositions[b] - sk.RestPositions[sk.Parents[b]];
                j.Item().Str("name", sk.Bones[b]).Vec("translation", Quat.ToGltfPosition(local));
                var children = new List<double>();
                for (int c = 0; c < n; c++)
                    if (sk.Parents[c] == b) children.Add(c);
                if (children.Count > 0) j.Nums("children", children.ToArray(), children.Count);
                j.Close();
            }
            j.CloseArray();

            j.OpenArray("buffers").Item().Num("byteLength", binary.Length).Close().CloseArray();
            j.OpenArray("bufferViews");
            foreach (var v in views) j.Item().Num("buffer", 0).Num("byteOffset", v.Offset).Num("byteLength", v.Length).Close();
            j.CloseArray();

            j.OpenArray("accessors");
            j.Item().Num("bufferView", 0).Num("componentType", Float).Num("count", frames).Str("type", "SCALAR")
                .Nums("min", new double[] { clip.Times[0] }, 1).Nums("max", new double[] { clip.Times[frames - 1] }, 1).Close();
            for (int b = 0; b < n; b++)
                j.Item().Num("bufferView", 1 + b).Num("componentType", Float).Num("count", frames).Str("type", "VEC4").Close();
            j.Item().Num("bufferView", 1 + n).Num("componentType", Float).Num("count", frames).Str("type", "VEC3").Close();
            j.CloseArray();

            j.OpenArray("animations").Item().Str("name", name ?? "capture");
            j.OpenArray("channels");
            for (int b = 0; b < n; b++)
                j.Item().Num("sampler", b).Open("target").Num("node", b).Str("path", "rotation").Close().Close();
            j.Item().Num("sampler", n).Open("target").Num("node", 0).Str("path", "translation").Close().Close();
            j.CloseArray();
            j.OpenArray("samplers");
            for (int b = 0; b <= n; b++)
                j.Item().Num("input", 0).Num("output", 1 + b).Str("interpolation", "LINEAR").Close();
            j.CloseArray();
            j.Close().CloseArray();
            j.Close();

            byte[] json = Pad(Encoding.UTF8.GetBytes(j.ToString()), 0x20);
            var glb = new MemoryStream(12 + 8 + json.Length + 8 + binary.Length);
            var o = new BinaryWriter(glb);
            o.Write(Magic);
            o.Write(2u);
            o.Write((uint)(12 + 8 + json.Length + 8 + binary.Length));
            o.Write((uint)json.Length);
            o.Write(JsonChunk);
            o.Write(json);
            o.Write((uint)binary.Length);
            o.Write(BinChunk);
            o.Write(binary);
            o.Flush();
            return glb.ToArray();
        }

        /// <summary><paramref name="data"/> padded to a multiple of four bytes with <paramref name="with"/>
        /// (a GLB chunk's rule: spaces after JSON, zeros after binary).</summary>
        static byte[] Pad(byte[] data, byte with)
        {
            int padded = (data.Length + 3) & ~3;
            if (padded == data.Length) return data;
            var r = new byte[padded];
            Buffer.BlockCopy(data, 0, r, 0, data.Length);
            for (int i = data.Length; i < padded; i++) r[i] = with;
            return r;
        }
    }
}
