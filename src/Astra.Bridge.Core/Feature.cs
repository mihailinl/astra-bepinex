// SPDX-License-Identifier: MIT
using System;
using System.Reflection;

namespace Astra.Bridge
{
    /// <summary>
    /// One OPTIONAL part of the Unity foundation — her light, her shadow, a diagnostic line, a default
    /// locator — that a game's build may lack the means for (an IL2CPP build strips every engine API
    /// the game never uses). Its first failure switches it off for the rest of the session, says so
    /// once with the reason, and undoes what it had set up; the rest of the plugin goes on. Only the
    /// essential chain (the shader, the camera hook, the picture, the composite, the link and the
    /// ring) may stop her. No Unity type crosses this boundary, so the rule is unit-tested here.
    /// </summary>
    public sealed class Feature
    {
        /// <summary>A feature that is on until its first failure.</summary>
        /// <param name="name">What it is, as the warning names it ("her shadow").</param>
        public Feature(string name)
        {
            Name = name;
        }

        /// <summary>What it is, as the warning names it.</summary>
        public string Name { get; }

        /// <summary>A failure switched it off; it stays off for the rest of the session.</summary>
        public bool Off { get; private set; }

        /// <summary>The failure that switched it off, in one line (<see cref="Describe"/>); null while on.</summary>
        public string Why { get; private set; }

        /// <summary>
        /// Run <paramref name="body"/> unless the feature is off. True when it ran to its end. A throw
        /// never leaves this method: it switches the feature off, calls <paramref name="warn"/> once
        /// with "&lt;Name&gt; is off in this game: &lt;Why&gt;", runs <paramref name="undo"/> (whose
        /// own throw is swallowed too: off is off) and answers false — as does every later call,
        /// without running the body again.
        /// </summary>
        public bool Run(Action body, Action<string> warn, Action undo = null)
        {
            if (Off) return false;
            try
            {
                body();
                return true;
            }
            catch (Exception e)
            {
                SwitchOff(e, warn, undo);
                return false;
            }
        }

        /// <summary>
        /// <see cref="Run(Action, Action{string}, Action)"/> for a body that takes one argument: a
        /// caller that runs a part EVERY FRAME keeps <paramref name="body"/> in a field and passes what
        /// changes as <paramref name="arg"/>, so no closure is made per call.
        /// </summary>
        public bool Run<T>(Action<T> body, T arg, Action<string> warn, Action undo = null)
        {
            if (Off) return false;
            try
            {
                body(arg);
                return true;
            }
            catch (Exception e)
            {
                SwitchOff(e, warn, undo);
                return false;
            }
        }

        void SwitchOff(Exception e, Action<string> warn, Action undo)
        {
            Off = true;
            Why = Describe(e);
            // The warning and the undo are each fenced on their own: a logger that throws must not
            // skip the undo, and neither may carry the failure out to the frame that ran us.
            try { warn?.Invoke($"{Name} is off in this game: {Why}"); }
            catch (Exception) { /* nowhere left to say it */ }
            if (undo != null)
            {
                try { undo(); }
                catch (Exception) { /* already broken: the feature is off either way */ }
            }
        }

        /// <summary>
        /// The failure itself under the wrappers that only say "something failed inside": a type
        /// initializer's (<see cref="TypeInitializationException"/>) and a reflective call's
        /// (<see cref="TargetInvocationException"/>), however deeply nested.
        /// </summary>
        public static Exception Cause(Exception e)
        {
            while ((e is TypeInitializationException || e is TargetInvocationException) && e.InnerException != null)
                e = e.InnerException;
            return e;
        }

        /// <summary>Why a call failed, in one line for the log: the <see cref="Cause"/>'s type and
        /// message. A MissingMemberException's message names the missing member, and a
        /// TypeLoadException's the type (both are thrown as the CALLER compiles, so the stack would name
        /// only the caller); any other failure is thrown by the member itself — "Method unstripping
        /// failed" names nothing — so the method that threw is added.</summary>
        public static string Describe(Exception e)
        {
            e = Cause(e);
            string why = $"{e.GetType().Name}: {e.Message}";
            if (e is MissingMemberException || e is TypeLoadException) return why;
            try
            {
                MethodBase at = e.TargetSite;
                if (at != null) why += $" (in {at.DeclaringType?.Name}.{at.Name})";
            }
            catch (Exception) { /* no stack to read: the message alone */ }
            return why;
        }
    }
}
