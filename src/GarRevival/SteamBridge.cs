using System;
using System.Reflection;
using Steamworks;

namespace GarRevival
{
    // The game's SteamManager is internal, so its Initialized flag is read by
    // reflection before touching the Steamworks API.
    internal static class SteamBridge
    {
        public static bool Initialized()
        {
            Type manager = typeof(SteamDeployLogin).Assembly.GetType("PlaformIntegration.Steam.SteamManager");
            PropertyInfo prop = manager == null ? null : manager.GetProperty("Initialized", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return prop != null && (bool)prop.GetValue(null, null);
        }

        public static string PersonaName()
        {
            try
            {
                return Initialized() ? SteamFriends.GetPersonaName() : null;
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Steam persona lookup failed", ex);
                return null;
            }
        }

        public static CSteamID? SteamId()
        {
            try
            {
                return Initialized() ? SteamUser.GetSteamID() : (CSteamID?)null;
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Steam id lookup failed", ex);
                return null;
            }
        }
    }
}
