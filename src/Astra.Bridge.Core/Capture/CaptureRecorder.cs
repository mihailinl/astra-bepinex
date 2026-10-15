// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Astra.Bridge.Capture
{
    /// <summary>Where a capture reads the game's pose from: a rig the foundation found.</summary>
    public interface IPoseSampler
    {
        /// <summary>The rig's skeleton (built once, from its rest pose).</summary>
        CaptureSkeleton Skeleton { get; }

        /// <summary>This frame's pose: each bone's rotation relative to the character's root (Unity's
        /// axes, <see cref="CaptureSkeleton.Bones"/>' order) and the hips' position relative to it.
        /// False when there is no pose now (the character is gone).</summary>
        bool Sample(Quat[] live, out Vec3 hips);
    }

    /// <summary>What a <see cref="CaptureRecorder"/> is doing.</summary>
    public enum CaptureState
    {
        /// <summary>Waiting for its parameter to turn true.</summary>
        Idle,
        /// <summary>Sampling the game's pose.</summary>
        Recording,
        /// <summary>Writing the take into the inbox, off the game's thread.</summary>
        Writing,
        /// <summary>The take is in the inbox; <c>clip_ready</c> goes out at the next chance.</summary>
        Ready,
    }

    /// <summary>
    /// <b>Capturing the game's own animation</b> (PROTOCOL.md, <c>clip_ready</c>): while ONE raw
    /// parameter of the integration is true (<c>climbing</c>), sample the player's pose at
    /// <see cref="Rate"/> per second; when it turns false, write the take as a <c>.vrma</c> into the
    /// capture inbox the engine's hello reply named — as <c>&lt;file&gt;.part</c>, renamed when whole —
    /// and tell the engine <c>clip_ready</c>. Only while that reply names an inbox: the player switched
    /// capture on for this integration in Astra; with it off nothing is ever recorded. No Unity here:
    /// the foundation steps it every frame with the parameter's value and the engine's inbox.
    /// <para>
    /// Bounded every way a game could make it run away: a take ends at <see cref="MaxSeconds"/>, one
    /// shorter than <see cref="MinSeconds"/> is dropped (a grab at a ledge is not a climb), at most
    /// <see cref="MaxTakes"/> takes are offered per game run (each becomes a card the player has to
    /// answer; the engine holds at most 32 waiting), and a take is recorded only once the one before
    /// was offered, so two <c>clip_ready</c> are at least <see cref="MinSeconds"/> apart — over the
    /// engine's own 500 ms rule. A file the engine refused
    /// (<c>clip_refused</c>) is removed — the engine leaves a refused file to the game — and so is one
    /// that was never offered because capture went off (or the inbox changed) before it could be.
    /// </para>
    /// </summary>
    public sealed class CaptureRecorder : IDisposable
    {
        /// <summary>Samples per second.</summary>
        public const double Rate = 30;

        /// <summary>The longest take, in seconds: about 1.6 MB of a 55-bone rig, far under the engine's
        /// 32 MiB.</summary>
        public const double MaxSeconds = 60;

        /// <summary>The shortest take that is offered, in seconds.</summary>
        public const double MinSeconds = 1.5;

        /// <summary>The most takes one recorder offers in a game run.</summary>
        public const int MaxTakes = 3;

        /// <summary>The engine's own cap on a captured file (<c>ops/capture.rs</c>).</summary>
        public const long MaxBytes = 32L << 20;

        /// <summary>The engine's own cap on a captured file's name.</summary>
        public const int MaxFileName = 128;

        readonly Action<string> log;
        readonly Action<Action> background;
        readonly ConcurrentQueue<string> refused = new ConcurrentQueue<string>();
        readonly Dictionary<string, string> offered = new Dictionary<string, string>(); // file → its full path
        readonly HashSet<string> said = new HashSet<string>();

        string inbox;            // the inbox as this process sees it, this session; null = capture off
        bool waitForFalse;       // a take ended while the parameter stayed true: wait for it to drop
        IPoseSampler sampler;
        CapturedClip take;
        Quat[] live, local;
        double started, nextSample;
        string file, path;       // the take being written / ready: its name, its full path
        volatile int written;    // 0 = still writing, 1 = done, 2 = failed (writeError says why)
        string writeError;
        volatile bool disposed;

        /// <param name="clip">The clip's name (<c>climb</c>): the file is <c>&lt;clip&gt;-&lt;unix
        /// seconds&gt;.vrma</c>, the engine files it as <c>captured-&lt;clip&gt;</c>.</param>
        /// <param name="forParam">The parameter it is a move for (<c>climbing</c>).</param>
        /// <param name="log">Where its lines go (the plugin's log).</param>
        /// <param name="background">Runs the file writing off the game's thread (default: the thread
        /// pool); a test passes one that runs it at once.</param>
        /// <exception cref="ArgumentException">A name the engine would refuse.</exception>
        public CaptureRecorder(string clip, string forParam, Action<string> log, Action<Action> background = null)
        {
            if (!ValidClipName(clip)) throw new ArgumentException($"'{clip}' is not a clip name (1-64 of A-Z a-z 0-9 _ . -, not starting with '.')", nameof(clip));
            if (!ValidName(forParam)) throw new ArgumentException($"'{forParam}' is not a parameter name the engine takes", nameof(forParam));
            Clip = clip;
            For = forParam;
            this.log = log ?? (_ => { });
            this.background = background ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
        }

        public string Clip { get; }
        public string For { get; }
        public CaptureState State { get; private set; }

        /// <summary>Takes offered (or being written) this game run.</summary>
        public int Takes { get; private set; }

        /// <summary>The take being recorded (null when none) — for a test or a diagnostic line.</summary>
        public CapturedClip Take => take;

        /// <summary>
        /// One game frame, on its main thread. <paramref name="inbox"/> is the engine's capture inbox for
        /// this connection AS THIS PROCESS SEES IT (<see cref="LocalInbox(string)"/>; null = capture off, or not
        /// connected); <paramref name="want"/> the parameter's value this frame;
        /// <paramref name="sampler"/> finds the player's rig when a take starts (null from it: none
        /// now); <paramref name="send"/> sends one message to the engine (false: not connected).
        /// </summary>
        public void Step(double now, string inbox, bool want, Func<IPoseSampler> sampler, Func<string, bool> send)
        {
            if (disposed) return;
            DrainRefusals();
            if (inbox != this.inbox) InboxChanged(inbox);
            if (!want) waitForFalse = false;

            switch (State)
            {
                case CaptureState.Idle:
                    if (want && !waitForFalse && this.inbox != null) Begin(now, sampler);
                    break;
                case CaptureState.Recording:
                    if (!want) Finish(now);
                    else
                    {
                        if (now >= nextSample) Sample(now);
                        if (State == CaptureState.Recording && take.Duration >= MaxSeconds)
                        {
                            Say($"the take of '{Clip}' reached {MaxSeconds:0} s: it ends here");
                            waitForFalse = true;
                            Finish(now);
                        }
                    }
                    break;
                case CaptureState.Writing:
                    if (written == 1)
                    {
                        State = CaptureState.Ready;
                        goto case CaptureState.Ready;
                    }
                    if (written == 2)
                    {
                        log($"could not write the capture '{file}': {writeError}");
                        Delete(path + ".part");
                        Reset();
                    }
                    break;
                case CaptureState.Ready:
                    if (this.inbox == null || !SameDir(Path.GetDirectoryName(path), this.inbox))
                    {
                        // Capture went off (or Astra went away, or another inbox came) before it was
                        // offered: the engine will never hear of it, so it does not stay behind.
                        log($"the capture '{file}' was not offered (the capture inbox changed): removed it");
                        Delete(path);
                        Reset();
                        break;
                    }
                    string json = Messages.ClipReady(file, Clip, For);
                    if (json == null)
                    {
                        log($"the capture '{file}' cannot be offered: {Messages.Rejection}");
                        Delete(path);
                        Reset();
                        break;
                    }
                    if (send(json))
                    {
                        offered[file] = path;
                        log($"offered '{file}' to Astra as the move for '{For}' (answer it in Astra: Parameters › Games)");
                        Reset();
                    }
                    break;
            }
        }

        /// <summary>The engine refused <paramref name="file"/> (its <c>clip_refused</c> warning). Any
        /// thread: handled at the next <see cref="Step"/>.</summary>
        public void Refused(string file)
        {
            if (!string.IsNullOrEmpty(file)) refused.Enqueue(file);
        }

        /// <summary>
        /// The inbox as THIS process sees it: a game running under Wine/Proton gets a Linux engine's Unix
        /// path, which its .NET reads as a path on the current drive — Wine's <c>Z:</c> drive is the
        /// Unix root (the frame ring is reached the same way). Null for null.
        /// </summary>
        public static string LocalInbox(string engineInbox) =>
            LocalInbox(engineInbox, Environment.OSVersion.Platform == PlatformID.Win32NT);

        /// <summary><see cref="LocalInbox(string)"/> with the platform given.</summary>
        public static string LocalInbox(string engineInbox, bool windows)
        {
            if (string.IsNullOrEmpty(engineInbox)) return null;
            if (windows && engineInbox.StartsWith("/", StringComparison.Ordinal)) return "Z:" + engineInbox.Replace('/', '\\');
            return engineInbox;
        }

        /// <summary>A clip name the engine takes as <c>name</c>, that also starts a valid file name:
        /// 1–64 of <c>[A-Za-z0-9_.-]</c>, not starting with a dot.</summary>
        public static bool ValidClipName(string s) => ValidName(s) && s[0] != '.';

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

        void Begin(double now, Func<IPoseSampler> find)
        {
            if (Takes >= MaxTakes)
            {
                Say($"{MaxTakes} takes of '{Clip}' were offered this game run: no more until the game restarts");
                waitForFalse = true;
                return;
            }
            IPoseSampler s = null;
            try
            {
                s = find?.Invoke();
            }
            catch (Exception e)
            {
                Say($"no rig to capture '{Clip}' from: {e.GetType().Name}: {e.Message}");
            }
            if (s == null || s.Skeleton == null)
            {
                waitForFalse = true; // asked again when the parameter next turns true
                return;
            }
            sampler = s;
            int n = s.Skeleton.Bones.Length;
            if (live == null || live.Length != n)
            {
                live = new Quat[n];
                local = new Quat[n];
            }
            take = new CapturedClip(s.Skeleton);
            started = now;
            nextSample = now;
            State = CaptureState.Recording;
            log($"capturing '{Clip}' while '{For}' is true (take {Takes + 1} of at most {MaxTakes}, {n} bones)");
            Sample(now);
        }

        void Sample(double now)
        {
            bool ok;
            Vec3 hips = default;
            try
            {
                ok = sampler.Sample(live, out var h) && take.Skeleton.Normalize(live, h, local, out hips);
            }
            catch (Exception e)
            {
                ok = false;
                Say($"capturing '{Clip}' failed: {e.GetType().Name}: {e.Message}");
            }
            if (!ok)
            {
                log($"the take of '{Clip}' lost its rig: dropped");
                waitForFalse = true;
                Reset();
                return;
            }
            take.Add(now - started, local, hips);
            nextSample += 1 / Rate;
            if (nextSample <= now) nextSample = now + 1 / Rate;
        }

        void Finish(double now)
        {
            var clip = take;
            if (clip == null || clip.Count < 2 || clip.Duration < MinSeconds)
            {
                log($"the take of '{Clip}' lasted {(clip?.Duration ?? 0):0.0} s (the least is {MinSeconds:0.0} s): not kept");
                Reset();
                return;
            }
            string name = NewFileName();
            if (name == null)
            {
                Say($"no free file name for '{Clip}' in the inbox");
                Reset();
                return;
            }
            file = name;
            path = Path.Combine(inbox, name);
            Takes++;
            written = 0;
            writeError = null;
            State = CaptureState.Writing;
            take = null;
            string target = path, clipName = Clip;
            background(() => WriteFile(clip, clipName, target));
        }

        void WriteFile(CapturedClip clip, string clipName, string target)
        {
            try
            {
                byte[] bytes = VrmaWriter.Write(clip, clipName);
                if (bytes.Length > MaxBytes) throw new IOException($"{bytes.Length} bytes is over the engine's {MaxBytes >> 20} MiB");
                string part = target + ".part";
                using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                File.Move(part, target);
                if (disposed)
                {
                    Delete(target); // the game closed meanwhile: it will never be offered
                    return;
                }
                log($"captured '{clipName}': {clip.Duration:0.0} s, {clip.Count} frames, {bytes.Length / 1024} KB → {Path.GetFileName(target)}");
                written = 1;
            }
            catch (Exception e)
            {
                writeError = e.GetType().Name + ": " + e.Message;
                written = 2;
            }
        }

        /// <summary><c>&lt;clip&gt;-&lt;unix seconds&gt;.vrma</c>, with <c>-2</c>, <c>-3</c>… when that
        /// one (or its <c>.part</c>) is there already.</summary>
        string NewFileName()
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (int i = 1; i <= 50; i++)
            {
                string name = i == 1 ? $"{Clip}-{ts}.vrma" : $"{Clip}-{ts}-{i}.vrma";
                if (!ValidFileName(name)) return null;
                string p = Path.Combine(inbox, name);
                if (!File.Exists(p) && !File.Exists(p + ".part") && !offered.ContainsKey(name)) return name;
            }
            return null;
        }

        void InboxChanged(string next)
        {
            string before = inbox;
            inbox = next;
            if (next == null && before != null) log($"Astra's capture inbox is gone (capture switched off, or Astra went away): '{Clip}' is not recorded");
            if (next != null && before == null) log($"Astra's capture is on for this game: '{Clip}' is recorded while '{For}' is true");
            if (State == CaptureState.Recording)
            {
                log($"the take of '{Clip}' was dropped: the capture inbox changed");
                waitForFalse = true;
                Reset();
            }
            // A take being written, or written and not yet offered, is checked against the inbox when
            // it would be offered (Ready): never offered into another session's inbox, never left.
        }

        static bool SameDir(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('/', '\\'), Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        void DrainRefusals()
        {
            while (refused.TryDequeue(out var f))
            {
                if (!offered.TryGetValue(f, out var p)) continue;
                offered.Remove(f);
                if (Delete(p)) log($"Astra did not take '{f}': removed it from the inbox");
            }
        }

        void Reset()
        {
            State = CaptureState.Idle;
            take = null;
            sampler = null;
            file = null;
            path = null;
        }

        /// <summary>Log <paramref name="line"/> once per recorder.</summary>
        void Say(string line)
        {
            if (said.Add(line)) log(line);
        }

        bool Delete(string p)
        {
            if (p == null) return false;
            try
            {
                if (!File.Exists(p)) return false;
                File.Delete(p);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The game is closing: a take that was written but never offered is removed (the
        /// engine would never hear of it).</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (State == CaptureState.Ready || (State == CaptureState.Writing && written == 1)) Delete(path);
            State = CaptureState.Idle;
        }
    }
}
