// SPDX-License-Identifier: MIT
using System;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// One view's picture of her on the GPU — the two whole-view textures the composite shader samples
    /// (colour, linear depth) — and how a ring frame gets into them.
    /// <para>
    /// A WHOLE frame (an engine without the <c>crop</c> cap) is uploaded whole, as before: ~16 MB a
    /// frame at 1080p copied and uploaded on the game's main thread. A CROPPED frame (flag 128) carries
    /// only her rectangle — she covers a few percent of the view — so only the rectangle is uploaded,
    /// into a texture its own size, and copied on the GPU to its place in the whole-view textures;
    /// where she stood the frame before and stands no more is cleared, tile by tile, from a small
    /// texture of nothing. The whole-view textures are then GPU-only (no CPU copy kept), and the
    /// shader is unchanged.
    /// </para>
    /// </summary>
    sealed class Picture
    {
        /// <summary>What the shader samples: the whole view.</summary>
        public Texture2D Colour, Depth;

        /// <summary>A cropped frame said "nothing of her in this view": there is nothing to draw.</summary>
        public bool Empty { get; private set; }

        // Cropped frames only: the rectangle as uploaded, a tile of nothing to clear with, and the
        // rectangle drawn last (zero size = none).
        Texture2D partColour, partDepth, zeroColour, zeroDepth;
        const int Tile = 256;
        bool cropped;
        int lastX, lastY, lastW, lastH;

        /// <summary>Can this game copy between textures on the GPU (what a cropped frame needs)?</summary>
        public static bool CanTakeCropped => (SystemInfo.copyTextureSupport & UnityEngine.Rendering.CopyTextureSupport.Basic) != 0;

        /// <summary>
        /// Take <paramref name="f"/> in place (a seqlock read: <see cref="FrameRing.StillValid"/> is asked
        /// before anything reaches the GPU). False = the engine reused the slot while it was copied:
        /// nothing changed on the GPU, which goes on showing the last whole picture — unless
        /// <paramref name="fresh"/> (the textures were just made and hold nothing yet).
        /// </summary>
        public bool Load(FrameRing ring, ref RingFrame f, int view, out bool fresh)
        {
            bool crop = f.Cropped;
            fresh = Colour == null || Colour.width != f.Width || Colour.height != f.Height || crop != cropped;
            if (fresh)
            {
                Release();
                Make(f.Width, f.Height, crop, $"view {view}");
            }
            if (!crop)
            {
                Colour.LoadRawTextureData(f.Colour, f.PlaneBytes);
                Depth.LoadRawTextureData(f.Depth, f.PlaneBytes);
                if (!ring.StillValid(ref f)) return false;
                Colour.Apply(false, false);
                Depth.Apply(false, false);
                Empty = false;
                return true;
            }
            if (f.CropW < 1 || f.CropH < 1)
            {
                if (!ring.StillValid(ref f)) return false;
                ClearLast();
                Empty = true;
                return true;
            }
            if (partColour == null || partColour.width != f.CropW || partColour.height != f.CropH)
            {
                Destroy(ref partColour);
                Destroy(ref partDepth);
                partColour = Texture(f.CropW, f.CropH, TextureFormat.RGBA32, $"view {view} part colour");
                partDepth = Texture(f.CropW, f.CropH, TextureFormat.RFloat, $"view {view} part depth");
            }
            partColour.LoadRawTextureData(f.Colour, f.PlaneBytes);
            partDepth.LoadRawTextureData(f.Depth, f.PlaneBytes);
            if (!ring.StillValid(ref f)) return false;
            partColour.Apply(false, false);
            partDepth.Apply(false, false);
            // What she covered last and does not cover now is cleared first; then her rectangle.
            if (!(f.CropX <= lastX && f.CropY <= lastY && lastX + lastW <= f.CropX + f.CropW && lastY + lastH <= f.CropY + f.CropH))
                ClearLast();
            Graphics.CopyTexture(partColour, 0, 0, 0, 0, f.CropW, f.CropH, Colour, 0, 0, f.CropX, f.CropY);
            Graphics.CopyTexture(partDepth, 0, 0, 0, 0, f.CropW, f.CropH, Depth, 0, 0, f.CropX, f.CropY);
            (lastX, lastY, lastW, lastH) = (f.CropX, f.CropY, f.CropW, f.CropH);
            Empty = false;
            return true;
        }

        /// <summary>Clear the rectangle drawn last from the whole-view textures.</summary>
        void ClearLast()
        {
            Clear(lastX, lastY, lastW, lastH);
            lastW = lastH = 0;
        }

        /// <summary>Clear a rectangle of the whole-view textures, a <see cref="Tile"/> at a time.</summary>
        void Clear(int x, int y, int w, int h)
        {
            for (int ty = y; ty < y + h; ty += Tile)
                for (int tx = x; tx < x + w; tx += Tile)
                {
                    int cw = Math.Min(Tile, x + w - tx), ch = Math.Min(Tile, y + h - ty);
                    Graphics.CopyTexture(zeroColour, 0, 0, 0, 0, cw, ch, Colour, 0, 0, tx, ty);
                    Graphics.CopyTexture(zeroDepth, 0, 0, 0, 0, cw, ch, Depth, 0, 0, tx, ty);
                }
        }

        /// <summary>The whole-view textures — for cropped frames GPU-only and cleared, with their
        /// textures of nothing beside them.</summary>
        void Make(int width, int height, bool crop, string name)
        {
            cropped = crop;
            Colour = Texture(width, height, TextureFormat.RGBA32, name + " colour");
            Depth = Texture(width, height, TextureFormat.RFloat, name + " depth");
            Colour.filterMode = FilterMode.Bilinear;
            if (!crop) return;
            zeroColour = Texture(Tile, Tile, TextureFormat.RGBA32, name + " nothing colour");
            zeroDepth = Texture(Tile, Tile, TextureFormat.RFloat, name + " nothing depth");
            var zeros = new byte[Tile * Tile * 4]; // RGBA32 and RFloat: 4 bytes a texel both
            unsafe
            {
                fixed (byte* p = zeros)
                    foreach (var t in new[] { zeroColour, zeroDepth })
                    {
                        t.LoadRawTextureData((IntPtr)p, zeros.Length);
                        t.Apply(false, true);
                    }
            }
            // No CPU copy kept: from now on only ever written on the GPU — cleared whole first.
            Colour.Apply(false, true);
            Depth.Apply(false, true);
            Clear(0, 0, width, height);
        }

        static Texture2D Texture(int width, int height, TextureFormat format, string name) =>
            new Texture2D(width, height, format, false, true)
            {
                name = "Astra " + name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave,
            };

        static void Destroy(ref Texture2D t)
        {
            if (t != null) UnityEngine.Object.Destroy(t);
            t = null;
        }

        public void Release()
        {
            Destroy(ref Colour);
            Destroy(ref Depth);
            Destroy(ref partColour);
            Destroy(ref partDepth);
            Destroy(ref zeroColour);
            Destroy(ref zeroDepth);
            lastW = lastH = 0;
            Empty = false;
        }
    }
}
