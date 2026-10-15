// SPDX-License-Identifier: MIT
namespace Astra.Bridge
{
    /// <summary>
    /// The PURE reading behind the Unity foundation's "her:" line (<c>Astra.Unity.WhereLine</c>): from
    /// what the last 10 s counted, which of the documented diagnoses (CONTRIBUTING.md, "The her: line")
    /// it is. No UnityEngine type crosses this boundary, so the order of the readings is unit-tested.
    /// </summary>
    public static class HerDiagnosis
    {
        /// <summary>
        /// The parenthesis that ends the "her:" line ("" when nothing is wrong).
        /// <para>An EMPTY picture (an engine that crops sends no rectangle when none of her is in view:
        /// she is behind the camera, off screen) is never drawn, by design. So "none is drawn" is the
        /// drawing's fault only when pictures with her in them arrived: blaming the pipeline for
        /// pictures that held nothing sent players and reporters looking in the wrong place (seen in
        /// ULTRAKILL, MiSide and REPO, 2026-10-15, whenever she stood behind the player).</para>
        /// </summary>
        /// <param name="shown">She is shown (not toggled off, the link is up).</param>
        /// <param name="pictures">Pictures that arrived in the period.</param>
        /// <param name="drawn">Draws of a picture with something of her in it.</param>
        /// <param name="empty">Draws skipped because the picture held nothing of her.</param>
        /// <param name="cover">Her share of the newest picture (0 = none of her; negative = not measured yet).</param>
        public static string Why(bool shown, int pictures, int drawn, int empty, float cover)
        {
            if (!shown) return " (hidden: toggled off, or Astra not connected)";
            if (pictures == 0) return " (no picture from Astra)";
            if (drawn == 0 && empty > 0) return " (Astra's picture is empty)";
            if (drawn == 0) return " (pictures arrive, none is drawn)";
            if (cover == 0) return " (Astra's picture is empty)";
            return "";
        }

        /// <summary>Whether the period reads as "her picture is empty" — part of the line's state, so a
        /// change to or from it is said.</summary>
        public static bool IsEmpty(int drawn, int empty, float cover) => cover == 0 || (drawn == 0 && empty > 0);
    }
}
