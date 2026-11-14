using System.Collections;
using System.Reflection;
using JunkWars.Client;
using Steamworks;

namespace GarRevival
{
    // Replacement for SteamDeployLogin.Start. The original asked play-gar.com
    // to validate a Steam ticket; that web API is gone, so go straight to the
    // game's own Photon login, which the loopback server answers.
    internal static class OfflineLogin
    {
        public static IEnumerator Run(SteamDeployLogin self)
        {
            yield return null;
            RevivalLog.Info("Shutdown notice bypassed; logging in to the built-in server");
            RevivalDriver.Ensure();
            // Same setup as the original Start; OnDestroy turns stats off again
            // and throws if they were never turned on, which would leave the UI
            // suppressed.
            PhotonNetwork.networkingPeer.TrafficStatsEnabled = true;
            UserInteractionsManager.Instance.Suppress(true);
            while (!GUITextureManager.Instance.Initialized || !FontsManager.Instance.Initialized)
            {
                yield return null;
            }

            CSteamID? steamId = SteamBridge.SteamId();
            if (steamId.HasValue)
            {
                typeof(SteamDeployLogin).GetField("_obtainedSteamId", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, steamId.Value);
            }

            MethodInfo login = typeof(SteamDeployLogin).GetMethod("LoginToPhotonWhenReady", BindingFlags.NonPublic | BindingFlags.Instance);
            self.StartCoroutine((IEnumerator)login.Invoke(self, new object[] { GameServer.SecretUserId }));
        }
    }
}
