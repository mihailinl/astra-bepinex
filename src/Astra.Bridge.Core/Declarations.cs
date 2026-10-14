// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Astra.Bridge
{
    /// <summary>The type of a raw parameter a game sends: the two an animation set's
    /// <c>[parameters]</c> know (<c>hello.params</c>, PROTOCOL.md).</summary>
    public enum ParamType
    {
        /// <summary>A number (<c>ParamSet.Set(name, double)</c>).</summary>
        Float,
        /// <summary>True or false (<c>ParamSet.Set(name, bool)</c>).</summary>
        Bool,
    }

    /// <summary>One raw parameter a game sends, as its <c>hello</c> declares it.</summary>
    public readonly struct ParamDecl
    {
        /// <summary>The longest parameter name, in characters.</summary>
        public const int MaxName = 32;

        public readonly string Name;
        public readonly ParamType Type;

        public ParamDecl(string name, ParamType type)
        {
            Name = name;
            Type = type;
        }

        /// <summary>The wire's word for <see cref="Type"/>.</summary>
        public string TypeWord => Type == ParamType.Bool ? "bool" : "float";

        /// <summary>Her ground speed, measured by the foundation from her own motion.</summary>
        public static readonly ParamDecl Speed = new ParamDecl("speed", ParamType.Float);

        /// <summary>She is off the ground, measured by the foundation.</summary>
        public static readonly ParamDecl Airborne = new ParamDecl("airborne", ParamType.Bool);

        /// <summary>Is <paramref name="name"/> a parameter name the engine takes: an ASCII identifier
        /// (a letter or <c>_</c>, then letters, digits or <c>_</c>) of at most <see cref="MaxName"/>
        /// characters — the rule for an animation set's <c>[parameters]</c> keys.</summary>
        public static bool ValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxName) return false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
                if (!(letter || (i > 0 && c >= '0' && c <= '9'))) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// The raw parameters a game DECLARES — what it sends, so Astra can show them to the player before
    /// the game ever ran and offer to make her react to them (<c>hello.params</c>). In order, each name
    /// once, at most <see cref="MaxCount"/>, the foundation's own measured ones first.
    /// </summary>
    public sealed class ParamDeclarations
    {
        /// <summary>The most parameters one hello declares.</summary>
        public const int MaxCount = 64;

        readonly List<ParamDecl> list = new List<ParamDecl>();
        readonly int measured;
        ParamDecl[] snapshot;

        /// <param name="measured">The parameters the foundation itself sends (<see cref="ParamDecl.Speed"/>,
        /// <see cref="ParamDecl.Airborne"/>): declared first, and names nobody else may declare.</param>
        public ParamDeclarations(params ParamDecl[] measured)
        {
            foreach (var p in measured ?? new ParamDecl[0]) list.Add(p);
            this.measured = list.Count;
            snapshot = list.ToArray();
        }

        public int Count => list.Count;

        /// <summary>
        /// Declare a parameter this game sends. Declaring the same name with the same type again does
        /// nothing.
        /// </summary>
        /// <exception cref="ArgumentException">The name is not an identifier of at most
        /// <see cref="ParamDecl.MaxName"/> characters, is one the foundation measures itself, was
        /// declared with the other type, or would be past the <see cref="MaxCount"/>th.</exception>
        public void Declare(string name, ParamType type)
        {
            if (!ParamDecl.ValidName(name))
                throw new ArgumentException($"'{name}' is not a parameter name: a letter or _, then letters, digits or _, at most {ParamDecl.MaxName} characters", nameof(name));
            if (type != ParamType.Float && type != ParamType.Bool)
                throw new ArgumentException($"{type} is not a parameter type", nameof(type));
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Name != name) continue;
                if (i < measured)
                    throw new ArgumentException($"'{name}' is measured by the foundation itself: do not declare or send it", nameof(name));
                if (list[i].Type != type)
                    throw new ArgumentException($"'{name}' is already declared as {list[i].TypeWord}", nameof(name));
                return;
            }
            if (list.Count >= MaxCount)
                throw new ArgumentException($"a game declares at most {MaxCount} parameters", nameof(name));
            list.Add(new ParamDecl(name, type));
            snapshot = list.ToArray();
        }

        /// <summary>The declarations as they stand: a copy, safe to hand to another thread.</summary>
        public ParamDecl[] ToArray() => (ParamDecl[])snapshot.Clone();
    }
}
