// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading;

namespace Astra.Bridge
{
    /// <summary>Her view from the game's sun (plane 3, flag 32) — see the protocol's "sun view".</summary>
    public struct SunView
    {
        /// <summary>Texels per side.</summary>
        public int N;
        public Vec3 Centre;
        public Vec3 Right, Up;
        /// <summary>The way the light TRAVELS.</summary>
        public Vec3 Dir;
        public float HalfExtent;
        public float Near;
        /// <summary>N×N little-endian f32, rows top-down; metres along <see cref="Dir"/> from the near
        /// plane, 0 = not her. Valid only while the frame is (see <see cref="FrameRing.StillValid"/>).</summary>
        public IntPtr Depth;
    }

    /// <summary>One published frame, read in place from the ring.</summary>
    public struct RingFrame
    {
        public int Slot;
        public long Seq;
        public long EngineFrame;
        /// <summary>The <c>cam.id</c> it was rendered for.</summary>
        public long HostFrame;
        public int Width, Height;
        public float Near, Far, FovY;
        public uint Flags;
        /// <summary>The camera position it was rendered from (glTF world, verbatim).</summary>
        public Vec3 CamPos;
        public float Echo0, Echo1, Echo2;
        public long CaptureNs, PublishNs;

        /// <summary>Width×Height RGBA8, premultiplied, display-encoded (sRGB), rows top-down.</summary>
        public IntPtr Colour;
        /// <summary>Width×Height f32 linear eye depth in metres; 0 where she is not.</summary>
        public IntPtr Depth;
        /// <summary>Bytes in each of <see cref="Colour"/> and <see cref="Depth"/>.</summary>
        public int PlaneBytes;
        public bool HasSun;
        public SunView Sun;
    }

    /// <summary>
    /// The engine's frame ring ("MCPT" v1), mapped read-only. A seqlock: <see cref="TryLatest"/>
    /// gives the newest whole frame IN PLACE; copy what you need, then ask
    /// <see cref="StillValid"/> — false means the engine reused the slot while you copied, and the
    /// copy must be thrown away. Every size the header and the descriptors claim is checked
    /// against the mapping before a pointer is handed out.
    /// </summary>
    public sealed unsafe class FrameRing : IDisposable
    {
        public const uint Magic = 0x5450434D; // 'M','C','P','T'
        public const uint FlagDepthNdc = 1, FlagBottomUp = 2, FlagReversedZ = 4, FlagLinearDepth = 8, FlagNoPlane3 = 16, FlagSunView = 32;
        const uint SunMagic = 0x564E5553; // 'S','U','N','V'
        const int SunHeader = 128;
        const int DescBase = 256, DescStride = 128;

        readonly RingMapping map;
        readonly byte* b;
        readonly int headerBytes;
        readonly long stride;

        public int Slots { get; }
        public int MaxWidth { get; }
        public int MaxHeight { get; }
        /// <summary>The path or name actually mapped.</summary>
        public string Source => map.Source;

        FrameRing(RingMapping map)
        {
            this.map = map;
            b = map.Base;
            if (map.Length < 4096) throw Bad("shorter than its header");
            if (*(uint*)b != Magic) throw Bad("not an MCPT ring");
            if (*(uint*)(b + 4) != 1) throw Bad($"MCPT version {*(uint*)(b + 4)}, this reader knows 1");
            headerBytes = *(int*)(b + 8);
            Slots = *(int*)(b + 12);
            stride = *(long*)(b + 16);
            MaxWidth = *(int*)(b + 24);
            MaxHeight = *(int*)(b + 28);
            if (headerBytes < DescBase + DescStride || Slots < 1 || Slots > (headerBytes - DescBase) / DescStride)
                throw Bad($"header {headerBytes} B with {Slots} slots");
            if (MaxWidth < 1 || MaxHeight < 1 || stride < 8L * MaxWidth * MaxHeight)
                throw Bad($"slot stride {stride} for {MaxWidth}x{MaxHeight}");
            if (headerBytes + Slots * stride > map.Length)
                throw Bad($"{Slots} slots of {stride} B do not fit {map.Length} B");
        }

        /// <summary>
        /// Map the ring the engine named in its hello (<see cref="HelloReply.Shm"/>). A Windows game
        /// under Wine/Proton gets a Unix path from a Linux engine and maps it through Wine's
        /// <c>Z:</c> drive; a Windows engine names a <c>Local\</c> mapping.
        /// </summary>
        /// <param name="shm">The engine's name for it.</param>
        /// <param name="overridePath">A path or mapping name to use instead (a player's config).</param>
        /// <exception cref="IOException">It cannot be opened, or it is not a valid ring.</exception>
        public static FrameRing Open(string shm, string overridePath = null)
        {
            var mapping = RingMapping.Open(string.IsNullOrEmpty(overridePath) ? shm : overridePath);
            try
            {
                return new FrameRing(mapping);
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }

        /// <summary>The publish counter: it changes whenever a frame is published.</summary>
        public long Counter => Volatile.Read(ref *(long*)(b + 32));

        /// <summary>
        /// The newest whole frame, if one was published since <paramref name="seen"/> (a counter value;
        /// start from 0). On success <paramref name="seen"/> moves to it. False: nothing new, or the
        /// newest is being written right now (call again later).
        /// </summary>
        public bool TryLatest(ref long seen, out RingFrame f)
        {
            f = default;
            long counter = Volatile.Read(ref *(long*)(b + 32));
            if (counter == seen) return false;
            int slot = Volatile.Read(ref *(int*)(b + 40));
            if (slot < 0 || slot >= Slots) return false;
            byte* d = b + DescBase + DescStride * slot;
            long seq = Volatile.Read(ref *(long*)d);
            if ((seq & 1) != 0) return false;
            Thread.MemoryBarrier();

            int w = *(int*)(d + 24), h = *(int*)(d + 28);
            if (w < 0 || h < 0 || w > MaxWidth || h > MaxHeight) return false;
            long plane = 4L * w * h;
            byte* data = b + headerBytes + stride * slot;
            f.Slot = slot;
            f.Seq = seq;
            f.EngineFrame = *(long*)(d + 8);
            f.HostFrame = *(long*)(d + 16);
            f.Width = w;
            f.Height = h;
            f.Near = *(float*)(d + 32);
            f.Far = *(float*)(d + 36);
            f.FovY = *(float*)(d + 40);
            f.Flags = *(uint*)(d + 44);
            f.CamPos = new Vec3(*(double*)(d + 48), *(double*)(d + 56), *(double*)(d + 64));
            f.Echo0 = *(float*)(d + 72);
            f.Echo1 = *(float*)(d + 76);
            f.Echo2 = *(float*)(d + 80);
            f.CaptureNs = *(long*)(d + 88);
            f.PublishNs = *(long*)(d + 96);
            f.Colour = (IntPtr)data;
            f.Depth = (IntPtr)(data + plane);
            f.PlaneBytes = (int)plane;
            if ((f.Flags & FlagSunView) != 0)
            {
                byte* s = data + 2 * plane;
                long room = stride - 2 * plane;
                int n = *(int*)(s + 4);
                if (room >= SunHeader && *(uint*)s == SunMagic && n > 0 && n <= 4096 && SunHeader + 4L * n * n <= room)
                {
                    f.HasSun = true;
                    f.Sun = new SunView
                    {
                        N = n,
                        Centre = new Vec3(*(double*)(s + 8), *(double*)(s + 16), *(double*)(s + 24)),
                        Right = new Vec3(*(float*)(s + 32), *(float*)(s + 36), *(float*)(s + 40)),
                        Up = new Vec3(*(float*)(s + 44), *(float*)(s + 48), *(float*)(s + 52)),
                        Dir = new Vec3(*(float*)(s + 56), *(float*)(s + 60), *(float*)(s + 64)),
                        HalfExtent = *(float*)(s + 68),
                        Near = *(float*)(s + 72),
                        Depth = (IntPtr)(s + SunHeader),
                    };
                }
            }

            Thread.MemoryBarrier();
            if (Volatile.Read(ref *(long*)d) != seq) return false;
            seen = counter;
            return true;
        }

        /// <summary>Is <paramref name="f"/> still the frame its slot holds? Ask AFTER copying from it.</summary>
        public bool StillValid(ref RingFrame f)
        {
            Thread.MemoryBarrier();
            return Volatile.Read(ref *(long*)(b + DescBase + DescStride * f.Slot)) == f.Seq;
        }

        static IOException Bad(string why) => new IOException("invalid frame ring: " + why);

        public void Dispose() => map.Dispose();
    }
}
