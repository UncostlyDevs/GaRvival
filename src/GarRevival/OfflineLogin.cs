using System.Collections;
using System.Reflection;
using JunkWars.Client;
using Steamworks;

namespace GarRevival;

// Replaces SteamDeployLogin.Start: skips the dead Steam ticket check and logs in to the loopback server.
internal static class OfflineLogin
{
    public static IEnumerator Run(SteamDeployLogin self)
    {
        yield return null;
        RevivalLog.Info("Shutdown notice bypassed; logging in to the built-in server");
        RevivalDriver.Ensure();
        // OnDestroy turns stats off and throws if they were never on, which leaves the UI suppressed.
        PhotonNetwork.networkingPeer.TrafficStatsEnabled = true;
        UserInteractionsManager.Instance.Suppress(true);
        while (!GUITextureManager.Instance.Initialized || !FontsManager.Instance.Initialized)
        {
            yield return null;
        }

        var steamId = SteamBridge.SteamId();
        if (steamId.HasValue)
        {
            typeof(SteamDeployLogin).GetField("_obtainedSteamId", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, steamId.Value);
        }

        var login = typeof(SteamDeployLogin).GetMethod("LoginToPhotonWhenReady", BindingFlags.NonPublic | BindingFlags.Instance);
        self.StartCoroutine((IEnumerator)login.Invoke(self, [GameServer.SecretUserId]));
    }
}
