using System;
using System.Reflection;
using Steamworks;

namespace GarRevival;

// The game's SteamManager is internal; its Initialized flag is read by reflection before any Steamworks call.
internal static class SteamBridge
{
    public static bool Initialized()
    {
        var manager = typeof(SteamDeployLogin).Assembly.GetType("PlaformIntegration.Steam.SteamManager");
        var prop = manager?.GetProperty("Initialized", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
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
