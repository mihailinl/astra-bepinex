// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Text;

namespace Astra.Bridge
{
    /// <summary>
    /// The engine's <c>warn</c> messages — what went wrong with something this game sent (a parameter
    /// her animation set does not declare, a state it does not have…) — turned into log lines for the
    /// player's console: each distinct <c>code</c> + <c>name</c> ONCE for the life of the link, and at
    /// most <see cref="Max"/> in all, then one line that says the rest are not shown. A game that sends
    /// an undeclared parameter every frame is told once, through reconnects too.
    /// </summary>
    public sealed class WarnLog
    {
        /// <summary>The most distinct warnings logged.</summary>
        public const int Max = 32;

        readonly HashSet<string> said = new HashSet<string>();
        bool full;

        /// <summary>The line to log for one <c>warn</c>, or null when it was logged already (or the
        /// budget is spent and that was said). The engine's words are a game's own text echoed back:
        /// cut and made printable here too.</summary>
        public string Admit(string code, string name, string why)
        {
            code = Printable(code, 32);
            name = Printable(name, 64);
            why = Printable(why, 200);
            if (code.Length == 0) code = "unknown";
            if (said.Contains(code + "\n" + name)) return null;
            if (said.Count >= Max)
            {
                if (full) return null;
                full = true;
                return $"Astra warns of more than {Max} things this game sent; the rest are not shown";
            }
            said.Add(code + "\n" + name);
            string line;
            switch (code)
            {
                case "param_undeclared":
                    line = $"Astra warns: her animation set does not declare the parameter '{name}' (kept: it takes effect once a set that declares it is active)";
                    break;
                case "state_unknown":
                    line = $"Astra warns: her animation set has no state '{name}' (that play was ignored)";
                    break;
                case "cue_wrong_pack":
                    line = $"Astra warns: the cue '{name}' is for an animation set she is not wearing (declined)";
                    break;
                case "clip_refused":
                    line = $"Astra warns: the clip '{name}' was refused";
                    break;
                default:
                    line = $"Astra warns ({code}): '{name}'";
                    break;
            }
            return why.Length > 0 ? line + ": " + why : line;
        }

        /// <summary>At most <paramref name="max"/> characters of <paramref name="s"/>, without control
        /// or format characters, trimmed; empty for null.</summary>
        static string Printable(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(System.Math.Min(s.Length, max));
            foreach (char c in s)
            {
                if (sb.Length >= max) break;
                // Control characters, and format ones (bidi overrides) that could disguise a line.
                if (!char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
