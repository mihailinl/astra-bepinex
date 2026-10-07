// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Astra.Bridge
{
    /// <summary>
    /// A compact JSON writer for the bridge's messages. Written by hand on purpose: a game ships its
    /// own JSON library (any version, or none) and a mod must not depend on it. Numbers are ALWAYS
    /// written with the invariant culture — a game running under a comma-decimal locale (ru-RU,
    /// de-DE…) must still send <c>1.5</c>, never <c>1,5</c>.
    /// </summary>
    public sealed class JsonWriter
    {
        readonly StringBuilder sb = new StringBuilder(256);
        // One flag per open object: does its next member need a comma?
        readonly Stack<bool> comma = new Stack<bool>();

        public JsonWriter Open()
        {
            sb.Append('{');
            comma.Push(false);
            return this;
        }

        public JsonWriter Open(string key)
        {
            Key(key);
            return Open();
        }

        public JsonWriter Close()
        {
            sb.Append('}');
            comma.Pop();
            return this;
        }

        public JsonWriter Str(string key, string value)
        {
            Key(key);
            Quote(value);
            return this;
        }

        /// <summary>A finite number. A non-finite one throws: the bridge refuses it anyway, and
        /// writing <c>null</c> instead would MEAN something (a parameter's "back to the default").</summary>
        public JsonWriter Num(string key, double value)
        {
            Key(key);
            Number(value);
            return this;
        }

        public JsonWriter Bool(string key, bool value)
        {
            Key(key);
            sb.Append(value ? "true" : "false");
            return this;
        }

        public JsonWriter Null(string key)
        {
            Key(key);
            sb.Append("null");
            return this;
        }

        public JsonWriter Vec(string key, Vec3 v)
        {
            Key(key);
            sb.Append('[');
            Number(v.X);
            sb.Append(',');
            Number(v.Y);
            sb.Append(',');
            Number(v.Z);
            sb.Append(']');
            return this;
        }

        public JsonWriter Nums(string key, double[] values, int count)
        {
            Key(key);
            sb.Append('[');
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                Number(values[i]);
            }
            sb.Append(']');
            return this;
        }

        /// <summary>An array of objects under <paramref name="key"/>: <see cref="Item"/> opens each one
        /// (<see cref="Close"/> it), <see cref="CloseArray"/> ends the array.</summary>
        public JsonWriter OpenArray(string key)
        {
            Key(key);
            sb.Append('[');
            comma.Push(false);
            return this;
        }

        public JsonWriter Item()
        {
            if (comma.Pop()) sb.Append(',');
            comma.Push(true);
            return Open();
        }

        public JsonWriter CloseArray()
        {
            sb.Append(']');
            comma.Pop();
            return this;
        }

        public override string ToString() => sb.ToString();

        public void Reset()
        {
            sb.Length = 0;
            comma.Clear();
        }

        void Key(string key)
        {
            if (comma.Pop()) sb.Append(',');
            comma.Push(true);
            Quote(key);
            sb.Append(':');
        }

        void Number(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                throw new ArgumentOutOfRangeException(nameof(v), v, "JSON has no non-finite numbers");
            // Integral values are written without a fraction; everything else round-trips ("R").
            if (v == Math.Floor(v) && Math.Abs(v) < 1e15)
                sb.Append(((long)v).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        void Quote(string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// Reads the TOP-LEVEL members of a JSON object — all the engine's replies need. Strings come
    /// back as <see cref="string"/>, numbers as <see cref="double"/>, booleans as <see cref="bool"/>,
    /// <c>null</c> as null; a nested object or array is parsed and skipped (its key is absent).
    /// </summary>
    public static class JsonReader
    {
        /// <exception cref="FormatException">The text is not one JSON object.</exception>
        public static Dictionary<string, object> ReadObject(string json)
        {
            var p = new Parser(json);
            var result = new Dictionary<string, object>();
            p.Ws();
            p.Expect('{');
            p.Ws();
            if (p.Peek() == '}')
            {
                p.Pos++;
            }
            else
            {
                while (true)
                {
                    p.Ws();
                    string key = p.String();
                    p.Ws();
                    p.Expect(':');
                    p.Ws();
                    char c = p.Peek();
                    if (c == '{' || c == '[') p.Skip();
                    else result[key] = p.Scalar();
                    p.Ws();
                    if (p.Peek() == ',') { p.Pos++; continue; }
                    p.Expect('}');
                    break;
                }
            }
            p.Ws();
            if (p.Pos != json.Length) throw new FormatException("trailing characters after the object");
            return result;
        }

        sealed class Parser
        {
            readonly string s;
            public int Pos;

            public Parser(string s) => this.s = s ?? throw new FormatException("no text");

            public char Peek()
            {
                if (Pos >= s.Length) throw new FormatException("unexpected end");
                return s[Pos];
            }

            public void Expect(char c)
            {
                if (Peek() != c) throw new FormatException($"expected '{c}' at {Pos}");
                Pos++;
            }

            public void Ws()
            {
                while (Pos < s.Length && (s[Pos] == ' ' || s[Pos] == '\t' || s[Pos] == '\n' || s[Pos] == '\r')) Pos++;
            }

            public object Scalar()
            {
                char c = Peek();
                if (c == '"') return String();
                if (Word("true")) return true;
                if (Word("false")) return false;
                if (Word("null")) return null;
                int start = Pos;
                while (Pos < s.Length && "+-0123456789.eE".IndexOf(s[Pos]) >= 0) Pos++;
                if (Pos == start) throw new FormatException($"unexpected '{c}' at {Pos}");
                return double.Parse(s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            public string String()
            {
                Expect('"');
                var sb = new StringBuilder();
                while (true)
                {
                    char c = Peek();
                    Pos++;
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    char e = Peek();
                    Pos++;
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > s.Length) throw new FormatException("short \\u escape");
                            sb.Append((char)int.Parse(s.Substring(Pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            Pos += 4;
                            break;
                        default: throw new FormatException($"bad escape '\\{e}'");
                    }
                }
            }

            /// <summary>Skip one value of any kind (objects and arrays nest).</summary>
            public void Skip()
            {
                char c = Peek();
                if (c == '{' || c == '[')
                {
                    char close = c == '{' ? '}' : ']';
                    Pos++;
                    Ws();
                    if (Peek() == close) { Pos++; return; }
                    while (true)
                    {
                        Ws();
                        if (c == '{')
                        {
                            String();
                            Ws();
                            Expect(':');
                            Ws();
                        }
                        Skip();
                        Ws();
                        if (Peek() == ',') { Pos++; continue; }
                        Expect(close);
                        return;
                    }
                }
                Scalar();
            }

            bool Word(string w)
            {
                if (string.CompareOrdinal(s, Pos, w, 0, w.Length) != 0) return false;
                Pos += w.Length;
                return true;
            }
        }
    }
}
