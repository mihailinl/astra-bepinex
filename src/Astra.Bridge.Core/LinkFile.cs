// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Astra.Bridge
{
    /// <summary>
    /// The Astra Link file (AstraPlugins <c>spec/games/link-v1.md</c> §2): on every Play, Astra's
    /// launcher writes <c>astra-link.json</c> into the game's profile (and names it in
    /// <c>ASTRA_LINK_FILE</c> when Astra starts the game itself). It says where the daemon's Link socket
    /// listens and holds a one-time token.
    /// <para>No file means no Link at all — an older Astra, a game not from the signed catalogue, or a
    /// game started outside Astra — and the integration then runs presentation-only, silently. A file
    /// this reads but does not trust (another host, another integration, expired) is the same.</para>
    /// </summary>
    public sealed class LinkFile
    {
        /// <summary>The file's name in the profile (a reserved profile file name).</summary>
        public const string FileName = "astra-link.json";

        /// <summary>The variable naming the file when Astra starts the game itself (under Proton, in its
        /// <c>Z:\</c> form, which the game opens as it is).</summary>
        public const string EnvVar = "ASTRA_LINK_FILE";

        /// <summary>The only <c>schema</c> this reads.</summary>
        public const int Schema = 1;

        /// <summary>A link file is a few hundred bytes: anything larger is not one.</summary>
        public const int MaxBytes = 4096;

        /// <summary>Where it was read from.</summary>
        public string Path { get; private set; }

        /// <summary>Always <c>127.0.0.1</c>: the daemon listens nowhere else, and a file naming another
        /// host is refused.</summary>
        public string Host { get; private set; }

        public int Port { get; private set; }

        /// <summary>The socket's path (<c>/link</c>).</summary>
        public string UrlPath { get; private set; }

        /// <summary>The one-time token the first <c>hello</c> spends.</summary>
        public string Token { get; private set; }

        /// <summary>The integration the file was written for.</summary>
        public string Integration { get; private set; }

        /// <summary>The daemon's key for the game (Steam digits, <c>local-…</c>, <c>epic-…</c>), or null.</summary>
        public string Game { get; private set; }

        public long IssuedUnixMs { get; private set; }

        /// <summary>When an unconnected token stops being taken.</summary>
        public long ExpiresUnixMs { get; private set; }

        /// <summary>
        /// Where the link file is: <see cref="EnvVar"/> when it is set and names a file, else
        /// <see cref="FileName"/> in <paramref name="profileDir"/> when it exists; null when neither does.
        /// <paramref name="env"/> reads a variable (the process's own when null).
        /// </summary>
        public static string Locate(string profileDir, Func<string, string> env = null)
        {
            env = env ?? Environment.GetEnvironmentVariable;
            try
            {
                string named = env(EnvVar);
                if (!string.IsNullOrEmpty(named) && File.Exists(named)) return named;
                if (string.IsNullOrEmpty(profileDir)) return null;
                string inProfile = System.IO.Path.Combine(profileDir, FileName);
                return File.Exists(inProfile) ? inProfile : null;
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// Read the file at <paramref name="path"/> and check it for <paramref name="integration"/> at
        /// <paramref name="nowUnixMs"/>. Null, with <paramref name="why"/> as a line for the log, when it
        /// cannot be read or must not be used.
        /// </summary>
        public static LinkFile Read(string path, string integration, long nowUnixMs, out string why)
        {
            string text;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxBytes)
                {
                    why = $"the link file '{path}' is {info.Length} bytes, more than a link file ever is";
                    return null;
                }
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is ArgumentException || e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is System.Security.SecurityException)
            {
                why = $"the link file '{path}' cannot be read ({e.Message})";
                return null;
            }
            return Parse(text, path, integration, nowUnixMs, out why);
        }

        /// <summary><see cref="Read"/>, for text already read (<paramref name="path"/> only names it).</summary>
        public static LinkFile Parse(string text, string path, string integration, long nowUnixMs, out string why)
        {
            Dictionary<string, object> m;
            try
            {
                m = JsonReader.ReadObject(text);
            }
            catch (FormatException e)
            {
                why = $"the link file '{path}' is not JSON ({e.Message})";
                return null;
            }
            if (!(m.TryGetValue("schema", out var schema) && schema is double s && s == Schema))
            {
                why = $"the link file '{path}' has schema {Show(schema)}; this foundation reads schema {Schema}";
                return null;
            }
            var f = new LinkFile { Path = path };
            if (!ParseUrl(m.TryGetValue("url", out var url) ? url as string : null, f, out why))
            {
                why = $"the link file '{path}': {why}";
                return null;
            }
            f.Token = m.TryGetValue("token", out var token) ? token as string : null;
            if (!Printable(f.Token, 512))
            {
                why = $"the link file '{path}' has no usable token";
                return null;
            }
            f.Integration = m.TryGetValue("integration", out var id) ? id as string : null;
            if (string.IsNullOrEmpty(f.Integration) || (integration != null && f.Integration != integration))
            {
                why = $"the link file '{path}' is for the integration {Show(f.Integration)}, not '{integration}'";
                return null;
            }
            f.Game = m.TryGetValue("game", out var game) ? game as string : null;
            f.IssuedUnixMs = m.TryGetValue("issued_unix_ms", out var issued) && issued is double i ? (long)i : 0;
            if (!(m.TryGetValue("expires_unix_ms", out var expires) && expires is double x && x > 0))
            {
                why = $"the link file '{path}' does not say when it expires";
                return null;
            }
            f.ExpiresUnixMs = (long)x;
            if (nowUnixMs >= f.ExpiresUnixMs)
            {
                why = $"the link file '{path}' expired {(nowUnixMs - f.ExpiresUnixMs) / 1000} s ago (left from an earlier Play?)";
                return null;
            }
            why = null;
            return f;
        }

        /// <summary><c>ws://127.0.0.1:&lt;port&gt;/&lt;path&gt;</c> and nothing else: the path goes into the
        /// request line as it is, so it may hold no space, no line break, no query.</summary>
        static bool ParseUrl(string url, LinkFile f, out string why)
        {
            const string prefix = "ws://127.0.0.1:";
            if (url == null || !url.StartsWith(prefix, StringComparison.Ordinal))
            {
                why = $"the url {Show(url)} is not ws://127.0.0.1:<port>/<path>";
                return false;
            }
            int slash = url.IndexOf('/', prefix.Length);
            string port = slash < 0 ? url.Substring(prefix.Length) : url.Substring(prefix.Length, slash - prefix.Length);
            if (port.Length == 0 || port.Length > 5 || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p < 1 || p > 65535)
            {
                why = $"the url {Show(url)} has no port 1–65535";
                return false;
            }
            string rest = slash < 0 ? "/" : url.Substring(slash);
            foreach (char c in rest)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '/' || c == '-' || c == '_' || c == '.' || c == '~'))
                {
                    why = $"the url {Show(url)} has a path this foundation does not send";
                    return false;
                }
            f.Host = "127.0.0.1";
            f.Port = p;
            f.UrlPath = rest;
            why = null;
            return true;
        }

        static bool Printable(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length > max) return false;
            foreach (char c in s)
                if (c < 0x21 || c > 0x7E) return false;
            return true;
        }

        static string Show(object v) => v == null ? "(none)" : v is string s ? $"'{(s.Length > 80 ? s.Substring(0, 80) + "…" : s)}'" : Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}
