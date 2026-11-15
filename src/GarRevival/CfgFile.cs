using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GarRevival
{
    // The key=value files in GARRevival (# starts a comment). Keys are
    // matched without regard to case; a player's own lines and comments are
    // never rewritten except the one line being set.
    internal static class CfgFile
    {
        // A key the file should have: { key, default value, comment (lines split by \n) }.
        public static string[] Key(string key, string value, string comment)
        {
            return new[] { key, value, comment };
        }

        public static Dictionary<string, string> Read(string path)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (!File.Exists(path))
            {
                return values;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (!line.StartsWith("#") && eq > 0)
                {
                    values[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
                }
            }
            return values;
        }

        // Appends the keys the file lacks, each with its comment, so files made
        // by an older version learn about new settings.
        public static void AddMissing(string path, IEnumerable<string[]> keys)
        {
            Dictionary<string, string> have = Read(path);
            string[] missing = keys.Where(k => !have.ContainsKey(k[0].ToLowerInvariant()))
                .SelectMany(k => k[2].Split('\n').Select(c => "# " + c).Concat(new[] { k[0] + "=" + k[1] })).ToArray();
            if (missing.Length == 0)
            {
                return;
            }
            string old = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            string gap = old.Length == 0 || old.EndsWith("\n") ? string.Empty : "\n";
            File.AppendAllText(path, gap + string.Join("\n", missing) + "\n");
        }

        // Sets one key: its line is replaced, or the line is added at the end.
        public static void Set(string path, string key, string value)
        {
            List<string> lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int at = lines.FindIndex(l => IsKey(l, key));
            string line = key + "=" + value;
            if (at >= 0)
            {
                lines[at] = line;
            }
            else
            {
                lines.Add(line);
            }
            File.WriteAllText(path, string.Join("\n", lines.ToArray()) + "\n");
        }

        private static bool IsKey(string raw, string key)
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            return !line.StartsWith("#") && eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase);
        }
    }
}
