// SPDX-License-Identifier: MIT
namespace Astra.Unity
{
    /// <summary>The foundation's state an integration may read through <see cref="Sdk.AstraSdk"/>.</summary>
    static class Runtime
    {
        /// <summary>Astra's engine answered and the frame ring is mapped.</summary>
        public static bool Connected { get; internal set; }
    }
}
