// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Astra.Bridge;
using Astra.Bridge.Capture;
using Astra.Sdk;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The driver's part in CAPTURING the game's own animation (<see cref="GameIntegration.CaptureWhile"/>):
    /// one <see cref="CaptureRecorder"/> per capture the integration in charge registered, stepped
    /// every frame with its parameter's value from the frame the integration just set, and the
    /// engine's capture inbox (<see cref="BridgeLink.CaptureInbox"/>) — present only while the player
    /// has capture switched on for this integration in Astra. Every recorder offers through ONE
    /// <see cref="OfferGate"/>. Optional: a failure switches capturing off and nothing else.
    /// </summary>
    public sealed partial class Driver
    {
        /// <summary>Capturing the game's animation: off by its first failure, never her.</summary>
        readonly Feature capturing = new Feature("capturing the game's animation");
        readonly List<Capturer> capturers = new List<Capturer>();
        /// <summary>The game's one pace for offers, shared by every recorder.</summary>
        readonly OfferGate offers = new OfferGate();
        /// <summary>The integration whose captures <see cref="capturers"/> are.</summary>
        GameIntegration capturersFor;
        /// <summary>The files the engine refused (<c>clip_refused</c>), from the link's thread.</summary>
        readonly ConcurrentQueue<string> refusedClips = new ConcurrentQueue<string>();
        Action<bool> stepCapture;
        Func<string, bool> sendToLink;
        /// <summary>The last inbox the engine named, and whether this game can reach it — asked once
        /// per inbox, not every frame.</summary>
        string inboxSeen, inboxReachable;
        /// <summary>The link session the capture switch's state was last said for.</summary>
        int captureSaidFor = -1;
        // ASTRA_CAPTURE_TEST (seconds): record that long of the player once capture is on, whatever the
        // parameter says — a test aid for a state the tester cannot reach (a climb, headless).
        bool captureTestRead;
        float captureTest, captureTestFrom = -1, captureTestUntil = -1;

        sealed class Capturer
        {
            public CaptureRequest Request;
            public CaptureRecorder Recorder;
            public PoseRig Rig;
            /// <summary>The Animator a rig could not be read from (instance id), and why: not tried
            /// again until the player's Animator is another one.</summary>
            public int FailedFor;
            public string FailedWhy;
            public Func<IPoseSampler> Find;
        }

        /// <summary>The link's <see cref="BridgeLink.Warned"/> (its own thread).</summary>
        void CaptureWarned(string code, string name, string why)
        {
            if (code == "clip_refused" && !string.IsNullOrEmpty(name)) refusedClips.Enqueue(name);
        }

        /// <summary>Every frame, after the integration set this frame's parameters (<paramref name="ran"/>:
        /// it did — the engine is connected and the frame ran).</summary>
        void StepCapture(bool ran)
        {
            if (capturing.Off || (integration == null && capturers.Count == 0)) return;
            capturing.Run(stepCapture ??= StepCaptureNow, ran, warn, StopCapture);
        }

        void StepCaptureNow(bool ran)
        {
            if (!ReferenceEquals(integration, capturersFor))
            {
                StopCapture();
                capturersFor = integration;
                if (integration != null)
                {
                    foreach (var req in integration.Captures)
                    {
                        var c = new Capturer
                        {
                            Request = req,
                            Recorder = new CaptureRecorder(req.Clip, req.Param, m => log.LogInfo("capture: " + m), gate: offers),
                        };
                        c.Find = () => FindRig(c);
                        capturers.Add(c);
                        log.LogInfo($"capture: '{req.Clip}' while '{req.Param}' is true — only once the player switches capture on for '{integration.Id}' in Astra");
                    }
                }
            }
            if (capturers.Count == 0) return;

            bool connected = link != null && link.Ready;
            if (connected && link.Session != captureSaidFor)
            {
                captureSaidFor = link.Session;
                if (link.CaptureInbox == null)
                    log.LogInfo(link.CaptureLive
                        ? "capture: off for this game in Astra; switching it on there starts it at once"
                        : "capture: off for this game in Astra; switching it on there takes effect from the game's next start (this Astra does not tell a running game)");
            }
            string inbox = ran && connected && link.Supports("capture") ? Reachable(CaptureInbox.Local(link.CaptureInbox)) : null;
            while (refusedClips.TryDequeue(out var file))
                foreach (var c in capturers) c.Recorder.Refused(file);
            sendToLink ??= json => link != null && link.Send(json);
            double now = Time.realtimeSinceStartup;
            for (int i = 0; i < capturers.Count; i++)
            {
                var c = capturers[i];
                bool want = ran && frame.Params.TryGetFlag(c.Request.Param, out bool on) && on;
                if (i == 0 && Testing(now, inbox, c)) want = true;
                c.Recorder.Step(now, inbox, want, c.Find, sendToLink);
            }
            if (captureTest > 0) ProbeOrder(capturers[0]);
        }

        /// <summary><paramref name="inbox"/> when this game can reach it (a folder), else null —
        /// asked once per inbox the engine names.</summary>
        string Reachable(string inbox)
        {
            if (inbox == null) return null;
            if (inbox != inboxSeen)
            {
                inboxSeen = inbox;
                inboxReachable = Directory.Exists(inbox) ? inbox : null;
                if (inboxReachable == null) log.LogWarning($"capture: Astra's capture inbox '{inbox}' is not a folder this game can reach: nothing is captured");
            }
            return inboxReachable;
        }

        /// <summary>The player's rig for <paramref name="c"/>, read once per Animator (and again when
        /// that one goes); null when there is none. An Animator whose rig cannot be read is not read
        /// again (the reason is said once).</summary>
        IPoseSampler FindRig(Capturer c)
        {
            var animator = c.Request.Player();
            if (animator == null) return null;
            if (c.Rig != null && c.Rig.Alive(animator)) return c.Rig;
            int id = animator.GetInstanceID();
            if (c.FailedWhy != null && c.FailedFor == id) return null;
            c.Rig = PoseRig.Find(animator, c.Request.Bones, out string why, out string report, out var warnings);
            if (c.Rig == null)
            {
                c.FailedFor = id;
                c.FailedWhy = why;
                log.LogWarning($"capture: cannot capture '{c.Request.Clip}': {why}");
                return null;
            }
            c.FailedWhy = null;
            log.LogInfo("capture: " + report);
            foreach (var w in warnings) log.LogWarning("capture: " + w);
            if (captureTest > 0) log.LogInfo("capture: " + c.Rig.DescribeNow());
            return c.Rig;
        }

        /// <summary><c>ASTRA_CAPTURE_TEST</c>: true for that many seconds, starting 5 s after capture is
        /// on and the player's Animator is there — once a game run.</summary>
        bool Testing(double now, string inbox, Capturer c)
        {
            if (!captureTestRead)
            {
                captureTestRead = true;
                string s = Environment.GetEnvironmentVariable("ASTRA_CAPTURE_TEST");
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0)
                {
                    captureTest = Mathf.Clamp(v, (float)(CaptureRecorder.MinSeconds + CaptureRecorder.LeadIn) + 0.5f, (float)CaptureRecorder.MaxSeconds - 1);
                    log.LogWarning($"capture: ASTRA_CAPTURE_TEST — '{c.Request.Clip}' records {captureTest:0.#} s of the player once capture is on, whatever '{c.Request.Param}' says (a test aid)");
                }
            }
            if (captureTest <= 0) return false;
            if (captureTestUntil < 0)
            {
                Animator a = null;
                try { a = inbox != null ? c.Request.Player() : null; } catch (Exception) { }
                if (a == null)
                {
                    captureTestFrom = -1;
                    return false;
                }
                if (captureTestFrom < 0) captureTestFrom = (float)now + 5;
                if (now < captureTestFrom) return false;
                captureTestUntil = (float)now + captureTest;
            }
            return now < captureTestUntil;
        }

        /// <summary>Let every recorder go (a take written but never offered is removed).</summary>
        void StopCapture()
        {
            foreach (var c in capturers)
            {
                try { c.Recorder.Dispose(); } catch (Exception) { }
            }
            capturers.Clear();
            capturersFor = null;
            StopProbe();
        }
    }
}
