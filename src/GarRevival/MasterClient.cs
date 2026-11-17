using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace GarRevival;

// A dedicated server lists itself on dedicated.cfg's masters (empty = this PC's); a player's game only with public=1 in server.cfg.
// A dedicated server lists itself when dedicated.cfg has master=; a player's game only with public=1 in server.cfg.
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
        public int Failures;
        public string Status = "registering...";
    }

    private static readonly List<Listing> Listings = [];
    private static string _lastState = string.Empty;

    // Servers and players must speak the same multiplayer protocol.
    public static string Version => Session.ProtocolVersion.ToString(CultureInfo.InvariantCulture);

    public static bool Configured => Settings.Masters.Length > 0;

    // One line per master for the lobby screen; empty when not listing.
    public static string Status => string.Join("\n", Listings.Select(l => $"Master {l.Url}: {l.Status}").ToArray());

    private static float Now => Time.realtimeSinceStartup;

    private static string[] Wanted()
    {
        var rules = ServerRules.Active;
        if (Session.Role != Session.Roles.Host || rules == null || (Session.TcpPort == 0 && !SteamNet.Hosting)) return [];
        return Instance.Dedicated ? rules.Masters : rules.Public ? Settings.Masters : [];
    }

    public static void Update()
    {
        string[] wanted = Wanted();
        foreach (var gone in Listings.Where(l => !wanted.Contains(l.Url)).ToList())
        {
            Unlist(gone);
            Listings.Remove(gone);
        }
        foreach (string url in wanted.Where(u => Listings.All(l => l.Url != u)))
        {
            Listings.Add(new Listing { Url = url });
        }
        if (Listings.Count == 0) return;
        // Tell the masters soon after players come and go or a match starts.
        string state = $"{Session.PlayerCount}/{SessionBattle.Busy}";
        if (state != _lastState)
        {
            _lastState = state;
            Listings.ForEach(l => l.NextAt = Mathf.Min(l.NextAt, Now + ChangeDelay));
        }
        foreach (var l in Listings.Where(l => !l.Busy && Now >= l.NextAt))
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
        var rules = ServerRules.Active;
        var battle = OfflineBattle.LoadSettings();
        bool steam = Session.TcpPort == 0;
        return new()
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

    private static string MapId(string map) => PlayScreen.MapIds.FirstOrDefault(m => string.Equals(m, map, StringComparison.OrdinalIgnoreCase)) ?? "random";

    private static void Register(Listing l)
    {
        l.Busy = true;
        MasterHttp.Post(l.Url + "/v1/servers", Describe(), r =>
        {
            l.Busy = false;
            if (!r.Ok)
            {
                l.Status = $"could not list ({r.Error})";
                MasterLauncher.Revive(l.Url, r);
                // 5, 10, 20, 40, then 60 s: a master the game just started needs a few seconds to compile.
                l.NextAt = Now + Mathf.Min(RetryInterval, 5f * (1 << Mathf.Min(l.Failures++, 4)));
                RevivalLog.Info($"Master {l.Url}: {l.Status}");
                return;
            }
            l.Failures = 0;
            l.Id = r.Json.Str("id");
            l.Token = r.Json.Str("token");
            l.Status = $"listed as {r.Json.Str("address")}:{r.Json.Int("port")}";
            l.NextAt = Now + BeatInterval;
            RevivalLog.Info($"Master {l.Url}: registered {l.Id}, {l.Status}");
        });
    }

    private static void Beat(Listing l)
    {
        l.Busy = true;
        var body = Describe();
        body["token"] = l.Token;
        MasterHttp.Post($"{l.Url}/v1/servers/{l.Id}/beat", body, r =>
        {
            l.Busy = false;
            l.NextAt = Now + BeatInterval;
            if (r.Status == 404 || r.Status == 403)
            {
                // The master restarted or dropped us.
                RevivalLog.Info($"Master {l.Url}: listing {l.Id} is gone, registering again");
                l.Id = null;
                l.NextAt = Now;
            }
            else if (!r.Ok)
            {
                l.Status = $"no answer to the heartbeat ({r.Error})";
                MasterLauncher.Revive(l.Url, r);
            }
        });
    }

    private static void Unlist(Listing l)
    {
        if (l.Id == null) return;
        MasterHttp.Post($"{l.Url}/v1/servers/{l.Id}/remove", new Dictionary<string, object> { { "token", l.Token } }, null);
        RevivalLog.Info($"Master {l.Url}: unlisted {l.Id}");
    }

    // On quit: no frame will come to notice the session ended.
    public static void UnlistAll()
    {
        Listings.ForEach(Unlist);
        Listings.Clear();
    }

    // Dedicated servers only; players are rated by full name. winningTeam -1 = draw.
    public static void ReportResult(string mode, string map, int seconds, int winningTeam, List<string> winners, List<string> losers)
    {
        foreach (var l in Listings.Where(l => l.Id != null))
        {
            var body = new Dictionary<string, object>
            {
                { "id", l.Id }, { "token", l.Token }, { "mode", mode }, { "map", MapId(map) },
                { "seconds", Mathf.Clamp(seconds, 1, 7200) }, { "winningTeam", winningTeam },
                { "winners", winners }, { "losers", losers },
            };
            string url = l.Url;
            MasterHttp.Post(url + "/v1/results", body, r => RevivalLog.Info($"Master {url}: result " +
                (!r.Ok ? $"refused ({r.Error})" : r.Json.Bool("rated") ? "rated " + MiniJson.Write(r.Json.List("ratings")) : "counted, not rated")));
        }
    }

    public static void FetchServers(Action<List<ServerEntry>, string> done) => Query("/v1/servers", false, done);

    // Ranked candidates from the first master that answers.
    public static void FetchMatch(string mode, Action<List<ServerEntry>, string> done) =>
        Query($"/v1/match?mode={WWW.EscapeURL(mode)}&version={Version}", true, done);

    private static void Query(string path, bool firstAnswer, Action<List<ServerEntry>, string> done)
    {
        string[] masters = Settings.Masters;
        var found = new List<ServerEntry>();
        if (masters.Length == 0)
        {
            done(found, NoMasterHint);
            return;
        }
        int left = masters.Length;
        string error = null;
        void Ask(int i) => MasterHttp.Get(masters[i] + path, r =>
        {
            if (r.Ok)
            {
                found.AddRange(r.Json.List("servers").OfType<Dictionary<string, object>>().Select(ServerEntry.FromMaster));
            }
            else
            {
                error = $"Master {masters[i]}: {r.Error}";
                MasterLauncher.Revive(masters[i], r);
            }
            if (firstAnswer && !r.Ok && i + 1 < masters.Length)
            {
                Ask(i + 1);
            }
            else if (firstAnswer || --left == 0)
            {
                done(found.GroupBy(s => s.Key).Select(g => g.First()).ToList(), found.Count == 0 ? error : null);
            }
        });
        if (firstAnswer)
        {
            Ask(0);
        }
        else
        {
            for (int i = 0; i < masters.Length; i++)
            {
                Ask(i);
            }
        }
    }
}
