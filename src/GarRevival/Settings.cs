using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

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
            "skipTutorial=true\n" +
            "# on = ammo used in battles is paid for; off = every battle starts fully loaded.\n" +
            "# In a hosted game the host's server.cfg decides.\n" +
            "ammo=on\n";

        public const float UnlimitedCreditsAmount = 10000000f;
        public const float UnlimitedPremiumAmount = 100000f;

        // Keys newer than DefaultConfig; appended to files that lack them.
        private static readonly string[][] AddedKeys =
        {
            CfgFile.Key("master", string.Empty, "Master servers (server browser, FIND MATCH), comma-separated, e.g.\nmaster=http://master.example.org:27090"),
        };

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

        // Master servers for the server browser, FIND MATCH and public games:
        // comma-separated http:// URLs. Empty hides the internet list.
        public static string[] Masters
        {
            get { return ParseMasters(Get("master")); }
        }

        public static string[] ParseMasters(string value)
        {
            return (value ?? string.Empty).Split(',').Select(u => u.Trim().TrimEnd('/'))
                .Where(u => u.Length <= 200 && u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && u.Length > 7).Distinct().Take(4).ToArray();
        }

        private static string ConfigPath
        {
            get { return Path.Combine(RevivalLog.Folder, "settings.cfg"); }
        }

        // Changes one setting in settings.cfg, keeping the rest of the file.
        public static void Set(string key, string value)
        {
            try
            {
                CfgFile.Set(ConfigPath, key, value);
                Get(key);
                _values[key.ToLowerInvariant()] = value;
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not write settings.cfg", ex);
            }
        }

        // On unless the file says off.
        public static bool Ammo
        {
            get { return !string.Equals(Get("ammo"), "off", StringComparison.OrdinalIgnoreCase); }
        }

        private static string Get(string key)
        {
            if (_values == null)
            {
                _values = new Dictionary<string, string>();
                try
                {
                    if (!File.Exists(ConfigPath))
                    {
                        File.WriteAllText(ConfigPath, DefaultConfig);
                    }
                    CfgFile.AddMissing(ConfigPath, AddedKeys);
                    _values = CfgFile.Read(ConfigPath);
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
