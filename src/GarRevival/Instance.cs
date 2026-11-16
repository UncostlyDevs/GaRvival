using System;

namespace GarRevival;

// "GAR.exe -garprofile <name>" runs a separate player with its own save, log and test script.
internal static class Instance
{
    private static bool _read;
    private static string _profile;
    private static bool _dedicated;

    // null for the default player.
    public static string Profile
    {
        get
        {
            Read();
            return _profile;
        }
    }

    // "-garserver": dedicated server; plays as profile "server" unless -garprofile names another.
    public static bool Dedicated
    {
        get
        {
            Read();
            return _dedicated;
        }
    }

    // "" or "-<profile>", appended to per-player file and folder names.
    public static string Suffix => Profile == null ? string.Empty : "-" + Profile;

    private static void Read()
    {
        if (_read) return;
        _read = true;
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "-garserver", StringComparison.OrdinalIgnoreCase))
            {
                _dedicated = true;
            }
            if (i + 1 < args.Length && string.Equals(args[i], "-garprofile", StringComparison.OrdinalIgnoreCase))
            {
                _profile = new string(Array.FindAll(args[i + 1].ToCharArray(), char.IsLetterOrDigit));
                if (_profile.Length == 0)
                {
                    _profile = null;
                }
            }
        }
        if (_dedicated && _profile == null)
        {
            _profile = "server";
        }
    }
}
