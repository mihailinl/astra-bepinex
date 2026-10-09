// SPDX-License-Identifier: MIT
using System;
using UnityEngine;

namespace Astra.Unity
{
    /// <summary>
    /// When to draw her into the TEXTURE a main camera renders into (a game's low-resolution screen,
    /// shown by a later camera or a canvas) under a scriptable pipeline: right after that camera —
    /// before anything presents the texture — unless the camera heads a URP camera STACK. There the
    /// Base camera renders into the stack's own intermediate and only the LAST Overlay camera blits it
    /// into the texture, so a draw at the Base camera's end is copied over. The stack is learned from
    /// the camera events themselves, the same in every flavour and URP version: URP renders a stack's
    /// cameras back to back, so an Overlay camera (URP's own camera type) that begins after the main
    /// camera's end, before any other camera, is in its stack; the next camera of another kind, or the
    /// end of the context, ends it. Learned one frame late (that one frame she is copied over), kept
    /// while the main camera stays. Only where the SRP reports the end of its context: without it,
    /// nothing later is sure to come, and she is drawn at the camera's end as before.
    /// </summary>
    sealed class TextureStack
    {
        readonly Action<string> note;
        // The main camera ended into its texture in this context, and no camera of another stack has
        // begun since.
        bool watching;
        // The main camera learned to head a URP stack (a new main camera learns afresh).
        Camera stacked;
        // Her draw into the main camera's texture waits for the end of its stack.
        bool owed;

        public TextureStack(Action<string> note)
        {
            this.note = note;
        }

        /// <summary>Does a camera's begin need <see cref="Begins"/> (the main camera's stack may be on)?</summary>
        public bool Watching => watching;

        /// <summary>
        /// The main camera <paramref name="main"/> ended. <paramref name="intoTexture"/>: it renders into
        /// a texture and the SRP reports its context's end. True: her draw waits for the end of its
        /// stack — the caller keeps its depth now, and draws when <see cref="Begins"/> or
        /// <see cref="Done"/> says so; false: draw now.
        /// </summary>
        public bool Defer(Camera main, bool intoTexture)
        {
            watching = intoTexture;
            owed = intoTexture && main != null && stacked == main;
            return owed;
        }

        /// <summary>A camera begins while <see cref="Watching"/>: an <paramref name="overlay"/> (URP's own
        /// camera type) goes on with the main camera's stack, and the main camera is learned to head one;
        /// any other camera begins another stack, so the main one is done. True when her draw is owed now.</summary>
        public bool Begins(Camera cam, Camera main, bool overlay)
        {
            if (overlay && cam != main)
            {
                if (main != null && stacked != main)
                {
                    stacked = main;
                    note?.Invoke($"'{main.name}' renders into a texture under URP overlay cameras ('{cam.name}'): "
                        + "she is drawn into it when its camera stack is done");
                }
                return false;
            }
            return Done();
        }

        /// <summary>The main camera's stack is done (another stack begins, or the context ends): true when
        /// her draw is owed now — against the depth kept at the main camera's own end.</summary>
        public bool Done()
        {
            watching = false;
            bool due = owed;
            owed = false;
            return due;
        }
    }
}
