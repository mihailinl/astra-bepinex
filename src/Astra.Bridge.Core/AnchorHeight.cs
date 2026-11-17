// SPDX-License-Identifier: MIT
using System;

namespace Astra.Bridge
{
    /// <summary>
    /// Where on her body the point an <c>avatar</c> message names sits (<see cref="Placement.Anchor"/>),
    /// as a share of her height from her feet: the foundation puts the middle of her body there, and her
    /// picture is looked up through the plane at that middle (<see cref="Reprojection"/>). A brain that
    /// seats her by her hips (on a ship's deck) gives her hips' position, not her feet's; taken as feet,
    /// the middle would sit up at her head.
    /// <para>The shares are an ordinary humanoid's (VRM rest pose, 1.6 m): the middle is wanted to a few
    /// centimetres, not her own model's bones.</para>
    /// </summary>
    public static class AnchorHeight
    {
        /// <summary>The share of her height (0 = feet, 1 = the top of her head) at which
        /// <paramref name="anchor"/> sits: a VRM humanoid bone (<c>"hips"</c>, <c>"leftFoot"</c>…) or
        /// <c>"feet"</c>; null or empty means feet. A bone it does not know (an arm's, a typo) is taken as
        /// her middle (0.5), so a wrong guess is never more than half her height off.</summary>
        public static double Of(string anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return 0;
            string a = anchor.Trim();
            // leftUpperLeg and rightUpperLeg sit at the same height.
            if (a.StartsWith("left", StringComparison.OrdinalIgnoreCase)) a = a.Substring(4);
            else if (a.StartsWith("right", StringComparison.OrdinalIgnoreCase)) a = a.Substring(5);
            switch (a.ToLowerInvariant())
            {
                case "feet": return 0;
                case "toes": return 0.02;
                case "foot": return 0.05;
                case "lowerleg": return 0.28;
                case "upperleg": return 0.5;
                case "hips": return 0.53;
                case "spine": return 0.59;
                case "chest": return 0.67;
                case "upperchest": return 0.73;
                case "neck": return 0.84;
                case "head": return 0.91;
                // An arm's bones move too far with the pose to name a height (hands hang or are raised).
                default: return 0.5;
            }
        }
    }
}
