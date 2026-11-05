using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apocars
{
    /// Minimal JSON: objects are Dictionary<string, object> (insertion order kept), arrays List<object>,
    /// numbers double (long when integral on read), plus string, bool and null.
    internal static class Json
    {
        // ------------------------------------------------------------------ writing

        public static string Write(object v, bool pretty = true)
        {
            var sb = new StringBuilder();
            Write(sb, v, pretty, 0);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v, bool pretty, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: Str(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: Num(sb, d); return;
                case float f: Num(sb, f); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case ushort us: sb.Append(us.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sby: sb.Append(sby.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary<string, object> o:
                {
                    if (o.Count == 0) { sb.Append("{}"); return; }
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        if (pretty) { sb.Append('\n'); sb.Append(' ', (indent + 1) * 2); }
                        Str(sb, kv.Key);
                        sb.Append(pretty ? ": " : ":");
                        Write(sb, kv.Value, pretty, indent + 1);
                    }
                    if (pretty) { sb.Append('\n'); sb.Append(' ', indent * 2); }
                    sb.Append('}');
                    return;
                }
                case IList list:
                {
                    if (list.Count == 0) { sb.Append("[]"); return; }
                    bool simple = true;
                    foreach (var e in list) if (e is IDictionary || e is IList) { simple = false; break; }
                    sb.Append('[');
                    for (int k = 0; k < list.Count; k++)
                    {
                        if (k > 0) sb.Append(simple && pretty ? ", " : ",");
                        if (pretty && !simple) { sb.Append('\n'); sb.Append(' ', (indent + 1) * 2); }
                        Write(sb, list[k], pretty, indent + 1);
                    }
                    if (pretty && !simple) { sb.Append('\n'); sb.Append(' ', indent * 2); }
                    sb.Append(']');
                    return;
                }
                default: Str(sb, v.ToString()); return;
            }
        }

        private static void Num(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { Str(sb, d.ToString(CultureInfo.InvariantCulture)); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Num(StringBuilder sb, float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) { Str(sb, f.ToString(CultureInfo.InvariantCulture)); return; }
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
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

        // ------------------------------------------------------------------ reading

        public static object Read(string s)
        {
            int p = 0;
            var v = Value(s, ref p);
            Ws(s, ref p);
            if (p != s.Length) throw new FormatException($"JSON: unexpected '{s[p]}' at {p}");
            return v;
        }

        private static void Ws(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') p++;
                else if (c == '/' && p + 1 < s.Length && s[p + 1] == '/') { while (p < s.Length && s[p] != '\n') p++; }
                else break;
            }
        }

        private static object Value(string s, ref int p)
        {
            Ws(s, ref p);
            if (p >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[p];
            if (c == '{')
            {
                p++;
                var o = new Dictionary<string, object>();
                Ws(s, ref p);
                if (s[p] == '}') { p++; return o; }
                while (true)
                {
                    Ws(s, ref p);
                    string k = String(s, ref p);
                    Ws(s, ref p);
                    if (s[p++] != ':') throw new FormatException($"JSON: ':' expected at {p - 1}");
                    o[k] = Value(s, ref p);
                    Ws(s, ref p);
                    char e = s[p++];
                    if (e == '}') return o;
                    if (e != ',') throw new FormatException($"JSON: ',' or '}}' expected at {p - 1}");
                }
            }
            if (c == '[')
            {
                p++;
                var a = new List<object>();
                Ws(s, ref p);
                if (s[p] == ']') { p++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref p));
                    Ws(s, ref p);
                    char e = s[p++];
                    if (e == ']') return a;
                    if (e != ',') throw new FormatException($"JSON: ',' or ']' expected at {p - 1}");
                }
            }
            if (c == '"') return String(s, ref p);
            if (s.Length - p >= 4 && string.CompareOrdinal(s, p, "true", 0, 4) == 0) { p += 4; return true; }
            if (s.Length - p >= 5 && string.CompareOrdinal(s, p, "false", 0, 5) == 0) { p += 5; return false; }
            if (s.Length - p >= 4 && string.CompareOrdinal(s, p, "null", 0, 4) == 0) { p += 4; return null; }
            int start = p;
            while (p < s.Length && "+-0123456789.eE".IndexOf(s[p]) >= 0) p++;
            string num = s.Substring(start, p - start);
            if (num.Length == 0) throw new FormatException($"JSON: unexpected '{c}' at {start}");
            if (num.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            return double.Parse(num, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static string String(string s, ref int p)
        {
            if (s[p] != '"') throw new FormatException($"JSON: string expected at {p}");
            p++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[p++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[p++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)Convert.ToInt32(s.Substring(p, 4), 16)); p += 4; break;
                    default: sb.Append(e); break;
                }
            }
        }

        /// A deep copy (objects and arrays copied, the rest shared: strings and numbers don't change).
        public static object Clone(object v)
        {
            if (v is Dictionary<string, object> d)
            {
                var o = new Dictionary<string, object>(d.Count);
                foreach (var kv in d) o[kv.Key] = Clone(kv.Value);
                return o;
            }
            if (v is List<object> l)
            {
                var a = new List<object>(l.Count);
                foreach (var e in l) a.Add(Clone(e));
                return a;
            }
            return v;
        }

        // ------------------------------------------------------------------ helpers for reading values

        public static Dictionary<string, object> Obj(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> Arr(object o, string key) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) ? v as List<object> : null;

        public static string Str(object o, string key, string def = null) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v != null ? v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture) : def;

        public static double Num(object o, string key, double def = 0) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v != null ? ToDouble(v, def) : def;

        public static bool Bool(object o, string key, bool def = false) =>
            o is Dictionary<string, object> d && d.TryGetValue(key, out var v) && v is bool b ? b : def;

        public static double ToDouble(object v, double def = 0)
        {
            switch (v)
            {
                case double d: return d;
                case long l: return l;
                case int i: return i;
                case float f: return f;
                case bool b: return b ? 1 : 0;
                case string s:
                    if (s == "NaN") return double.NaN;
                    if (s == "Infinity") return double.PositiveInfinity;
                    if (s == "-Infinity") return double.NegativeInfinity;
                    return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
                default: return def;
            }
        }
    }
}
