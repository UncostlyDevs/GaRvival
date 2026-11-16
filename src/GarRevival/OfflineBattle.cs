using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Arena;
using JunkWars.Client.Arena.Offline;
using JunkWars.Client.Robot;
using JunkWars.Common.Arena;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Player.ValueObjects;

namespace GarRevival;

// BATTLE starts the client's own offline mode (OfflineArena + OfflineServerEmulator) with bots,
// configured by GARRevival/battle.cfg.
internal static class OfflineBattle
{
    private static readonly string[] Maps = ["Western", "WesternNight", "Laundry", "Pirates", "Medieval", "DragonTemple", "Pompei", "Dungeon"];

    // GenerateBattleData always puts the player on team 1.
    private const int PlayerTeam = 1;

    internal sealed class Settings
    {
        public bool HumansSplit;
        public string Map = "random";
        public GameModeType Mode = GameModeType.TeamDeadmach;
        public int Enemies = 4;
        public int Allies = 3;
        public int Minutes = 10;
        public bool WeakEnemies;
    }

    private const string DefaultConfig =
        "# GAR Revival offline battle settings\n" +
        "# map: random, Western, WesternNight, Laundry, Pirates, Medieval, DragonTemple, Pompei, Dungeon\n" +
        "map=random\n" +
        "# mode: TeamDeadmach, CaptureTheFlag, DestoryBase\n" +
        "mode=TeamDeadmach\n" +
        "# bots on each side (at most 5 enemies and 4 allies: the arenas hold 5 robots a team)\n" +
        "enemies=4\n" +
        "allies=3\n" +
        "minutes=10\n" +
        "# in a multiplayer session: together = all players on one team against bots,\n" +
        "# split = players divided between the two teams\n" +
        "humans=together\n" +
        "# true makes enemy bots weaker\n" +
        "weakEnemies=false\n";

    // A dedicated server's matches have their own settings file.
    private static string SettingsPath => Path.Combine(RevivalLog.Folder, Instance.Dedicated ? "dedicated-battle.cfg" : "battle.cfg");

    private static int LocalTeam => SessionBattle.Active ? SessionBattle.LocalTeam : PlayerTeam;

    // Written by the Play screen whenever a match is started.
    internal static void SaveSettings(Settings s)
    {
        string[] lines =
        [
            "# GAR Revival match settings. The Play screen in the game edits this file.",
            "# map: random, Western, WesternNight, Laundry, Pirates, Medieval, DragonTemple, Pompei, Dungeon",
            "map=" + s.Map,
            "# mode: TeamDeadmach, CaptureTheFlag, DestoryBase",
            "mode=" + s.Mode,
            "# bots on each side (at most 5 enemies and 4 allies: the arenas hold 5 robots a team)",
            "enemies=" + s.Enemies,
            "allies=" + s.Allies,
            "minutes=" + s.Minutes,
            "# in a multiplayer session: together = all players on one team against bots,",
            "# split = players divided between the two teams",
            "humans=" + (s.HumansSplit ? "split" : "together"),
            "# true makes enemy bots weaker",
            "weakEnemies=" + (s.WeakEnemies ? "true" : "false"),
        ];
        try
        {
            File.WriteAllText(SettingsPath, string.Join("\n", lines) + "\n");
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not save battle.cfg", ex);
        }
    }

    internal static Settings LoadSettings()
    {
        var settings = new Settings();
        string path = SettingsPath;
        if (!File.Exists(path))
        {
            File.WriteAllText(path, DefaultConfig);
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            int eq = line.IndexOf('=');
            if (line.StartsWith("#") || eq < 0) continue;
            string key = line.Substring(0, eq).Trim().ToLowerInvariant();
            string value = line.Substring(eq + 1).Trim();
            try
            {
                switch (key)
                {
                    case "map": settings.Map = value; break;
                    case "mode": settings.Mode = (GameModeType)Enum.Parse(typeof(GameModeType), value, true); break;
                    case "enemies": settings.Enemies = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "allies": settings.Allies = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "minutes": settings.Minutes = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "weakenemies": settings.WeakEnemies = bool.Parse(value); break;
                    case "humans": settings.HumansSplit = string.Equals(value, "split", StringComparison.OrdinalIgnoreCase); break;
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("battle.cfg: bad value for " + key, ex);
            }
        }
        settings.Enemies = Math.Max(1, Math.Min(5, settings.Enemies));
        settings.Allies = Math.Max(0, Math.Min(4, settings.Allies));
        settings.Minutes = Math.Max(1, settings.Minutes);
        return settings;
    }

    // The local player's selected robot as the battle sees it.
    internal static ActorVO BuildLocalActor()
    {
        var player = PlayerManager.Instance.player.valueObject;
        var robot = GameManager.Instance.garage != null ? GameManager.Instance.garage.currentRobotAssembly : null;
        if (robot == null)
        {
            robot = PlayerManager.Instance.player.GetRobotAssemblyFromID(player.ownedRobotAssemblies[player.selectedRobotIndex].assemblyID);
        }
        var actor = RandomRobotGenerator.BuildActorVOFromRobot(robot, player);
        actor.name = player.playerName;
        actor.fullName = player.fullName;
        actor.level = player.level;
        // The client's PlayerVO is the copy from login; the ladder has moved on since.
        var current = PlayerState.Current;
        actor.ladderRating = current.ladderRating;
        actor.gamesCount = current.seasonBattles;
        actor.league = LadderOps.League(current);
        actor.leaguePosition = LadderOps.Position(current);
        actor.battleInventoryItems = BattleUpkeep.BattleInventory();
        actor.activeGuildBuffs = SocialGuild.BattleBuffs();
        LogGuildBuffs(actor);
        AmmoOps.LoadActor(actor);
        return actor;
    }

    internal static void LogGuildBuffs(ActorVO actor)
    {
        if (actor.activeGuildBuffs.Count > 0)
        {
            RevivalLog.Info($"Battle: {actor.fullName} guild benefits {string.Join(", ", actor.activeGuildBuffs.ToArray())}");
        }
    }

    internal static string PickMap(Settings settings) =>
        Maps.FirstOrDefault(m => string.Equals(m, settings.Map, StringComparison.OrdinalIgnoreCase))
            ?? Maps[UnityEngine.Random.Range(0, Maps.Length)];

    public static void Start()
    {
        var settings = LoadSettings();
        var actor = BuildLocalActor();
        AmmoOps.RecordStart(actor);
        string map = PickMap(settings);
        BattleManager.offlineBattleData = OfflineServerEmulator.GenerateBattleData(map, settings.Mode, settings.Minutes * 60, actor, settings.Enemies, settings.Allies, settings.WeakEnemies);
        RevivalLog.Info($"Offline battle: {map}, {settings.Mode}, {settings.Enemies} enemies vs you + {settings.Allies} allies, {settings.Minutes} min");
        GameManager.Instance.ChangeGameState(GameState.OfflineArena);
    }

    // Applies the emulator's own reward calculation to the saved profile and tells the client.
    public static void ApplyResult(EndBattleData result)
    {
        if (result == null) return;
        if (DedicatedServer.Enabled)
        {
            // The server watched a bot; nothing of its own to reward.
            RevivalLog.Info($"Dedicated server: match over, winning team {result.winningTeam}");
            LogCombatants();
            return;
        }
        // First: event days change the rewards, and mission progress is queued until the profile is updated.
        MissionsOps.OnBattleFinished(result, LocalTeam);
        // A joined client gets its wear from the host instead.
        var arena = BattleManager.arena;
        if (!(SessionBattle.Active && Session.Role == Session.Roles.Client) && arena != null && arena.possessedRobot != null)
        {
            BattleUpkeep.ApplyWear(BattleUpkeep.EndDurability(arena.possessedRobot.actorId));
            if (AmmoOps.Economy)
            {
                AmmoOps.ApplyBattleUse(AmmoOps.EndAmmo(arena.possessedRobot.actorId));
            }
        }
        var player = PlayerState.Current;
        float credits = Math.Max(0f, result.credits - result.penaltyCredits) * ServerRules.CreditRewards * WorkshopOps.CreditsBoost();
        int experience = (int)(Math.Max(0, result.experience - result.penaltyExperience) * ServerRules.ExperienceRewards * WorkshopOps.ExperienceBoost());
        int oldLevel = player.level;
        player.experience += experience;
        player.level = Math.Max(oldLevel, global::Utils.GetLevelFromExperience(player.experience));

        var stats = player.lifetimeStatistics;
        stats.battlesParticipated++;
        if (result.winningTeam == LocalTeam)
        {
            stats.battlesWon++;
        }
        else if (result.winningTeam < 0)
        {
            stats.battlesDrawn++;
        }
        else
        {
            stats.battlesLost++;
        }
        if (result.survived)
        {
            stats.battlesSurvived++;
        }
        stats.kills += result.kills;
        stats.shots += result.shots;
        stats.hits += result.hits;
        stats.inflictedDamage += result.inflictedDamage;

        RevivalLog.Info($"Battle result: winningTeam={result.winningTeam} kills={result.kills} +{credits} credits +{experience} xp, level {oldLevel} -> {player.level}");
        LogCombatants();
        GameServer.AnnounceBattleRewards(credits, experience, oldLevel != player.level);
        CosmeticsOps.GrantBattleLoot(result, result.winningTeam == LocalTeam);
        PlayerState.Save();
    }

    // One line per robot from the emulator's private tally, for tuning bots and stats.
    private static void LogCombatants()
    {
        if (SessionBattle.Active && Session.Role == Session.Roles.Client) return; // the tally lives on the host
        var emulator = BattleManager.arena?.offlineServerEmulator;
        var field = typeof(OfflineServerEmulator).GetField("battleData", BindingFlags.NonPublic | BindingFlags.Instance);
        if (emulator == null || field == null || field.GetValue(emulator) is not Dictionary<int, BattleData> data) return;
        foreach (var actor in data)
        {
            var d = actor.Value;
            RevivalLog.Info($"  actor {actor.Key} team {d.team}: shots {d.shots}, hits {d.hits}, damage {(int)d.inflictedDamage}" +
                $", received {(int)d.receivedDamage}, kills {d.kills}{(d.survived ? ", survived" : ", destroyed")}");
        }
    }
}
