using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GarRevival
{
    // Just enough JSON for the master server's answers. Objects become
    // Dictionary<string, object>, arrays List<object>, numbers double;
    // anything malformed parses to null.
    internal static class MiniJson
    {
        private const int MaxDepth = 10;
        private const int MaxLength = 512 * 1024;

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
            {
                return null;
            }
            try
            {
                Reader r = new Reader(text);
                object value = r.Value(0);
                r.Space();
                return r.AtEnd ? value : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private sealed class Reader
        {
            private readonly string _s;
            private int _i;

            public Reader(string s)
            {
                _s = s;
            }

            public bool AtEnd => _i >= _s.Length;

            private char Peek => _i < _s.Length ? _s[_i] : '\0';

            public object Value(int depth)
            {
                Space();
                if (depth > MaxDepth || AtEnd)
                {
                    throw new FormatException();
                }
                switch (Peek)
                {
                    case '{':
                        Dictionary<string, object> obj = new Dictionary<string, object>();
                        Items('}', () =>
                        {
                            Space();
                            string key = Text();
                            Expect(':');
                            obj[key] = Value(depth + 1);
                        });
                        return obj;
                    case '[':
                        List<object> list = new List<object>();
                        Items(']', () => list.Add(Value(depth + 1)));
                        return list;
                    case '"':
                        return Text();
                }
                foreach (KeyValuePair<string, object> word in Words)
                {
                    if (string.CompareOrdinal(_s, _i, word.Key, 0, word.Key.Length) == 0)
                    {
                        _i += word.Key.Length;
                        return word.Value;
                    }
                }
                int start = _i;
                while (!AtEnd && "+-.eE0123456789".IndexOf(Peek) >= 0)
                {
                    _i++;
                }
                if (!double.TryParse(_s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                {
                    throw new FormatException();
                }
                return n;
            }

            // "{ item, item }" or "[ item, item ]", starting at the opening bracket.
            private void Items(char close, Action item)
            {
                _i++;
                Space();
                if (Peek == close)
                {
                    _i++;
                    return;
                }
                while (true)
                {
                    item();
                    Space();
                    if (Peek != ',')
                    {
                        Expect(close);
                        return;
                    }
                    _i++;
                }
            }

            private string Text()
            {
                Expect('"');
                StringBuilder b = new StringBuilder();
                while (!AtEnd)
                {
                    char c = _s[_i++];
                    if (c == '"')
                    {
                        return b.ToString();
                    }
                    if (c != '\\')
                    {
                        b.Append(c);
                        continue;
                    }
                    char e = AtEnd ? '?' : _s[_i++];
                    switch (e)
                    {
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'u':
                            if (_i + 4 > _s.Length || !int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            {
                                throw new FormatException();
                            }
                            b.Append((char)code);
                            _i += 4;
                            break;
                        default: b.Append(e); break;
                    }
                }
                throw new FormatException();
            }

            private void Expect(char c)
            {
                Space();
                if (Peek != c)
                {
                    throw new FormatException();
                }
                _i++;
            }

            public void Space()
            {
                while (!AtEnd && char.IsWhiteSpace(Peek))
                {
                    _i++;
                }
            }
        }

        private static readonly KeyValuePair<string, object>[] Words =
        {
            new KeyValuePair<string, object>("true", true),
            new KeyValuePair<string, object>("false", false),
            new KeyValuePair<string, object>("null", null),
        };

        public static string Write(object value)
        {
            switch (value)
            {
                case null: return "null";
                case string text: return Quote(text);
                case bool flag: return flag ? "true" : "false";
                case int _:
                case long _:
                case float _:
                case double _:
                    return Convert.ToString(value, CultureInfo.InvariantCulture);
                case IDictionary<string, object> obj:
                    return "{" + string.Join(",", obj.Select(p => Quote(p.Key) + ":" + Write(p.Value)).ToArray()) + "}";
                case IEnumerable items:
                    return "[" + string.Join(",", items.Cast<object>().Select(Write).ToArray()) + "]";
                default:
                    return Quote(value.ToString());
            }
        }

        private static string Quote(string s)
        {
            StringBuilder b = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\')
                {
                    b.Append('\\').Append(c);
                }
                else if (c < 0x20)
                {
                    b.Append("\\u").Append(((int)c).ToString("x4"));
                }
                else
                {
                    b.Append(c);
                }
            }
            return b.Append('"').ToString();
        }

        // ---- reading fields of a parsed object ----

        public static string Str(this Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) && v is string s ? s : string.Empty;
        }

        public static int Int(this Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) && v is double d && d >= int.MinValue && d <= int.MaxValue ? (int)d : 0;
        }

        public static bool Bool(this Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) && v is bool f && f;
        }

        public static List<object> List(this Dictionary<string, object> o, string key)
        {
            return o != null && o.TryGetValue(key, out object v) && v is List<object> l ? l : new List<object>();
        }
    }
}
