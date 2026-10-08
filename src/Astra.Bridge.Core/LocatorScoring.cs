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
        /// Does a camera scored (<paramref name="depth"/>, <paramref name="since"/>,
        /// <paramref name="area"/>) outrank the best one found so far? Highest depth wins; a tie
        /// goes to whichever moved more recently (an idle security camera loses to the one the
        /// player is steering); a further tie goes to the larger viewport.
        /// </summary>
        public static bool BetterCamera(double depth, double since, double area,
            double bestDepth, double bestSince, double bestArea) =>
            depth > bestDepth || (depth == bestDepth && (since > bestSince || (since == bestSince && area > bestArea)));

        /// <summary>
        /// A third-person player candidate's score (LOWER wins): its distance from the camera,
        /// plus <paramref name="maxDist"/> when it is BEHIND the camera — so anything ahead, at any
        /// distance within range, outranks anything behind, and the nearest wins within each half.
        /// </summary>
        public static double CandidateScore(double distance, bool aheadOfCamera, double maxDist) =>
            distance + (aheadOfCamera ? 0 : maxDist);
    }
}
