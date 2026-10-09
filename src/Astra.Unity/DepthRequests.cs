// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// The depth textures the foundation asks URP for (<see cref="Compat.RequestDepthTexture"/>). Her
    /// depth test after a camera reads that camera's depth texture, which URP renders only for a camera
    /// that asks or whose pipeline asset says so — and which costs the game a depth copy or a depth
    /// prepass every frame. So a request is HELD only while she is drawn into the camera, and the
    /// camera's own option is put back when she stops being (the link drops, she is hidden, the main
    /// camera changes, the plugin stops): never left on a camera nobody composites her into (M6, as the
    /// Built-in depth bit: <c>Compositor.DetachBuiltIn</c>). An OPTIONAL part: its first failure
    /// switches it off, said once (<see cref="Feature"/>).
    /// </summary>
    sealed class DepthRequests
    {
        struct Held
        {
            public Camera Cam;
            public Compat.DepthSetting Before;
        }

        // Every camera asked while held — a "no" too (no URP, no URP data on it), so it is not asked
        // again every frame — by instance id.
        readonly Dictionary<int, Held> held = new Dictionary<int, Held>();
        readonly HashSet<string> said = new HashSet<string>();
        readonly Feature asking = new Feature("the depth-texture request");
        readonly List<int> ids = new List<int>();
        readonly Action<string> info, warn;
        Action<Camera> ask;

        public DepthRequests(Action<string> info, Action<string> warn)
        {
            this.info = info;
            this.warn = warn;
        }

        /// <summary>Hold <paramref name="cam"/>'s request: asked the first time, nothing after while it
        /// is held (a dictionary look-up: callable every frame).</summary>
        public void Hold(Camera cam)
        {
            if (cam == null || asking.Off || held.ContainsKey(cam.GetInstanceID())) return;
            asking.Run(ask ??= Ask, cam, warn);
        }

        /// <summary>Let go of <paramref name="cam"/>'s request: its option is put back as it was.</summary>
        public void Release(Camera cam)
        {
            if (ReferenceEquals(cam, null)) return;
            int id = cam.GetInstanceID();
            if (!held.TryGetValue(id, out var h)) return;
            held.Remove(id);
            PutBack(h);
        }

        /// <summary>Let go of every request (the link dropped, she is hidden, the plugin stops).</summary>
        public void ReleaseAll()
        {
            if (held.Count == 0) return;
            ids.Clear();
            ids.AddRange(held.Keys);
            foreach (int id in ids)
            {
                var h = held[id];
                held.Remove(id);
                PutBack(h);
            }
        }

        void PutBack(Held h)
        {
            // A camera destroyed since has nothing left to put back (Compat checks).
            string why = Compat.RestoreDepthTexture(h.Cam, h.Before);
            if (why != null && said.Add("depth-restore:" + why))
                warn?.Invoke($"could not put back a camera's own depth-texture option ({why}): it keeps rendering one");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void Ask(Camera cam)
        {
            bool asked = Compat.RequestDepthTexture(cam, out string how, out var before);
            held[cam.GetInstanceID()] = new Held { Cam = cam, Before = asked ? before : default };
            // Logged once per camera and outcome, never per hold: a link that comes and goes asks again.
            if (asked)
            {
                if (said.Add($"depth-ask:{cam.name}:{how}"))
                    info?.Invoke($"asked URP for '{cam.name}''s depth texture ({how})"
                        + (before.Changed ? "; put back when she leaves it" : ""));
            }
            else if (how != null && said.Add($"depth-ask:{cam.name}:{how}"))
                warn?.Invoke($"could not ask URP for '{cam.name}''s depth texture ({how}): "
                    + "she is depth-tested there only if the pipeline renders one anyway");
        }
    }
}
