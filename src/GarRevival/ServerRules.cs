using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using JunkWars.Client;
using UnityEngine;

namespace GarRevival;

// Rules a host sets for its players (server.cfg): own profiles by default, or per-server profiles
// with their own starting money and reward rates.
internal static class ServerRules
{
    public class Rules
    {
        public string Id = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string Name = "My server";
        public bool ServerProfiles;
        public float StartingCredits = 20000f;
        public float StartingSparks = 0f;
        public float CreditRewards = 1f;
        public float ExperienceRewards = 1f;
        public bool Ammo = true;
        // Host only: players' guild benefits count in battles.
        public bool GuildBuffs = true;
        // Dedicated server (-garserver) only.
        public bool Steam;
        public int Port = 27015;
        public int MinPlayers = 1;
        public float MatchDelay = 30f;
        // A dedicated server does not count itself.
        public int MaxPlayers = 10;
        // Player-hosted: list on the settings.cfg masters.
        public bool Public;
        // Dedicated: list on these masters.
        public string[] Masters = [];
    }

    // Keys newer than the file Write() makes; appended to files that lack them.
    private static readonly string[][] AddedKeys =
    [
        CfgFile.Key("maxPlayers", "10", "players let in at once, the host included (a dedicated server does not count itself)"),
        CfgFile.Key("public", "0", "player-hosted games: 1 lists your game on the master servers in settings.cfg (master=);\nthe list shows your public IP address to everyone"),
        CfgFile.Key("master", string.Empty, "dedicated server: master servers to list it on, comma-separated, e.g. http://master.example.org:27090"),
    ];

    private static Rules _hosted;
    private static float _reloadAt;

    // Null when playing alone.
    public static Rules Active { get; private set; }

    public static float CreditRewards => Active?.CreditRewards ?? 1f;

    public static float ExperienceRewards => Active?.ExperienceRewards ?? 1f;

    // Re-read each time hosting starts, so edits apply without a restart.
    public static Rules Hosted => _hosted = Load();

    // A dedicated server keeps its own file, apart from the rules a player hosts with.
    private static string ConfigPath => Path.Combine(RevivalLog.Folder, Instance.Dedicated ? "dedicated.cfg" : $"server{Instance.Suffix}.cfg");

    private static Rules Load()
    {
        var rules = new Rules();
        bool hasId = false;
        if (File.Exists(ConfigPath))
        {
            foreach (string raw in File.ReadAllLines(ConfigPath))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (line.StartsWith("#") || eq < 0) continue;
                string value = line.Substring(eq + 1).Trim();
                try
                {
                    switch (line.Substring(0, eq).Trim().ToLowerInvariant())
                    {
                        case "id": if (value.Length > 0) { rules.Id = Safe(value); hasId = true; } break;
                        case "name": rules.Name = value; break;
                        case "profiles": rules.ServerProfiles = string.Equals(value, "server", StringComparison.OrdinalIgnoreCase); break;
                        case "startingcredits": rules.StartingCredits = Number(value); break;
                        case "startingsparks": rules.StartingSparks = Number(value); break;
                        case "creditrewards": rules.CreditRewards = Mathf.Clamp(Number(value), 0f, 100f); break;
                        case "experiencerewards": rules.ExperienceRewards = Mathf.Clamp(Number(value), 0f, 100f); break;
                        case "ammo": rules.Ammo = !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase); break;
                        case "guildbuffs": rules.GuildBuffs = !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase); break;
                        case "host": rules.Steam = string.Equals(value, "steam", StringComparison.OrdinalIgnoreCase); break;
                        case "port": rules.Port = Mathf.Clamp((int)Number(value), 1, 65535); break;
                        case "minplayers": rules.MinPlayers = Mathf.Clamp((int)Number(value), 1, 9); break;
                        case "matchdelay": rules.MatchDelay = Mathf.Clamp(Number(value), 5f, 3600f); break;
                        case "maxplayers": rules.MaxPlayers = Mathf.Clamp((int)Number(value), 1, 64); break;
                        case "public": rules.Public = value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); break;
                        case "master": rules.Masters = Settings.ParseMasters(value); break;
                    }
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("server.cfg: bad value in line: " + line, ex);
                }
            }
        }
        try
        {
            if (!hasId) Write(rules);
            CfgFile.AddMissing(ConfigPath, AddedKeys);
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Could not write {ConfigPath}", ex);
        }
        return rules;
    }

    private static void Write(Rules r)
    {
        File.WriteAllText(ConfigPath, string.Join("\n", new[]
        {
            "# GAR Revival: rules for games you host. Read when you start hosting.",
            "# Identifies your server to players' games; keep it to keep their server profiles.",
            "id=" + r.Id,
            "name=" + r.Name,
            "# own    = players use their own profile: their unlocks and money count here",
            "# server = every player (you too) gets a separate profile that exists only for",
            "#          this server and starts from the values below",
            "profiles=" + (r.ServerProfiles ? "server" : "own"),
            "startingCredits=" + r.StartingCredits.ToString(CultureInfo.InvariantCulture),
            "startingSparks=" + r.StartingSparks.ToString(CultureInfo.InvariantCulture),
            "# multipliers for what battles on this server pay out",
            "creditRewards=" + r.CreditRewards.ToString(CultureInfo.InvariantCulture),
            "experienceRewards=" + r.ExperienceRewards.ToString(CultureInfo.InvariantCulture),
            "# on = ammo used in battles is paid for; off = every battle starts fully loaded",
            "ammo=" + (r.Ammo ? "on" : "off"),
            "# on or off: guild benefits (damage, speed, ...) count in battles here",
            "guildBuffs=" + (r.GuildBuffs ? "on" : "off"),
            "# only for a dedicated server (GAR.exe -garserver):",
            "# host = tcp (LAN or port-forwarded, on the port below) or steam (a Steam lobby)",
            "host=" + (r.Steam ? "steam" : "tcp"),
            "port=" + r.Port,
            "# a match starts matchDelay seconds after at least minPlayers players are in",
            "minPlayers=" + r.MinPlayers,
            "matchDelay=" + r.MatchDelay.ToString(CultureInfo.InvariantCulture),
            "# players let in at once, the host included (a dedicated server does not count itself)",
            "maxPlayers=" + r.MaxPlayers,
            "# player-hosted games: 1 lists your game on the master servers in settings.cfg (master=);",
            "# the list shows your public IP address to everyone",
            "public=" + (r.Public ? "1" : "0"),
            "# dedicated server: master servers to list it on, comma-separated, e.g. http://master.example.org:27090",
            "master=" + string.Join(",", r.Masters),
            string.Empty,
        }));
    }

    private static float Number(string value) => float.Parse(value, CultureInfo.InvariantCulture);

    // Ids become folder names.
    private static string Safe(string id)
    {
        char[] chars = id.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
            {
                chars[i] = '_';
            }
        }
        var safe = new string(chars);
        return safe.Length > 40 ? safe.Substring(0, 40) : safe;
    }

    // Sent to players who join.
    public static string Pack(Rules r) => string.Join("|",
    [
        r.Id, r.Name.Replace('|', ' '), r.ServerProfiles ? "1" : "0",
        r.StartingCredits.ToString(CultureInfo.InvariantCulture), r.StartingSparks.ToString(CultureInfo.InvariantCulture),
        r.CreditRewards.ToString(CultureInfo.InvariantCulture), r.ExperienceRewards.ToString(CultureInfo.InvariantCulture),
        r.Ammo ? "1" : "0",
    ]);

    public static Rules Unpack(string packed)
    {
        string[] p = (packed ?? string.Empty).Split('|');
        if (p.Length < 7) return null;
        return new Rules
        {
            Id = Safe(p[0]),
            Name = p[1],
            ServerProfiles = p[2] == "1",
            StartingCredits = Number(p[3]),
            StartingSparks = Number(p[4]),
            CreditRewards = Mathf.Clamp(Number(p[5]), 0f, 100f),
            ExperienceRewards = Mathf.Clamp(Number(p[6]), 0f, 100f),
            Ammo = p.Length < 8 || p[7] != "0",
        };
    }

    // Host: when hosting starts. Client: when the host's rules arrive.
    public static void Enter(Rules rules)
    {
        if (rules == null) return;
        Active = rules;
        RevivalLog.Info($"Server rules: \"{rules.Name}\" ({rules.Id}), profiles={(rules.ServerProfiles ? "server" : "own")}" +
            $", rewards x{rules.CreditRewards} credits, x{rules.ExperienceRewards} experience, ammo {(rules.Ammo ? "on" : "off")}" +
            (rules.GuildBuffs ? string.Empty : ", guild benefits off"));
        if (rules.ServerProfiles)
        {
            PlayerState.UseServerProfile(rules.Id, rules.StartingCredits, rules.StartingSparks);
            ReloadProfile();
            GameServer.Notice("This server keeps its own profiles: your garage here is separate from your own.");
        }
    }

    public static void Exit()
    {
        var rules = Active;
        Active = null;
        if (rules is { ServerProfiles: true } && PlayerState.ServerProfile != null)
        {
            PlayerState.UseServerProfile(null, 0f, 0f);
            ReloadProfile();
        }
    }

    // The client re-requests the profile as after login; the garage is rebuilt once it is applied.
    private static void ReloadProfile()
    {
        RevivalLog.Info("Switching to " + (PlayerState.ServerProfile == null ? "your own profile" : $"the server profile {PlayerState.ServerProfile}"));
        if (GameManager.Instance.currentGameState == GameState.Garage)
        {
            // Tear the garage down as before a battle, while the old profile's robots still exist.
            typeof(GameManager).GetMethod("CleanUpGarage", BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(GameManager.Instance, null);
        }
        PhotonNetwork.GetUserData();
        _reloadAt = Time.realtimeSinceStartup + 1f;
    }

    public static void Update()
    {
        if (_reloadAt > 0f && Time.realtimeSinceStartup >= _reloadAt)
        {
            _reloadAt = 0f;
            if (GameManager.Instance.currentGameState == GameState.Garage)
            {
                GameManager.Instance.ChangeGameState(GameState.Garage);
            }
        }
    }
}
