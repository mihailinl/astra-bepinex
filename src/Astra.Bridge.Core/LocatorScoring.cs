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
        /// to a base camera's picture and has no depth of its own).
        /// </summary>
        public static int CameraRank(bool rendering, bool overlay, bool orthographic, int cullingMask) =>
            !rendering || overlay ? 0 : !orthographic && (cullingMask & ~UiLayerBit) != 0 ? 2 : 1;

        /// <summary>Unity's built-in "UI" layer (5).</summary>
        public const int UiLayerBit = 1 << 5;

        /// <summary>
        /// A third-person player candidate's score (LOWER wins): its distance from the camera,
        /// plus <paramref name="maxDist"/> when it is BEHIND the camera — so anything ahead, at any
        /// distance within range, outranks anything behind, and the nearest wins within each half.
        /// </summary>
        public static double CandidateScore(double distance, bool aheadOfCamera, double maxDist) =>
            distance + (aheadOfCamera ? 0 : maxDist);
    }
}
