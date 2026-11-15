using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace GarRevival
{
    // Talks to the master servers (master/MasterServer.cs): keeps a hosted
    // game listed while it runs, fetches server lists and match candidates,
    // and sends a dedicated server's match results to the ladder.
    // A dedicated server lists itself when dedicated.cfg has master=; a
    // player's game only when server.cfg has public=1 (masters from settings.cfg).
    internal static class MasterClient
    {
        public const string NoMasterHint = "No master server set. Add master=http://host:27090 to GARRevival\\settings.cfg.";

        private const float BeatInterval = 20f;
        private const float RetryInterval = 60f;
        private const float ChangeDelay = 2f;

        private sealed class Listing
        {
            public string Url;
            public string Id;
            public string Token;
            public bool Busy;
            public float NextAt;
            public string Status = "registering...";
        }

        private static readonly List<Listing> Listings = new List<Listing>();
        private static string _lastState = string.Empty;

        // The multiplayer protocol; servers and players must have the same one.
        public static string Version => Session.ProtocolVersion.ToString(CultureInfo.InvariantCulture);

        public static bool Configured => Settings.Masters.Length > 0;

        // "Listed on http://...: yes" per master, for the lobby screen; empty when not listing.
        public static string Status => string.Join("\n", Listings.Select(l => "Master " + l.Url + ": " + l.Status).ToArray());

        private static float Now => Time.realtimeSinceStartup;

        // ---- listing a hosted game ----

        private static string[] Wanted()
        {
            ServerRules.Rules rules = ServerRules.Active;
            if (Session.Role != Session.Roles.Host || rules == null || (Session.TcpPort == 0 && !SteamNet.Hosting))
            {
                return new string[0];
            }
            return Instance.Dedicated ? rules.Masters : rules.Public ? Settings.Masters : new string[0];
        }

        public static void Update()
        {
            string[] wanted = Wanted();
            foreach (Listing gone in Listings.Where(l => !wanted.Contains(l.Url)).ToList())
            {
                Unlist(gone);
                Listings.Remove(gone);
            }
            foreach (string url in wanted.Where(u => Listings.All(l => l.Url != u)))
            {
                Listings.Add(new Listing { Url = url });
            }
            if (Listings.Count == 0)
            {
                return;
            }
            // Tell the masters soon after players come and go or a match starts.
            string state = Session.PlayerCount + "/" + SessionBattle.Busy;
            if (state != _lastState)
            {
                _lastState = state;
                Listings.ForEach(l => l.NextAt = Mathf.Min(l.NextAt, Now + ChangeDelay));
            }
            foreach (Listing l in Listings.Where(l => !l.Busy && Now >= l.NextAt))
            {
                if (l.Id == null)
                {
                    Register(l);
                }
                else
                {
                    Beat(l);
                }
            }
        }

        private static Dictionary<string, object> Describe()
        {
            ServerRules.Rules rules = ServerRules.Active;
            OfflineBattle.Settings battle = OfflineBattle.LoadSettings();
            bool steam = Session.TcpPort == 0;
            return new Dictionary<string, object>
            {
                { "name", rules.Name },
                { "port", steam ? 1 : Session.TcpPort },
                { "transport", steam ? "steam" : "tcp" },
                { "steamLobby", steam ? SteamNet.Lobby.m_SteamID.ToString(CultureInfo.InvariantCulture) : string.Empty },
                { "lan", LanDiscovery.LocalAddresses().Take(4).ToList() },
                { "mode", battle.Mode.ToString() },
                { "map", MapId(battle.Map) },
                { "players", Session.PlayerCount },
                { "maxPlayers", rules.MaxPlayers },
                { "version", Version },
                { "rules", (rules.ServerProfiles ? "server profiles" : "own profiles") + ", rewards x" + rules.CreditRewards.ToString(CultureInfo.InvariantCulture) },
                { "dedicated", Instance.Dedicated },
                { "state", SessionBattle.Busy ? "battle" : "lobby" },
            };
        }

        private static string MapId(string map)
        {
            return PlayScreen.MapIds.FirstOrDefault(m => string.Equals(m, map, StringComparison.OrdinalIgnoreCase)) ?? "random";
        }

        private static void Register(Listing l)
        {
            l.Busy = true;
            MasterHttp.Post(l.Url + "/v1/servers", Describe(), r =>
            {
                l.Busy = false;
                if (!r.Ok)
                {
                    l.Status = "could not list (" + r.Error + ")";
                    l.NextAt = Now + RetryInterval;
                    RevivalLog.Info("Master " + l.Url + ": " + l.Status);
                    return;
                }
                l.Id = r.Json.Str("id");
                l.Token = r.Json.Str("token");
                l.Status = "listed as " + r.Json.Str("address") + ":" + r.Json.Int("port");
                l.NextAt = Now + BeatInterval;
                RevivalLog.Info("Master " + l.Url + ": registered " + l.Id + ", " + l.Status);
            });
        }

        private static void Beat(Listing l)
        {
            l.Busy = true;
            Dictionary<string, object> body = Describe();
            body["token"] = l.Token;
            MasterHttp.Post(l.Url + "/v1/servers/" + l.Id + "/beat", body, r =>
            {
                l.Busy = false;
                l.NextAt = Now + BeatInterval;
                if (r.Status == 404 || r.Status == 403)
                {
                    // The master restarted or dropped us: register again.
                    RevivalLog.Info("Master " + l.Url + ": listing " + l.Id + " is gone, registering again");
                    l.Id = null;
                    l.NextAt = Now;
                }
                else if (!r.Ok)
                {
                    l.Status = "no answer to the heartbeat (" + r.Error + ")";
                }
            });
        }

        private static void Unlist(Listing l)
        {
            if (l.Id != null)
            {
                MasterHttp.Post(l.Url + "/v1/servers/" + l.Id + "/remove", new Dictionary<string, object> { { "token", l.Token } }, null);
                RevivalLog.Info("Master " + l.Url + ": unlisted " + l.Id);
            }
        }

        // The game is closing: no frame will come to notice the session ended.
        public static void UnlistAll()
        {
            Listings.ForEach(Unlist);
            Listings.Clear();
        }

        // ---- results ----

        // Dedicated servers only: rates the players by full name. winningTeam -1 = draw.
        public static void ReportResult(string mode, string map, int seconds, int winningTeam, List<string> winners, List<string> losers)
        {
            foreach (Listing l in Listings.Where(l => l.Id != null))
            {
                Dictionary<string, object> body = new Dictionary<string, object>
                {
                    { "id", l.Id }, { "token", l.Token }, { "mode", mode }, { "map", MapId(map) },
                    { "seconds", Mathf.Clamp(seconds, 1, 7200) }, { "winningTeam", winningTeam },
                    { "winners", winners }, { "losers", losers },
                };
                string url = l.Url;
                MasterHttp.Post(url + "/v1/results", body, r => RevivalLog.Info("Master " + url + ": result " +
                    (!r.Ok ? "refused (" + r.Error + ")" : r.Json.Bool("rated") ? "rated " + MiniJson.Write(r.Json.List("ratings")) : "counted, not rated")));
            }
        }

        // ---- lists ----

        public static void FetchServers(Action<List<ServerEntry>, string> done)
        {
            Query("/v1/servers", false, done);
        }

        // Ranked candidates from the first master that answers.
        public static void FetchMatch(string mode, Action<List<ServerEntry>, string> done)
        {
            Query("/v1/match?mode=" + WWW.EscapeURL(mode) + "&version=" + Version, true, done);
        }

        private static void Query(string path, bool firstAnswer, Action<List<ServerEntry>, string> done)
        {
            string[] masters = Settings.Masters;
            List<ServerEntry> found = new List<ServerEntry>();
            if (masters.Length == 0)
            {
                done(found, NoMasterHint);
                return;
            }
            int left = masters.Length;
            string error = null;
            Action<int> ask = null;
            ask = i => MasterHttp.Get(masters[i] + path, r =>
            {
                if (r.Ok)
                {
                    found.AddRange(r.Json.List("servers").OfType<Dictionary<string, object>>().Select(ServerEntry.FromMaster));
                }
                else
                {
                    error = "Master " + masters[i] + ": " + r.Error;
                }
                if (firstAnswer && !r.Ok && i + 1 < masters.Length)
                {
                    ask(i + 1);
                }
                else if (firstAnswer || --left == 0)
                {
                    done(found.GroupBy(s => s.Key).Select(g => g.First()).ToList(), found.Count == 0 ? error : null);
                }
            });
            if (firstAnswer)
            {
                ask(0);
            }
            else
            {
                for (int i = 0; i < masters.Length; i++)
                {
                    ask(i);
                }
            }
        }
    }
}
