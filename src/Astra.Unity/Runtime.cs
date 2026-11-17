// SPDX-License-Identifier: MIT
namespace Astra.Unity
{
    /// <summary>The foundation's state an integration may read through <see cref="Sdk.AstraSdk"/>.</summary>
    static class Runtime
    {
        /// <summary>Astra's engine answered and the frame ring is mapped.</summary>
        public static bool Connected { get; internal set; }

        /// <summary>She was drawn in the last frame: connected, composited into the main camera, placed.</summary>
        public static bool Drawn { get; internal set; }
    }
}
