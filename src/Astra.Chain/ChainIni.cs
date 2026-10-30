// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;

namespace Astra.Chain
{
    /// <summary>The `chain.ini` the launcher writes beside this DLL at every Play "with mods"
    /// (design §4.3): one section, three keys. No TOML/INI library — this assembly carries nothing
    /// beyond mscorlib, so the parser is a few lines of plain text handling, forgiving of anything
    /// it does not need (a whole-line `;`/`#` comment, any section order, unknown keys ignored). A
    /// `;`/`#` is a comment only as the FIRST character of a trimmed line — never stripped out of
    /// a value, so a mod source's profile name such as "My #1" round-trips whole.</summary>
    internal sealed class ChainIni
    {
        public string Target;
        public string Source;
        public string Profile;

        /// <summary>Throws on anything that leaves <see cref="Target"/> unset: a missing file, a
        /// missing `[Chain]` section, a missing `target` key. The caller treats that as "fall back
        /// to Astra-only" (design §4.3's Fallback). Messages never say "chain.ini:" themselves —
        /// the one caller that reports them (<c>ChainCore.Describe</c>) adds that prefix once.</summary>
        public static ChainIni Read(string path)
        {
            var result = new ChainIni();
            string section = null;
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string text = line.Trim();
                    if (text.Length == 0 || text[0] == ';' || text[0] == '#') continue;

                    if (text[0] == '[' && text[text.Length - 1] == ']')
                    {
                        section = text.Substring(1, text.Length - 2).Trim();
                        continue;
                    }
                    if (section == null || !string.Equals(section, "Chain", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int eq = text.IndexOf('=');
                    if (eq < 0) continue;
                    string key = text.Substring(0, eq).Trim();
                    string value = text.Substring(eq + 1).Trim();
                    if (string.Equals(key, "target", StringComparison.OrdinalIgnoreCase)) result.Target = value;
                    else if (string.Equals(key, "source", StringComparison.OrdinalIgnoreCase)) result.Source = value;
                    else if (string.Equals(key, "profile", StringComparison.OrdinalIgnoreCase)) result.Profile = value;
                }
            }
            if (string.IsNullOrEmpty(result.Target))
                throw new InvalidDataException("[Chain] target is missing");
            return result;
        }
    }
}
