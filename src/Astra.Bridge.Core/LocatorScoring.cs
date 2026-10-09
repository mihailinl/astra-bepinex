// SPDX-License-Identifier: MIT
namespace Astra.Bridge
{
    /// <summary>
    /// The PURE math behind the Unity foundation's default camera and player locators
    /// (<c>Astra.Unity.DefaultCamera</c> / <c>DefaultPlayer</c>, the bare-foundation fallback used
    /// when a game has no Astra integration at all): no UnityEngine type crosses this boundary, so
    /// the tie-breaks are unit-tested here without a live Unity scene.
    /// </summary>
    public static class LocatorScoring
    {
        /// <summary>
        /// Does a camera scored (<paramref name="rank"/>, <paramref name="depth"/>,
        /// <paramref name="since"/>, <paramref name="area"/>) outrank the best one found so far? The
        /// higher <see cref="CameraRank"/> wins (a camera that draws the WORLD beats a UI or overlay
        /// camera stacked above it, whatever their depths); then the highest depth; a tie goes to
        /// whichever moved more recently (an idle security camera loses to the one the player is
        /// steering); a further tie goes to the larger viewport.
        /// </summary>
        public static bool BetterCamera(int rank, double depth, double since, double area,
            int bestRank, double bestDepth, double bestSince, double bestArea) =>
            rank > bestRank || (rank == bestRank
                && (depth > bestDepth || (depth == bestDepth && (since > bestSince || (since == bestSince && area > bestArea)))));

        /// <summary>
        /// What a camera that renders to the screen can be for her: 2 — it draws the WORLD (a
        /// perspective camera whose culling mask holds more than Unity's UI layer, seen rendering);
        /// 1 — it renders, but draws something else (an orthographic UI or minimap camera, a camera
        /// of the UI layer alone); 0 — not seen rendering lately, or a URP OVERLAY camera (it only adds
        /// to a base camera's picture and has no depth of its own). The default camera locator passes
        /// no overlay here and caps a rendering overlay (<see cref="IsOverlay"/>) at 1 itself: it is
        /// still seen rendering, so it outranks a camera that is not, and its base camera outranks it.
        /// </summary>
        public static int CameraRank(bool rendering, bool overlay, bool orthographic, int cullingMask) =>
            !rendering || overlay ? 0 : !orthographic && (cullingMask & ~UiLayerBit) != 0 ? 2 : 1;

        /// <summary>Unity's built-in "UI" layer (5).</summary>
        public const int UiLayerBit = 1 << 5;

        /// <summary>
        /// Is a camera an OVERLAY, one that only adds to another camera's picture (a HUD, a weapon, a
        /// first-person arm), rather than a camera with a world of its own? <paramref name="knownOverlay"/>:
        /// the pipeline says so (URP's camera type). Otherwise Unity's own stacking, all three at once:
        /// it <paramref name="sharesViewWithLowerDepthCamera"/> (a camera rendered before it draws into
        /// the same target from the same pose); it <paramref name="keepsPictureBeneath"/> (the caller
        /// knows its pipeline's clearing: Built-in keeps it for a camera that clears depth only or
        /// nothing; a URP Base camera only for "nothing"; HDRP never); and it draws FEWER layers than
        /// the widest camera beneath it (<paramref name="cullingMask"/> against
        /// <paramref name="beneathMask"/>, <see cref="DrawsFewerLayers"/>) — a HUD or a weapon draws a
        /// layer or two over a world, while the near half of a near/far split draws the same layers as
        /// its far half and is the world's camera itself. A camera at another pose is never an overlay.
        /// </summary>
        public static bool IsOverlay(bool knownOverlay, bool sharesViewWithLowerDepthCamera, bool keepsPictureBeneath,
            int cullingMask, int beneathMask) =>
            knownOverlay || (sharesViewWithLowerDepthCamera && keepsPictureBeneath && DrawsFewerLayers(cullingMask, beneathMask));

        /// <summary>Does a camera of <paramref name="cullingMask"/> draw fewer layers than one of
        /// <paramref name="beneathMask"/>? Counted, not compared bit by bit: an overlay's layers are
        /// usually ones the world's camera does not draw at all (a HUD layer, a weapon layer).</summary>
        public static bool DrawsFewerLayers(int cullingMask, int beneathMask) => Layers(cullingMask) < Layers(beneathMask);

        /// <summary>How many layers <paramref name="mask"/> holds (its set bits).</summary>
        public static int Layers(int mask)
        {
            uint v = unchecked((uint)mask);
            int n = 0;
            while (v != 0)
            {
                v &= v - 1;
                n++;
            }
            return n;
        }

        /// <summary>
        /// A third-person player candidate's score (LOWER wins): its distance from the camera,
        /// plus <paramref name="maxDist"/> when it is BEHIND the camera — so anything ahead, at any
        /// distance within range, outranks anything behind, and the nearest wins within each half.
        /// </summary>
        public static double CandidateScore(double distance, bool aheadOfCamera, double maxDist) =>
            distance + (aheadOfCamera ? 0 : maxDist);
    }
}
