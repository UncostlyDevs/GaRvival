using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace GarRevival;

// GARRevival/settings.cfg, key=value per line, created with defaults on first use.
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
    [
        CfgFile.Key("master", string.Empty, "Master servers (server browser, FIND MATCH, ladder), comma-separated, e.g.\nmaster=http://master.example.org:27090\nEmpty = the master on this PC, which the game starts; off = none"),
        CfgFile.Key("masterAutoStart", "on", "on = the game starts the bundled master (master\\MasterServer.cs) on this PC\nwhen master= points here and none is running"),
    ];

    private static Dictionary<string, string> _values;

    public static bool UnlimitedCredits => string.Equals(Get("unlimitedcredits"), "true", StringComparison.OrdinalIgnoreCase);

    // On unless the file says false.
    public static bool SkipTutorial => !string.Equals(Get("skiptutorial"), "false", StringComparison.OrdinalIgnoreCase);

    // Comma-separated http:// URLs; empty = this PC's master, "off" = none.
    public static string[] Masters => ParseMasters(Get("master"));

    public static bool MasterAutoStart => !string.Equals(Get("masterautostart"), "off", StringComparison.OrdinalIgnoreCase);

    // On unless the file says off.
    public static bool Ammo => !string.Equals(Get("ammo"), "off", StringComparison.OrdinalIgnoreCase);

    public static int SkinTextureSize => Mathf.ClosestPowerOfTwo(Int("skintexturesize", 1024, 128, 2048));

    public static bool SkinCompress => !string.Equals(Get("skincompress"), "false", StringComparison.OrdinalIgnoreCase);

    public static int SkinCacheParts => Int("skincacheparts", 48, 4, 512);

    private static string ConfigPath => Path.Combine(RevivalLog.Folder, "settings.cfg");

    public static string[] ParseMasters(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0) return [MasterLauncher.LocalUrl];
        return value.Split(',').Select(u => u.Trim().TrimEnd('/'))
            .Where(u => u.Length <= 200 && u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && u.Length > 7).Distinct().Take(4).ToArray();
    }

    // Changes one setting, keeping the rest of the file.
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

    private static int Int(string key, int fallback, int min, int max) =>
        int.TryParse(Get(key), out int value) ? Math.Min(max, Math.Max(min, value)) : fallback;

    private static string Get(string key)
    {
        if (_values == null)
        {
            _values = new();
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
        return _values.TryGetValue(key, out string value) ? value : null;
    }
}
