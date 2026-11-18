// SPDX-License-Identifier: MIT
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>The foundation's state an integration may read through <see cref="Sdk.AstraSdk"/>.</summary>
    static class Runtime
    {
        /// <summary>Astra's engine answered and the frame ring is mapped.</summary>
        public static bool Connected { get; internal set; }

        /// <summary>She was drawn in the last frame: connected, composited into the main camera, placed.</summary>
        public static bool Drawn { get; internal set; }

        /// <summary>Where one of her bones is in the main camera's picture now, in the game's world
        /// (<see cref="Sdk.GameIntegration.TryBone"/>); null while no driver runs.</summary>
        public static BoneLookup Bone { get; internal set; }
    }

    /// <summary>The pose of a named bone in the picture of her drawn now, or false.</summary>
    delegate bool BoneLookup(string bone, out Vector3 position, out Quaternion rotation);
}
