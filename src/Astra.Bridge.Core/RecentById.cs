// SPDX-License-Identifier: MIT
using System;

namespace Astra.Bridge
{
    /// <summary>
    /// What was sent lately, found again by its id: held for an AGE, never a count. The Unity
    /// foundation keeps every camera it sends here (<c>Astra.Unity.CameraFeed</c>): the picture the
    /// engine sends back names the camera it was drawn for, and the compositor needs that camera's pose.
    /// <para>It held the last 64 cameras until 0.8.1. That is a second at 60 FPS, and less than 0.2 s
    /// for a game running uncapped at several hundred: every picture then came back after its camera
    /// had been pushed out, and she was never drawn while the game had the focus (Valheim, Windows,
    /// 2026-11-16). An age means the same thing at any frame rate.</para>
    /// <para>Ids are expected to rise by one per entry (a sequence), so each id has its own slot until
    /// <see cref="Capacity"/> newer ones have been put: a game sending more than that within
    /// <see cref="MaxAge"/> loses its oldest entries early, never a newer one. Not thread-safe.</para>
    /// </summary>
    public sealed class RecentById<T>
    {
        struct Entry
        {
            public long Id;
            public double At;
            public T Value;
        }

        readonly Entry[] ring;

        /// <param name="capacity">How many entries it holds at most (at least 1).</param>
        /// <param name="maxAge">How long an entry is found after it was put, in the caller's time unit
        /// (seconds for the foundation).</param>
        public RecentById(int capacity, double maxAge)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!(maxAge > 0)) throw new ArgumentOutOfRangeException(nameof(maxAge));
            ring = new Entry[capacity];
            MaxAge = maxAge;
        }

        /// <summary>How many entries it holds at most.</summary>
        public int Capacity => ring.Length;

        /// <summary>How long an entry is found after it was put.</summary>
        public double MaxAge { get; }

        /// <summary>Remember <paramref name="value"/> as <paramref name="id"/>, put at time
        /// <paramref name="at"/>. Id 0 is never found.</summary>
        public void Put(long id, double at, T value) => ring[Slot(id)] = new Entry { Id = id, At = at, Value = value };

        /// <summary>The value put as <paramref name="id"/>, while it is still held and no older than
        /// <see cref="MaxAge"/> at time <paramref name="now"/>.</summary>
        public bool Find(long id, double now, out T value)
        {
            var e = ring[Slot(id)];
            if (id != 0 && e.Id == id && now - e.At <= MaxAge)
            {
                value = e.Value;
                return true;
            }
            value = default;
            return false;
        }

        int Slot(long id) => (int)(((id % ring.Length) + ring.Length) % ring.Length);
    }
}
