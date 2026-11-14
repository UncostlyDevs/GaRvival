using System;

namespace GarRevival
{
    // "GAR.exe -garprofile <name>" runs the game as a separate player with its
    // own save, log and test script, so two copies can run on one PC (for
    // testing multiplayer, or for a second person sharing the computer).
    internal static class Instance
    {
        private static bool _read;
        private static string _profile;

        // null for the normal, default player.
        public static string Profile
        {
            get
            {
                Read();
                return _profile;
            }
        }

        // "-garserver": a dedicated server (DedicatedServer.cs). It plays as
        // the profile "server" unless -garprofile names another.
        public static bool Dedicated
        {
            get
            {
                Read();
                return _dedicated;
            }
        }

        private static bool _dedicated;

        private static void Read()
        {
            if (_read)
            {
                return;
            }
            _read = true;
            string[] args = Environment.GetCommandLineArgs();
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

        // "" or "-<profile>", appended to per-player file and folder names.
        public static string Suffix
        {
            get { return Profile == null ? string.Empty : "-" + Profile; }
        }
    }
}
