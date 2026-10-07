// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Astra.Bridge;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// Which VIEW each extra camera (a mirror, a video camera) is: 1..<see cref="Count"/>, where the
    /// count is what the engine draws and its ring holds beside the main view (0 for an engine from
    /// before views — it would take every camera for the main one). A view is held while its camera
    /// lives: a destroyed camera's view is free at once, and when every view is held, the one whose
    /// camera rendered longest ago (over a second) is taken over. A camera that finds none gets 0 —
    /// the main picture, reprojected.
    /// </summary>
    sealed class ViewTable
    {
        const float Idle = 1f;

        readonly Camera[] cams = new Camera[Messages.MaxView + 1];
        readonly int[] ids = new int[Messages.MaxView + 1];
        readonly float[] used = new float[Messages.MaxView + 1];
        readonly Dictionary<int, int> byCamera = new Dictionary<int, int>();
        int count;

        /// <summary>How many views may be handed out; setting it frees them all.</summary>
        public int Count
        {
            get => count;
            set
            {
                Clear();
                count = Mathf.Clamp(value, 0, Messages.MaxView);
            }
        }

        /// <summary>The view <paramref name="cam"/> holds, 0 if none (and it counts as rendering now).</summary>
        public int Of(Camera cam)
        {
            if (cam == null || !byCamera.TryGetValue(cam.GetInstanceID(), out int v)) return 0;
            used[v] = Time.unscaledTime;
            return v;
        }

        /// <summary>Give <paramref name="cam"/> a view: its own if it holds one, else a free one or the
        /// longest idle; 0 when there is none. <paramref name="taken"/> is a view it was given just now —
        /// whatever was shown in it belongs to another camera and must be forgotten.</summary>
        public int Assign(Camera cam, out int taken)
        {
            taken = 0;
            int held = Of(cam);
            if (held != 0) return held;
            int pick = 0;
            for (int v = 1; v <= count; v++)
            {
                if (cams[v] == null) // free, or its camera was destroyed
                {
                    pick = v;
                    break;
                }
                if (Time.unscaledTime - used[v] > Idle && (pick == 0 || used[v] < used[pick])) pick = v;
            }
            if (pick == 0) return 0;
            Free(pick);
            cams[pick] = cam;
            ids[pick] = cam.GetInstanceID();
            used[pick] = Time.unscaledTime;
            byCamera[ids[pick]] = pick;
            taken = pick;
            return pick;
        }

        public void Clear()
        {
            for (int v = 1; v < cams.Length; v++) Free(v);
        }

        void Free(int v)
        {
            if (ids[v] != 0) byCamera.Remove(ids[v]);
            cams[v] = null;
            ids[v] = 0;
        }
    }
}
