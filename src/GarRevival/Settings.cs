using System;
using System.Collections.Generic;
using System.IO;

namespace GarRevival
{
    // GARRevival/settings.cfg, key=value per line. Created with defaults on
    // first use.
    internal static class Settings
    {
        private const string DefaultConfig =
            "# GAR Revival settings\n" +
            "# true refills credits and sparks to a huge amount on every launch.\n" +
            "# Nothing is unlocked for you: parts still have to be bought.\n" +
            "unlimitedCredits=false\n" +
            "# The original new-player tutorial takes over the BATTLE button (it starts a\n" +
            "# scripted practice battle) and expects features that are not back yet.\n" +
            "# true marks it as completed; false leaves it running.\n" +
            "skipTutorial=true\n";

        public const float UnlimitedCreditsAmount = 10000000f;
        public const float UnlimitedPremiumAmount = 100000f;

        private static Dictionary<string, string> _values;

        public static bool UnlimitedCredits
        {
            get { return string.Equals(Get("unlimitedcredits"), "true", StringComparison.OrdinalIgnoreCase); }
        }

        // On unless the file says false.
        public static bool SkipTutorial
        {
            get { return !string.Equals(Get("skiptutorial"), "false", StringComparison.OrdinalIgnoreCase); }
        }

        private static string Get(string key)
        {
            if (_values == null)
            {
                _values = new Dictionary<string, string>();
                try
                {
                    string path = Path.Combine(RevivalLog.Folder, "settings.cfg");
                    if (!File.Exists(path))
                    {
                        File.WriteAllText(path, DefaultConfig);
                    }
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        int eq = line.IndexOf('=');
                        if (!line.StartsWith("#") && eq > 0)
                        {
                            _values[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
                        }
                    }
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Could not read settings.cfg", ex);
                }
            }
            string value;
            return _values.TryGetValue(key, out value) ? value : null;
        }
    }
}
