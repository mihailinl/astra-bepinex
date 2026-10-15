// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Astra.Bridge.Capture
{
    /// <summary>What a <see cref="CaptureRecorder"/> is doing.</summary>
    public enum CaptureState
    {
        /// <summary>Waiting for its parameter to turn true.</summary>
        Idle,
        /// <summary>Sampling the game's pose (after the take's lead-in).</summary>
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
    /// capture inbox the engine named — as <c>&lt;file&gt;.part</c>, renamed when whole — and tell the
    /// engine <c>clip_ready</c>. Only while the engine names an inbox (its hello reply, then its
    /// <c>capture</c> messages): the player switched capture on for this integration in Astra; with it
    /// off nothing is recorded, and switching it off stops a take at once. No Unity here: the
    /// foundation steps it every frame with the parameter's value and the engine's inbox.
    /// <para>
    /// A take starts <see cref="LeadIn"/> after the parameter turns true — the game's way INTO the
    /// state (a jump at the wall, arms flung out) is not the state — and is measured in the
    /// character's frame as it was at its first sample (<see cref="RootPose"/>).
    /// </para>
    /// <para>
    /// Bounded every way a game could make it run away: a take ends at <see cref="MaxSeconds"/>, one
    /// shorter than <see cref="MinSeconds"/> is dropped (a grab at a ledge is not a climb), at most
    /// <see cref="MaxTakes"/> takes are offered per game run (each becomes a card the player has to
    /// answer; the engine holds at most 32 waiting), and every recorder of the game offers through ONE
    /// <see cref="OfferGate"/> (two <c>clip_ready</c> at least 600 ms apart). A file the engine refused
    /// (<c>clip_refused</c>) is removed when the refusal arrives; one written but never offered
    /// (capture went off, the inbox changed, the game closed) is removed by whoever comes last, the
    /// writer or the recorder; and a take of this clip left in the inbox longer than
    /// <see cref="CaptureInbox.StaleAfter"/> — a refusal that never reached the game — is removed when
    /// an inbox next appears.
    /// </para>
    /// </summary>
    public sealed class CaptureRecorder : IDisposable
    {
        /// <summary>Samples per second.</summary>
        public const double Rate = 30;

        /// <summary>The longest take, in seconds: about 1.6 MB of a 55-bone rig, far under the engine's
        /// 32 MiB.</summary>
        public const double MaxSeconds = 60;

        /// <summary>The shortest take that is offered, in seconds (after the lead-in).</summary>
        public const double MinSeconds = 1.5;

        /// <summary>How long the parameter must have been true before the first sample, in seconds.</summary>
        public const double LeadIn = 0.3;

        /// <summary>The most takes one recorder offers in a game run.</summary>
        public const int MaxTakes = 3;

        /// <summary>The engine's own cap on a captured file (<c>ops/capture.rs</c>).</summary>
        public const long MaxBytes = 32L << 20;

        readonly Action<string> log;
        readonly Action<Action> background;
        readonly OfferGate gate;
        readonly ConcurrentQueue<string> refused = new ConcurrentQueue<string>();
        readonly Dictionary<string, string> offered = new Dictionary<string, string>(); // file → its full path
        readonly HashSet<string> said = new HashSet<string>();

        string inbox;            // the inbox as this process sees it, now; null = capture off
        bool waitForFalse;       // a take ended while the parameter stayed true: wait for it to drop
        IPoseSampler sampler;
        CapturedClip take;
        Quat[] world, live, local;
        Vec3[] worldAt, at;
        Quat frozen;             // the root's rotation at the take's first sample: the take's frame
        double firstAt, nextSample;
        long lastSecond;
        WriteJob job;            // the take being written / waiting to be offered
        volatile bool disposed;

        /// <summary>One take on its way into the inbox, shared by the recorder and its writer; every
        /// decision about its files is made under its lock.</summary>
        sealed class WriteJob
        {
            public string File, Path;
            public int Status;        // 0 writing, 1 written, 2 failed, 3 removed (abandoned)
            public bool Abandon;      // never to be offered: whoever comes last removes its files
            public string Error;
        }

        /// <param name="clip">The clip's name (<c>climb</c>, at most <see cref="CaptureInbox.MaxClipName"/>):
        /// the file is <c>&lt;clip&gt;-&lt;unix seconds&gt;.vrma</c>, the engine files it as
        /// <c>captured-&lt;clip&gt;</c>.</param>
        /// <param name="forParam">The parameter it is a move for (<c>climbing</c>).</param>
        /// <param name="log">Where its lines go (the plugin's log; also called from the writer's thread).</param>
        /// <param name="background">Runs the file writing off the game's thread (default: the thread
        /// pool); a test passes one that runs it at once.</param>
        /// <param name="gate">The game's one pace for offers, shared by all its recorders (default: one
        /// of its own).</param>
        /// <exception cref="ArgumentException">A name the engine would refuse.</exception>
        public CaptureRecorder(string clip, string forParam, Action<string> log, Action<Action> background = null, OfferGate gate = null)
        {
            if (!CaptureInbox.ValidClipName(clip))
                throw new ArgumentException($"'{clip}' is not a clip name (1-{CaptureInbox.MaxClipName} of A-Z a-z 0-9 _ . -, not starting with '.')", nameof(clip));
            if (!CaptureInbox.ValidName(forParam)) throw new ArgumentException($"'{forParam}' is not a parameter name the engine takes", nameof(forParam));
            Clip = clip;
            For = forParam;
            this.log = log ?? (_ => { });
            this.background = background ?? (a => ThreadPool.QueueUserWorkItem(_ => a()));
            this.gate = gate ?? new OfferGate();
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
        /// this connection AS THIS PROCESS SEES IT (<see cref="CaptureInbox.Local(string)"/>; null =
        /// capture off, or not connected); <paramref name="want"/> the parameter's value this frame;
        /// <paramref name="find"/> finds the player's rig when a take starts (null from it: none
        /// now); <paramref name="send"/> sends one message to the engine (false: not connected).
        /// </summary>
        public void Step(double now, string inbox, bool want, Func<IPoseSampler> find, Func<string, bool> send)
        {
            if (disposed) return;
            DrainRefusals();
            if (inbox != this.inbox) InboxChanged(inbox);
            if (!want) waitForFalse = false;

            switch (State)
            {
                case CaptureState.Idle:
                    if (want && !waitForFalse && this.inbox != null) Begin(now, find);
                    break;
                case CaptureState.Recording:
                    if (!want)
                    {
                        Finish();
                        break;
                    }
                    if (now >= nextSample) Sample(now);
                    if (State == CaptureState.Recording && take.Duration >= MaxSeconds)
                    {
                        Say($"the take of '{Clip}' reached {MaxSeconds:0} s: it ends here");
                        waitForFalse = true;
                        Finish();
                    }
                    break;
                case CaptureState.Writing:
                    int status;
                    lock (job) status = job.Status;
                    if (status == 2)
                    {
                        log($"could not write the capture '{job.File}': {job.Error}");
                        job = null;
                        Reset();
                        break;
                    }
                    if (status != 1) break;
                    State = CaptureState.Ready;
                    goto case CaptureState.Ready;
                case CaptureState.Ready:
                    if (!gate.Open(now)) break;
                    string json = Messages.ClipReady(job.File, Clip, For);
                    if (json == null)
                    {
                        log($"the capture '{job.File}' cannot be offered: {Messages.Rejection}");
                        Abandon();
                        break;
                    }
                    if (send(json))
                    {
                        gate.Sent(now);
                        offered[job.File] = job.Path;
                        log($"offered '{job.File}' to Astra as the move for '{For}' (answer it in Astra: Parameters › Games)");
                        job = null;
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
                world = new Quat[n];
                live = new Quat[n];
                local = new Quat[n];
                worldAt = new Vec3[n];
                at = new Vec3[n];
            }
            take = new CapturedClip(s.Skeleton);
            nextSample = now + LeadIn;
            State = CaptureState.Recording;
            log($"capturing '{Clip}' while '{For}' is true (take {Takes + 1} of at most {MaxTakes}, {n} bones, from {LeadIn:0.0} s in)");
        }

        void Sample(double now)
        {
            bool ok;
            Vec3 hips = default;
            try
            {
                ok = sampler.Sample(world, worldAt, out var root) && root.IsFinite;
                if (ok)
                {
                    if (take.Count == 0)
                    {
                        frozen = root.Rotation;
                        firstAt = now;
                    }
                    RootPose.ToTakeFrame(frozen, root, world, worldAt, world.Length, live, at);
                    ok = take.Skeleton.Normalize(live, at, local, out hips);
                }
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
            take.Add(now - firstAt, local, hips);
            nextSample += 1 / Rate;
            if (nextSample <= now) nextSample = now + 1 / Rate;
        }

        void Finish()
        {
            var clip = take;
            if (clip == null || clip.Count < 2 || clip.Duration < MinSeconds)
            {
                log($"the take of '{Clip}' lasted {(clip?.Duration ?? 0):0.0} s past its lead-in (the least is {MinSeconds:0.0} s): not kept");
                Reset();
                return;
            }
            double turned = clip.FaceForward();
            if (turned > 1) log($"the take of '{Clip}' faced {turned:0}° off its character's root at its start: turned to face front");
            string name = NewFileName();
            if (name == null)
            {
                Say($"no free file name for '{Clip}' in the inbox");
                Reset();
                return;
            }
            var j = new WriteJob { File = name, Path = Path.Combine(inbox, name) };
            job = j;
            Takes++;
            State = CaptureState.Writing;
            take = null;
            sampler = null;
            string clipName = Clip;
            background(() => Write(j, clip, clipName));
        }

        /// <summary>The writer (off the game's thread): <c>.part</c>, then — unless the take was
        /// abandoned meanwhile — the final name; an abandoned or failed take leaves no file.</summary>
        void Write(WriteJob j, CapturedClip clip, string clipName)
        {
            string part = j.Path + ".part";
            bool created = false;
            try
            {
                byte[] bytes = VrmaWriter.Write(clip, clipName);
                if (bytes.Length > MaxBytes) throw new IOException($"{bytes.Length} bytes is over the engine's {MaxBytes >> 20} MiB");
                using (var f = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                lock (j)
                {
                    if (j.Abandon)
                    {
                        Delete(part);
                        j.Status = 3;
                        return;
                    }
                    File.Move(part, j.Path);
                    j.Status = 1;
                }
                log($"captured '{clipName}': {clip.Duration:0.0} s, {clip.Count} frames, {bytes.Length / 1024} KB → {j.File}");
            }
            catch (Exception e)
            {
                lock (j)
                {
                    if (created) Delete(part);
                    j.Error = e.GetType().Name + ": " + e.Message;
                    j.Status = j.Abandon ? 3 : 2;
                }
            }
        }

        /// <summary>The take on its way is never to be offered: its files go now if they are whole,
        /// else when its writer finishes.</summary>
        void Abandon()
        {
            var j = job;
            job = null;
            if (j != null)
            {
                lock (j)
                {
                    j.Abandon = true;
                    if (j.Status == 1)
                    {
                        if (Delete(j.Path)) log($"the capture '{j.File}' was not offered (the capture inbox changed, or the game let go): removed it");
                        j.Status = 3;
                    }
                }
            }
            Reset();
        }

        /// <summary><c>&lt;clip&gt;-&lt;unix seconds&gt;.vrma</c>, the next second when that one (or
        /// its <c>.part</c>) is taken: never longer than the engine echoes back.</summary>
        string NewFileName()
        {
            long s = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), lastSecond + 1);
            for (int i = 0; i < 100; i++, s++)
            {
                string name = CaptureInbox.FileName(Clip, s);
                if (!CaptureInbox.ValidFileName(name) || name.Length > CaptureInbox.MaxEchoedName) return null;
                string p = Path.Combine(inbox, name);
                if (File.Exists(p) || File.Exists(p + ".part") || offered.ContainsKey(name)) continue;
                lastSecond = s;
                return name;
            }
            return null;
        }

        void InboxChanged(string next)
        {
            string before = inbox;
            inbox = next;
            if (State == CaptureState.Recording)
            {
                log($"the take of '{Clip}' was dropped: the capture inbox " + (next == null ? "went away (capture off, or Astra went away)" : "changed"));
                waitForFalse = true;
                Reset();
            }
            // A take being written, or written and not offered, is never offered into another inbox
            // (or none): its files go now, or when its writer finishes.
            if (job != null && (next == null || !SameDir(Path.GetDirectoryName(job.Path), next))) Abandon();
            if (next != null && before == null)
            {
                string mine = job?.File;
                int gone = CaptureInbox.SweepStale(next, Clip, DateTime.UtcNow, f => f == mine || f == mine + ".part" || offered.ContainsKey(f));
                if (gone > 0) log($"removed {gone} old take(s) of '{Clip}' left in the capture inbox (refused, or never offered)");
            }
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
        }

        /// <summary>Log <paramref name="line"/> once per recorder.</summary>
        void Say(string line)
        {
            if (said.Add(line)) log(line);
        }

        static bool Delete(string p)
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

        /// <summary>The game is closing (or its integration let go): a take written but never offered
        /// is removed, now or by its writer when it finishes.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (job != null) Abandon();
            Reset();
        }
    }
}
