// SPDX-License-Identifier: MIT
using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
#if IL2CPP
using Il2CppInterop.Runtime.InteropTypes.Arrays;
#endif

namespace Astra.Unity
{
    /// <summary>
    /// Every use of <c>SphericalHarmonicsL2</c> — light probes, the scene's ambient probe, HDRP's sky
    /// probe — and nothing else. A game built with code stripping that never touches light probes has
    /// the type REMOVED (MiSide, IL2CPP: its interop has no such type, nor
    /// <c>RenderSettings.ambientProbe</c>), and a method that names a removed type or member fails to
    /// compile the first time it runs: before this class, that stopped the whole plugin at its start.
    /// So nothing outside this class names the type, its methods never inline, and every caller
    /// catches the failure (<see cref="Compat.Stripped"/>) and lights her from what the game still has.
    /// </summary>
    static class Sh
    {
        /// <summary>Light probes interpolated at <paramref name="at"/>, toward each of
        /// <paramref name="directions"/>; null where the scene has none — or where they read EXACTLY
        /// zero: probes never baked, which is no reading (a real dark corner still reads a little).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Color[] Local(Vector3 at, Vector3[] directions)
        {
            var probes = LightmapSettings.lightProbes;
            if (probes == null || probes.count == 0) return null;
            LightProbes.GetInterpolatedProbe(at, null, out var sh);
            return sh == default(SphericalHarmonicsL2) ? null : Evaluate(sh, directions);
        }

        /// <summary>The scene's own ambient probe (<c>RenderSettings.ambientProbe</c>).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Color[] Global(Vector3[] directions) => Evaluate(RenderSettings.ambientProbe, directions);

        /// <summary>HDRP's sky probe through its adapter (<see cref="HdrpProbe"/>); null when unbound or
        /// it answered with nothing.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static Color[] Hdrp(Camera cam, Vector3[] directions)
        {
            var read = HdrpProbe.Read;
            if (read == null) return null;
            var sh = read(cam);
            return sh == default(SphericalHarmonicsL2) ? null : Evaluate(sh, directions);
        }

        /// <summary>Bind the HDRP adapter's <c>Ambient</c> (by reflection).</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void BindHdrp() =>
            HdrpProbe.Read = Adapters.Method<Func<Camera, SphericalHarmonicsL2>>("Astra.Hdrp", "Astra.Unity.Hdrp", "Ambient");

        /// <summary>A probe's light on a surface facing each of <paramref name="directions"/> (what the
        /// game's own shaders get from it).</summary>
        static Color[] Evaluate(SphericalHarmonicsL2 sh, Vector3[] directions)
        {
#if IL2CPP
            var dirs = new Il2CppStructArray<Vector3>(directions.Length);
            var cols = new Il2CppStructArray<Color>(directions.Length);
            for (int i = 0; i < directions.Length; i++) dirs[i] = directions[i];
            sh.Evaluate(dirs, cols);
            var result = new Color[directions.Length];
            for (int i = 0; i < result.Length; i++) result[i] = cols[i];
            return result;
#else
            var cols = new Color[directions.Length];
            sh.Evaluate(directions, cols);
            return cols;
#endif
        }

        /// <summary>Its own class, so <see cref="Sh"/> itself holds no field typed on the probe.</summary>
        static class HdrpProbe
        {
            public static Func<Camera, SphericalHarmonicsL2> Read;
        }
    }
}
