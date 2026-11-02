using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StrmInfo.Engine
{
    /// <summary>A JSON number, kept as written so it prints the way the provider sent it.</summary>
    public sealed class JNum
    {
        public readonly string Raw;
        public JNum(string raw) { Raw = raw; }
        public override string ToString() => Raw;
        public double AsDouble() =>
            double.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>
    /// Minimal JSON reader/writer. Objects become Dictionary&lt;string, object&gt; (insertion
    /// order kept), arrays List&lt;object&gt;, numbers JNum, plus string, bool and null.
    /// </summary>
    public static class Json
    {
        public static object Parse(string s)
        {
            var p = new Parser(s);
            p.Ws();
            var v = p.Value();
            p.Ws();
            if (p.Pos < s.Length) throw new FormatException("Unexpected data after JSON value at " + p.Pos);
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            public int Pos;
            readonly StringBuilder _sb = new StringBuilder();
            public Parser(string s) { _s = s; }

            public void Ws()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++; else break;
                }
            }

            public object Value()
            {
                if (Pos >= _s.Length) throw new FormatException("Unexpected end of JSON");
                var c = _s[Pos];
                switch (c)
                {
                    case '{': return Obj();
                    case '[': return Arr();
                    case '"': return Str();
                    case 't': Lit("true"); return true;
                    case 'f': Lit("false"); return false;
                    case 'n': Lit("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return Num();
                        throw new FormatException($"Unexpected '{c}' at {Pos}");
                }
            }

            void Lit(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0)
                    throw new FormatException("Bad literal at " + Pos);
                Pos += word.Length;
            }

            Dictionary<string, object> Obj()
            {
                var d = new Dictionary<string, object>();
                Pos++; Ws();
                if (Pos < _s.Length && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    Ws();
                    if (Pos >= _s.Length || _s[Pos] != '"') throw new FormatException("Expected key at " + Pos);
                    var k = Str();
                    Ws();
                    if (Pos >= _s.Length || _s[Pos] != ':') throw new FormatException("Expected ':' at " + Pos);
                    Pos++; Ws();
                    d[k] = Value();
                    Ws();
                    if (Pos >= _s.Length) throw new FormatException("Unexpected end of object");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + Pos);
                }
            }

            List<object> Arr()
            {
                var l = new List<object>();
                Pos++; Ws();
                if (Pos < _s.Length && _s[Pos] == ']') { Pos++; return l; }
                while (true)
                {
                    Ws();
                    l.Add(Value());
                    Ws();
                    if (Pos >= _s.Length) throw new FormatException("Unexpected end of array");
                    if (_s[Pos] == ',') { Pos++; continue; }
                    if (_s[Pos] == ']') { Pos++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + Pos);
                }
            }

            string Str()
            {
                Pos++; // opening quote
                int start = Pos;
                // Fast path: no escapes.
                while (Pos < _s.Length && _s[Pos] != '"' && _s[Pos] != '\\') Pos++;
                if (Pos < _s.Length && _s[Pos] == '"') { var r = _s.Substring(start, Pos - start); Pos++; return r; }
                _sb.Clear();
                _sb.Append(_s, start, Pos - start);
                while (Pos < _s.Length)
                {
                    var c = _s[Pos++];
                    if (c == '"') return _sb.ToString();
                    if (c != '\\') { _sb.Append(c); continue; }
                    if (Pos >= _s.Length) break;
                    var e = _s[Pos++];
                    switch (e)
                    {
                        case '"': _sb.Append('"'); break;
                        case '\\': _sb.Append('\\'); break;
                        case '/': _sb.Append('/'); break;
                        case 'b': _sb.Append('\b'); break;
                        case 'f': _sb.Append('\f'); break;
                        case 'n': _sb.Append('\n'); break;
                        case 'r': _sb.Append('\r'); break;
                        case 't': _sb.Append('\t'); break;
                        case 'u':
                            if (Pos + 4 > _s.Length) throw new FormatException("Bad \\u escape");
                            _sb.Append((char)Convert.ToInt32(_s.Substring(Pos, 4), 16));
                            Pos += 4;
                            break;
                        default: _sb.Append(e); break;
                    }
                }
                throw new FormatException("Unterminated string");
            }

            JNum Num()
            {
                int start = Pos;
                if (_s[Pos] == '-') Pos++;
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') Pos++;
                    else break;
                }
                return new JNum(_s.Substring(start, Pos - start));
            }
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            WriteTo(sb, v);
            return sb.ToString();
        }

        public static void WriteTo(StringBuilder sb, object v)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case JNum n: sb.Append(n.Raw); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d:
                    if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                    else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case IDictionary<string, object> d:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteTo(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case IDictionary<string, string> ds:
                    sb.Append('{');
                    bool f2 = true;
                    foreach (var kv in ds)
                    {
                        if (!f2) sb.Append(',');
                        f2 = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteString(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case IDictionary<string, long> dl:
                    sb.Append('{');
                    bool f3 = true;
                    foreach (var kv in dl)
                    {
                        if (!f3) sb.Append(',');
                        f3 = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        sb.Append(kv.Value.ToString(CultureInfo.InvariantCulture));
                    }
                    sb.Append('}');
                    break;
                case System.Collections.IEnumerable e:
                    sb.Append('[');
                    bool f4 = true;
                    foreach (var x in e)
                    {
                        if (!f4) sb.Append(',');
                        f4 = false;
                        WriteTo(sb, x);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
                    break;
            }
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- helpers that mirror Python's loose handling of provider data -------

        /// <summary>Python str() of a JSON value, with None/missing as "".</summary>
        public static string S(object v)
        {
            switch (v)
            {
                case null: return "";
                case string s: return s;
                case JNum n: return n.Raw;
                case bool b: return b ? "True" : "False";
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Python truthiness.</summary>
        public static bool Truthy(object v)
        {
            switch (v)
            {
                case null: return false;
                case string s: return s.Length > 0;
                case bool b: return b;
                case JNum n: return n.AsDouble() != 0;
                case System.Collections.ICollection c: return c.Count > 0;
                default: return true;
            }
        }

        /// <summary>Python's "a or b".</summary>
        public static object Or(object a, object b) => Truthy(a) ? a : b;

        public static object Get(object d, string key)
        {
            if (d is Dictionary<string, object> dict && dict.TryGetValue(key, out var v)) return v;
            return null;
        }

        public static Dictionary<string, object> Obj(object v) => v as Dictionary<string, object>;
        public static List<object> Arr(object v) => v as List<object>;
    }
}
