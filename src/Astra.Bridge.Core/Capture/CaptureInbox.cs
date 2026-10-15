// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Astra.Bridge.Capture
{
    /// <summary>
    /// The capture inbox's rules, shared by every <see cref="CaptureRecorder"/>: the names the engine
    /// takes, the inbox as this process sees it, and the clean-up of a game's own takes left there.
    /// </summary>
    public static class CaptureInbox
    {
        /// <summary>The longest clip name (the contract, both sides): its file name
        /// <c>&lt;clip&gt;-&lt;10-digit seconds&gt;.vrma</c> is then at most 64 characters, all of
        /// which the engine echoes back in a <c>clip_refused</c> warning — so a refusal always names
        /// the file exactly.</summary>
        public const int MaxClipName = 48;

        /// <summary>The longest name the engine echoes back whole in a warning.</summary>
        public const int MaxEchoedName = 64;

        /// <summary>The engine's own cap on a captured file's name.</summary>
        public const int MaxFileName = 128;

        /// <summary>A take of this game's left in the inbox longer than this was refused or never
        /// offered (the engine takes an offered file away at once): <see cref="SweepStale"/>.</summary>
        public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

        /// <summary>A clip name: 1–<see cref="MaxClipName"/> of <c>[A-Za-z0-9_.-]</c>, not starting
        /// with a dot (it starts a file name).</summary>
        public static bool ValidClipName(string s) => ValidName(s) && s.Length <= MaxClipName && s[0] != '.';

        /// <summary>A <c>name</c>/<c>for</c> the engine takes: 1–64 of <c>[A-Za-z0-9_.-]</c>.</summary>
        public static bool ValidName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
            foreach (char c in s)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-'))) return false;
            return true;
        }

        /// <summary>A file name the engine takes: a bare name of <c>[A-Za-z0-9._-]</c>, not starting
        /// with a dot, ending <c>.vrma</c>, at most <see cref="MaxFileName"/> characters.</summary>
        public static bool ValidFileName(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > MaxFileName || s[0] == '.') return false;
            foreach (char c in s)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-'))) return false;
            return s.EndsWith(".vrma", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The file name of a take of <paramref name="clip"/> taken at Unix second
        /// <paramref name="seconds"/>: <c>&lt;clip&gt;-&lt;seconds&gt;.vrma</c>, the seconds written
        /// with ten digits (a second taken already is the next one: the length never grows).</summary>
        public static string FileName(string clip, long seconds) => $"{clip}-{seconds:D10}.vrma";

        /// <summary>
        /// The inbox as THIS process sees it: a game running under Wine/Proton gets a Linux engine's Unix
        /// path, which its .NET reads as a path on the current drive — Wine's <c>Z:</c> drive is the
        /// Unix root (the frame ring is reached the same way). Null for null or empty.
        /// </summary>
        public static string Local(string engineInbox) =>
            Local(engineInbox, Environment.OSVersion.Platform == PlatformID.Win32NT);

        /// <summary><see cref="Local(string)"/> with the platform given.</summary>
        public static string Local(string engineInbox, bool windows)
        {
            if (string.IsNullOrEmpty(engineInbox)) return null;
            if (windows && engineInbox.StartsWith("/", StringComparison.Ordinal)) return "Z:" + engineInbox.Replace('/', '\\');
            return engineInbox;
        }

        /// <summary>
        /// Remove this game's own takes of <paramref name="clip"/> (<c>&lt;clip&gt;-&lt;10 digits&gt;.vrma</c>
        /// and its <c>.part</c>) left in <paramref name="inbox"/> for longer than
        /// <see cref="StaleAfter"/>, except <paramref name="keep"/>: the engine moves an offered file
        /// out at once, so one still there was refused (its refusal never reached the game — it closed,
        /// the link dropped) or never offered. Run when an inbox first appears. Returns how many went.
        /// </summary>
        public static int SweepStale(string inbox, string clip, DateTime nowUtc, Func<string, bool> keep = null)
        {
            if (inbox == null || !Directory.Exists(inbox)) return 0;
            var mine = new Regex("^" + Regex.Escape(clip) + @"-\d{10}\.vrma(\.part)?$", RegexOptions.CultureInvariant);
            int gone = 0;
            string[] files;
            try
            {
                files = Directory.GetFiles(inbox);
            }
            catch (Exception)
            {
                return 0;
            }
            foreach (var path in files)
            {
                string name = Path.GetFileName(path);
                if (!mine.IsMatch(name) || (keep != null && keep(name))) continue;
                try
                {
                    var info = new FileInfo(path);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never through a link
                    if (nowUtc - info.LastWriteTimeUtc < StaleAfter) continue;
                    File.Delete(path);
                    gone++;
                }
                catch (Exception)
                {
                    // Not ours to fight over: the next sweep tries again.
                }
            }
            return gone;
        }
    }

    /// <summary>
    /// ONE pace and ONE set of file names for every recorder of a game: two <c>clip_ready</c> at least
    /// <see cref="Gap"/> apart, whichever recorders they come from (the engine refuses a second within
    /// 500 ms of a connection's last), and each take's second reserved here, so two captures of the
    /// same clip name finishing on one frame never pick the same file (review r2: the second writer's
    /// <c>CreateNew</c> threw and its take was lost). Main thread only.
    /// </summary>
    public sealed class OfferGate
    {
        /// <summary>The least time between two offers, in seconds — over the engine's 500 ms.</summary>
        public const double Gap = 0.6;

        double last = double.NegativeInfinity;
        readonly System.Collections.Generic.Dictionary<string, long> lastSecond = new System.Collections.Generic.Dictionary<string, long>();

        /// <summary>May an offer go now?</summary>
        public bool Open(double now) => now - last >= Gap;

        /// <summary>An offer went at <paramref name="now"/>.</summary>
        public void Sent(double now) => last = now;

        /// <summary>
        /// The second a new take of <paramref name="clip"/> is named after: <paramref name="now"/>, or
        /// the next one this game has not reserved for that clip yet and <paramref name="taken"/> does
        /// not refuse (a file of that name already in the inbox) — reserved here before the writer
        /// creates anything. -1 when 100 seconds in a row are taken.
        /// </summary>
        public long Reserve(string clip, long now, Func<long, bool> taken)
        {
            long s = lastSecond.TryGetValue(clip, out var l) ? Math.Max(now, l + 1) : now;
            for (int i = 0; i < 100; i++, s++)
            {
                if (taken != null && taken(s)) continue;
                lastSecond[clip] = s;
                return s;
            }
            return -1;
        }
    }
}
