using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace GarRevival;

// Minimal JSON: objects are Dictionary<string, object>, arrays List<object>, numbers double; malformed input parses to null.
internal static class MiniJson
{
    private const int MaxDepth = 10;
    private const int MaxLength = 512 * 1024;

    private static readonly KeyValuePair<string, object>[] Words =
    [
        new("true", true),
        new("false", false),
        new("null", null),
    ];

    public static object Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return null;
        try
        {
            var r = new Reader(text);
            object value = r.Value(0);
            r.Space();
            return r.AtEnd ? value : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class Reader(string s)
    {
        private int _i;

        public bool AtEnd => _i >= s.Length;

        private char Peek => _i < s.Length ? s[_i] : '\0';

        public object Value(int depth)
        {
            Space();
            if (depth > MaxDepth || AtEnd) throw new FormatException();
            switch (Peek)
            {
                case '{':
                    var obj = new Dictionary<string, object>();
                    Items('}', () =>
                    {
                        Space();
                        string key = Text();
                        Expect(':');
                        obj[key] = Value(depth + 1);
                    });
                    return obj;
                case '[':
                    var list = new List<object>();
                    Items(']', () => list.Add(Value(depth + 1)));
                    return list;
                case '"':
                    return Text();
            }
            foreach (var word in Words)
            {
                if (string.CompareOrdinal(s, _i, word.Key, 0, word.Key.Length) == 0)
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
            if (!double.TryParse(s.Substring(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                throw new FormatException();
            }
            return n;
        }

        // "{ a, b }" or "[ a, b ]", from the opening bracket.
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
            var b = new StringBuilder();
            while (!AtEnd)
            {
                char c = s[_i++];
                if (c == '"') return b.ToString();
                if (c != '\\')
                {
                    b.Append(c);
                    continue;
                }
                char e = AtEnd ? '?' : s[_i++];
                switch (e)
                {
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'u':
                        if (_i + 4 > s.Length || !int.TryParse(s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
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
            if (Peek != c) throw new FormatException();
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

    public static string Write(object value) => value switch
    {
        null => "null",
        string text => Quote(text),
        bool flag => flag ? "true" : "false",
        int or long or float or double => Convert.ToString(value, CultureInfo.InvariantCulture),
        IDictionary<string, object> obj => "{" + string.Join(",", obj.Select(p => Quote(p.Key) + ":" + Write(p.Value)).ToArray()) + "}",
        IEnumerable items => "[" + string.Join(",", items.Cast<object>().Select(Write).ToArray()) + "]",
        _ => Quote(value.ToString()),
    };

    private static string Quote(string s)
    {
        var b = new StringBuilder("\"");
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

    public static string Str(this Dictionary<string, object> o, string key) =>
        o != null && o.TryGetValue(key, out var v) && v is string s ? s : string.Empty;

    public static int Int(this Dictionary<string, object> o, string key) =>
        o != null && o.TryGetValue(key, out var v) && v is double d && d >= int.MinValue && d <= int.MaxValue ? (int)d : 0;

    public static double Num(this Dictionary<string, object> o, string key) =>
        o != null && o.TryGetValue(key, out var v) && v is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : 0;

    public static bool Bool(this Dictionary<string, object> o, string key) => o != null && o.TryGetValue(key, out var v) && v is bool f && f;

    public static List<object> List(this Dictionary<string, object> o, string key) =>
        o != null && o.TryGetValue(key, out var v) && v is List<object> l ? l : [];
}
