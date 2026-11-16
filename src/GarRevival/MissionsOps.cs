using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using JunkWars.Client.Arena;
using JunkWars.Client.Arena.Offline;
using JunkWars.Common.Arena;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Guild.Templates;
using JunkWars.Common.Missions;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival;

// Daily missions, achievements and weekend events. The client has one model for all: a definition
// (id, named targets, reward) plus a progress record (status, value per target), in categories.
// Achievements live in the four "Ach_*" categories, the picked daily mission in "DailySelectableMissions".
// Progress is kept in the profile and announced with event 147. The client has no claim op:
// a reward is paid when the mission completes.
internal static class MissionsOps
{
    private const byte OpTimedMissionAction = 243;
    private const byte OpGetMessageBoxData = 182;
    private const byte OpGetMissionData = 165;
    private const byte OpSelectableMissions = 164;
    private const byte OpGetMissionTemplateData = 148;

    private const byte EvCreditsUpdate = 220;
    private const byte EvMissionsProgressChanged = 147;
    private const byte EvSelectableMissionsInfoChanged = 146;

    private static readonly FieldInfo BattleDataField = typeof(OfflineServerEmulator).GetField("battleData", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Random Rng = new();
    private static Dictionary<string, int> _battleTally = new();

    public static bool Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        switch (op)
        {
            case OpGetMissionData:
                bool full = Param(request, 218) is not bool asked || asked;
                Hashtable timed;
                try
                {
                    timed = TimedData();
                }
                catch (Exception ex)
                {
                    // A failed answer here logs the player out.
                    RevivalLog.Error("Missions: timed mission data failed", ex);
                    timed = EmptyTimedData();
                }
                // true makes the client go on to ask for the message box (the login sequence).
                GameServer.Reply(conn, op, new() { { 231, timed }, { 218, full } });
                Guard("mission data follow-up", () =>
                {
                    if (full)
                    {
                        LadderOps.Announce(conn, 0);
                    }
                    else
                    {
                        // The garage repeats this every five minutes: a good moment to notice midnight.
                        Refresh();
                    }
                });
                return true;
            case OpSelectableMissions:
                try
                {
                    SelectableOperation(conn, op, request);
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Missions: daily mission request failed", ex);
                    GameServer.Fail(conn, op, "The daily missions are not available right now");
                }
                return true;
            case OpGetMissionTemplateData:
                try
                {
                    GameServer.Reply(conn, op, new() { { 218, DataSerialization.ProtoSerializeObjectHighCompressed(TimedTemplates()) } });
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Missions: timed mission templates failed", ex);
                    GameServer.Reply(conn, op);
                }
                return true;
            case OpTimedMissionAction:
                // Seasonal arena objects (pumpkins, presents...) exist only in the unused online arena.
                GameServer.Reply(conn, op);
                return true;
            case OpGetMessageBoxData:
                MessageBoxOps.Reply(conn, op);
                return true;
            default:
                return false;
        }
    }

    // Op 181 key 167. Also the once-per-login check: counts the login day and updates achievements;
    // anything changed since the profile was sent goes in newMissionProgress, so the client shows it.
    public static MissionInitData MissionInit()
    {
        try
        {
            MissionStore.NoteLogin(DateTime.Today);
            EnsureDailyOffer(false);
            var changed = SyncAchievements(null);
            PlayerState.Save();
            return new MissionInitData { missionDefinitions = Definitions(), newMissionProgress = changed };
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Missions: init data failed; starting without missions", ex);
            // The client needs a non-empty list here or its mission table stays null.
            return new MissionInitData { missionDefinitions = [new(MissionCatalog.DailyCategory)] };
        }
    }

    // Key 166: today's offer and which one was picked.
    public static SelectableMissionsInfo SelectableMissions()
    {
        try
        {
            EnsureDailyOffer(false);
            return PlayerState.Current.dailyMissionsInfo;
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Missions: daily offer failed", ex);
            return new SelectableMissionsInfo();
        }
    }

    // Key 165: the definitions of the offered missions.
    public static MissionDefinitionCategory MissionCategory()
    {
        try
        {
            EnsureDailyOffer(false);
            return OfferDefinitions(PlayerState.Current.dailyMissionsInfo);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Missions: daily definitions failed", ex);
            return new MissionDefinitionCategory(MissionCatalog.DailyCategory);
        }
    }

    // Call first thing in OfflineBattle.ApplyResult: on an event day this raises result.credits /
    // experience in place, so rewards and the end screen agree. Mission progress follows once
    // ApplyResult has added the battle to the profile, after the battle's own reward events.
    public static void OnBattleFinished(EndBattleData result, int localTeam)
    {
        if (result == null) return;
        Guard("battle result", () =>
        {
            var facts = Gather(result, localTeam);
            ApplyEvents(result);
            NoteLifetime(facts);
            var conn = Garage();
            if (conn == null)
            {
                AfterBattle(facts);
            }
            else
            {
                // Runs when the client next reads its queue, by which time ApplyResult is done.
                Loopback.Enqueue(conn, "mission progress", listener => Guard("mission progress", () => AfterBattle(facts)));
            }
        });
    }

    // Something a battle does not put in its result (a battery captured,
    // a bomb planted, a mine kill); counted until the battle ends.
    public static void NoteBattleEvent(string name, int amount = 1)
    {
        _battleTally.TryGetValue(name, out int value);
        _battleTally[name] = value + amount;
    }

    private static BattleFacts Gather(EndBattleData result, int localTeam)
    {
        var facts = new BattleFacts
        {
            Won = result.winningTeam == localTeam,
            Drawn = result.winningTeam < 0,
            Survived = result.survived,
            Kills = result.kills,
            Shots = result.shots,
            Damage = result.inflictedDamage,
            Accuracy = result.shots > 0 ? result.hits * 100f / result.shots : 0f,
            PlantedBomb = result.plantedBomb,
            DefusedBomb = result.defusedBomb,
            Mode = BattleManager.offlineBattleData?.gameMode ?? GameModeType.TeamDeadmach,
            Session = SessionBattle.Active,
            SessionHost = SessionBattle.Active && Session.Role == Session.Roles.Host,
            Extra = _battleTally,
        };
        _battleTally = new();

        // The emulator blanks destroyedModules in the local player's result before publishing it;
        // its own tally still has them. A session client gets them in the result and has no tally.
        var destroyed = result.destroyedModules;
        var tally = SessionBattle.Active && Session.Role == Session.Roles.Client ? null : EmulatorTally();
        var arena = BattleManager.arena;
        if (tally != null && arena != null && arena.possessedRobot != null && tally.TryGetValue(arena.possessedRobot.actorId, out var mine))
        {
            destroyed ??= mine.destroyedModules;
            int me = arena.possessedRobot.actorId;
            var mates = tally.Where(a => a.Key != me && a.Value.team == mine.team).Select(a => a.Value).ToList();
            facts.LastStanding = facts.Mode == GameModeType.TeamDeadmach && result.survived && result.kills > 0 && mates.Count > 0 && mates.All(m => !m.survived);
        }
        if (destroyed != null)
        {
            facts.Weapons = Count(destroyed, LogicalType.Weapon);
            facts.Heads = Count(destroyed, LogicalType.Head);
            facts.Chassis = Count(destroyed, LogicalType.Cassis);
            facts.Modules = destroyed.Values.Sum();
        }

        var player = PlayerState.Current;
        var robots = player.ownedRobotAssemblies;
        if (robots != null && player.selectedRobotIndex < robots.Length && robots[player.selectedRobotIndex].dockingPointToModuleLinks != null)
        {
            facts.RobotWeapons = robots[player.selectedRobotIndex].dockingPointToModuleLinks.Count(l =>
            {
                var module = PlayerState.FindModule(l.childModuleID);
                return module != null && module.type == LogicalType.Weapon;
            });
        }
        return facts;
    }

    private static int Count(Dictionary<LogicalType, int> destroyed, LogicalType type) => destroyed.TryGetValue(type, out int count) ? count : 0;

    private static Dictionary<int, BattleData> EmulatorTally()
    {
        var emulator = BattleManager.arena?.offlineServerEmulator;
        return emulator == null || BattleDataField == null ? null : BattleDataField.GetValue(emulator) as Dictionary<int, BattleData>;
    }

    private static void ApplyEvents(EndBattleData result)
    {
        foreach (var type in ActiveEvents(DateTime.Now))
        {
            if (type == TimedMisssionType.DoubleCredits)
            {
                result.credits *= MissionCatalog.EventMultiplier;
            }
            else if (type == TimedMisssionType.DoubleExperience)
            {
                result.experience = (int)(result.experience * MissionCatalog.EventMultiplier);
            }
            RevivalLog.Info($"Missions: event {type} applied to the battle rewards");
        }
    }

    // Lifetime figures the profile has fields for but ApplyResult does not keep.
    private static void NoteLifetime(BattleFacts facts)
    {
        var stats = PlayerState.Current.lifetimeStatistics ??= new();
        stats.destroyedWeapons += facts.Weapons;
        stats.destroyedHeads += facts.Heads;
        stats.destroyedChassis += facts.Chassis;
        stats.maxDamageInBattle = Math.Max(stats.maxDamageInBattle, facts.Damage);
        stats.maxKillsInBattle = Math.Max(stats.maxKillsInBattle, facts.Kills);
        if (!facts.Survived)
        {
            stats.deaths++;
        }
    }

    private static void AfterBattle(BattleFacts facts)
    {
        var player = PlayerState.Current;
        EnsureDailyOffer(true);
        if (facts.LastStanding)
        {
            MissionStore.Add("lastStanding", 1);
        }
        MissionStore.Raise("maxModulesInBattle", facts.Modules);
        // A session with other people is the nearest thing to a squad.
        if (facts.Session && facts.Won)
        {
            MissionStore.Add("squadWins", 1);
        }
        if (facts.SessionHost)
        {
            MissionStore.Raise("squadsCreated", 1);
            if (facts.Won)
            {
                MissionStore.Add("squadLeaderWins", 1);
            }
        }
        short leagueChange = LadderOps.Rate(player, facts);

        List<MissionProgress> changed = [];
        AdvanceDaily(facts, changed);
        changed.AddRange(SyncAchievements(facts));
        PlayerState.Save();
        string outcome = facts.Won ? "won" : facts.Drawn ? "drawn" : "lost";
        RevivalLog.Info($"Missions: battle counted ({outcome}, {facts.Mode}, kills {facts.Kills}, damage {(int)facts.Damage}, accuracy {(int)facts.Accuracy}%, " +
            $"modules {facts.Modules}); {changed.Count} mission(s) moved, ladder {(int)player.ladderRating} league {LadderOps.League(player)}");
        PushProgress(changed, null);
        LadderOps.Announce(Garage(), leagueChange);
        SocialOps.AnnounceLadder();
    }

    private static void AdvanceDaily(BattleFacts facts, List<MissionProgress> changed)
    {
        string id = SelectedId(PlayerState.Current.dailyMissionsInfo);
        var daily = MissionCatalog.ParseDaily(id);
        var progress = daily == null ? null : FindProgress(MissionCatalog.DailyCategory, id);
        if (progress == null || progress.status != MissionStatus.InProgress) return;
        float gain = daily.Spec.Gain(facts, daily.Spec.Extras(daily.Tier));
        if (gain <= 0f) return;
        int goal = daily.Spec.Goals[daily.Tier];
        double value = Math.Min(goal, Number(progress, daily.Spec.Target) + gain);
        SetValue(progress, daily.Spec.Target, value);
        if (value >= goal)
        {
            Complete(progress, MissionCatalog.DailyDefinition(id).rewardPackage, "daily mission " + id);
        }
        changed.Add(progress);
    }

    // Adds to a counter an achievement watches: partsBought, accessoriesBought, portraitsBought,
    // creditsSpent, squadsCreated, squadWins, squadLeaderWins, lastStanding, or "ach.<Name>" for
    // battle feats nothing measures (ach.LastStand, ach.MVP, ...).
    public static void Report(string counter, double amount = 1)
    {
        Guard("report " + counter, () =>
        {
            MissionStore.Add(counter, amount);
            Refresh();
        });
    }

    // For the shop's buy operation, after the purchase went through.
    public static void OnItemBought(string templateId, ItemType type, float price)
    {
        Guard("item bought", () =>
        {
            if (price > 0f)
            {
                MissionStore.Add("creditsSpent", price);
            }
            PartTemplate part = null;
            if (type == ItemType.Module && Catalog.Modules != null && Catalog.Modules.TryGetValue(templateId, out var module))
            {
                Catalog.Parts.TryGetValue(module.moduleCaseIndent, out part);
            }
            else if (type == ItemType.Part && Catalog.Parts != null)
            {
                Catalog.Parts.TryGetValue(templateId, out part);
            }
            MissionStore.Add(part != null && IsAccessory(part.type) ? "accessoriesBought" : "partsBought", 1);
            Refresh();
        });
    }

    // For anything else that costs credits (upgrades, repairs, ...).
    public static void OnCreditsSpent(float amount)
    {
        if (amount > 0f)
        {
            Report("creditsSpent", amount);
        }
    }

    private static bool IsAccessory(LogicalType type) => type is LogicalType.Hat or LogicalType.HatCase or LogicalType.Eyewear
        or LogicalType.EyewearCase or LogicalType.Mouthwear or LogicalType.MouthwearCase;

    // Re-reads everything achievements depend on (level, credits, friends, robot names, counters),
    // rolls the daily offer over after midnight, and tells the client what changed.
    public static void Refresh()
    {
        Guard("refresh", () =>
        {
            EnsureDailyOffer(true);
            var changed = SyncAchievements(null);
            if (changed.Count > 0)
            {
                PlayerState.Save();
                PushProgress(changed, null);
            }
        });
    }

    private static void SelectableOperation(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        // If the day has just changed the request was made looking at yesterday's offer;
        // show today's instead of acting on the wrong mission.
        bool newDay = EnsureDailyOffer(false);
        var player = PlayerState.Current;
        var info = player.dailyMissionsInfo;
        object kind = Param(request, 168);
        int operation = kind == null ? -1 : Convert.ToInt32(kind, CultureInfo.InvariantCulture);
        string error = null;
        if (newDay)
        {
            error = "A new day has started. Here are today's missions";
        }
        else if (operation == (int)SelectableMissionsOperationEnum.RefreshMissions)
        {
            int cost = CommonConstants.DailyMissionsRefreshCredits;
            if (info.selectedMissionIndex != -1)
            {
                error = "Today's mission has already been chosen";
            }
            else if (player.credits < cost)
            {
                error = "Not enough credits";
            }
            else
            {
                GameServer.ChangeCredits(conn, -cost, ItemAddedReason.Buy);
                MissionStore.Add("creditsSpent", cost);
                player.dailyMissionsInfo = info = NewOffer(DateTime.Today, info.missionIds);
                PlayerState.Save();
                RevivalLog.Info("Missions: new daily offer bought: " + string.Join(", ", info.missionIds.ToArray()));
            }
        }
        else if (operation == (int)SelectableMissionsOperationEnum.SelectMission)
        {
            if (info.selectedMissionIndex != -1)
            {
                error = "Today's mission has already been chosen";
            }
            else if (!int.TryParse(Param(request, 167) as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index < 0 || index >= info.missionIds.Count)
            {
                error = "No such daily mission";
            }
            else
            {
                info.selectedMissionIndex = index;
                // The client shows selectDateTime + its reset hours as the time left; that is midnight.
                info.selectDateTime = info.initDateTime;
                string id = info.missionIds[index];
                var progress = StartDaily(id);
                PlayerState.Save();
                RevivalLog.Info($"Missions: daily mission {id} selected");
                // Before the reply: the client's list looks the mission up when the reply arrives.
                var started = new MissionDefinitionCategory(MissionCatalog.DailyCategory);
                started.missionDefinitions.Add(MissionCatalog.DailyDefinition(id));
                PushProgress([progress], [started]);
            }
        }
        else
        {
            error = "Unknown daily mission request";
        }
        if (error != null)
        {
            GameServer.Fail(conn, op, error);
        }
        // Sent after a failure too: only a successful answer clears the client's "request in progress"
        // flag, which otherwise blocks the Accept buttons until the next login.
        GameServer.Reply(conn, op, new()
        {
            { 168, DataSerialization.ProtoSerializeObject(info) },
            { 167, DataSerialization.ProtoSerializeObject(OfferDefinitions(info)) },
        });
    }

    // Makes sure the profile holds an offer for today; true when a new one had to be made.
    // Yesterday's mission, finished or not, goes with yesterday's offer.
    private static bool EnsureDailyOffer(bool announce)
    {
        var player = PlayerState.Current;
        var info = player.dailyMissionsInfo;
        var today = DateTime.Today;
        bool current = info != null && info.missionIds != null && info.missionIds.Count > 0 && info.initDateTime.Date == today &&
            info.missionIds.All(id => MissionCatalog.ParseDaily(id) != null) && info.selectedMissionIndex < info.missionIds.Count;
        if (current)
        {
            // A picked mission must have a progress record: the arena HUD assumes the client knows it.
            string selected = SelectedId(info);
            if (selected != null && FindProgress(MissionCatalog.DailyCategory, selected) == null)
            {
                StartDaily(selected);
            }
            return false;
        }
        player.dailyMissionsInfo = info = NewOffer(today, info?.missionIds);
        ProgressCategory(MissionCatalog.DailyCategory).missionsProgress.Clear();
        PlayerState.Save();
        RevivalLog.Info($"Missions: daily offer for {today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}: " + string.Join(", ", info.missionIds.ToArray()));
        var conn = announce ? Garage() : null;
        if (conn != null)
        {
            GameServer.Event(conn, EvSelectableMissionsInfoChanged, new()
            {
                { 168, DataSerialization.ProtoSerializeObject(info) },
                { 167, DataSerialization.ProtoSerializeObject(OfferDefinitions(info)) },
            });
        }
        return true;
    }

    // An easy, a medium and a hard mission of three different kinds,
    // avoiding the kinds of the offer being replaced when possible.
    private static SelectableMissionsInfo NewOffer(DateTime today, List<string> replacing)
    {
        var avoid = new HashSet<string>((replacing ?? []).Select(MissionCatalog.ParseDaily).Where(d => d != null).Select(d => d.Spec.Key));
        var pool = MissionCatalog.Dailies.Where(d => d.Offered).ToList();
        var fresh = pool.Where(d => !avoid.Contains(d.Key)).ToList();
        if (fresh.Count >= MissionCatalog.DailyOffers)
        {
            pool = fresh;
        }
        var info = new SelectableMissionsInfo
        {
            // The client counts down to initDateTime + its reset hours; make that the coming midnight.
            initDateTime = today.AddDays(1).AddHours(-CommonConstants.DailyMissionsResetHours),
        };
        for (int tier = 0; tier < MissionCatalog.DailyOffers && pool.Count > 0; tier++)
        {
            var spec = pool[Rng.Next(pool.Count)];
            pool.Remove(spec);
            info.missionIds.Add(MissionCatalog.DailyId(spec, today, Math.Min(tier, spec.Goals.Length - 1)));
        }
        return info;
    }

    private static MissionProgress StartDaily(string id)
    {
        var daily = MissionCatalog.ParseDaily(id);
        var category = ProgressCategory(MissionCatalog.DailyCategory);
        category.missionsProgress.Clear();
        var progress = new MissionProgress(id, MissionStatus.InProgress, new[] { new MissionProgress.ProgressValue(daily.Spec.Target, 0) });
        category.missionsProgress.Add(progress);
        return progress;
    }

    private static string SelectedId(SelectableMissionsInfo info) =>
        info?.missionIds != null && info.selectedMissionIndex >= 0 && info.selectedMissionIndex < info.missionIds.Count
            ? info.missionIds[info.selectedMissionIndex] : null;

    private static MissionDefinitionCategory OfferDefinitions(SelectableMissionsInfo info)
    {
        var category = new MissionDefinitionCategory(MissionCatalog.DailyCategory);
        category.missionDefinitions.AddRange(info.missionIds.Select(MissionCatalog.DailyDefinition).Where(d => d != null));
        return category;
    }

    public static int CompletedAchievements() =>
        (PlayerState.Current.missionProgress ?? []).Where(c => c != null && c.missionsProgress != null && MissionCatalog.AchCategories.Contains(c.categoryId))
            .Sum(c => c.missionsProgress.Count(p => p.status == MissionStatus.Completed));

    // Brings every unfinished achievement up to date and returns the records that changed.
    // Feats are judged only when a battle is given.
    private static List<MissionProgress> SyncAchievements(BattleFacts battle)
    {
        List<MissionProgress> changed = [];
        // Completing one can complete another ("complete 25 achievements").
        for (int pass = 0; pass < 3; pass++)
        {
            bool completedAny = false;
            foreach (var spec in MissionCatalog.Achievements)
            {
                bool moved = false;
                var progress = FindProgress(spec.Category, spec.Id);
                if (progress == null)
                {
                    progress = new MissionProgress(spec.Id, MissionStatus.InProgress, null);
                    ProgressCategory(spec.Category).missionsProgress.Add(progress);
                    moved = true;
                }
                if (progress.status == MissionStatus.Completed) continue;
                double value = spec.Value != null ? spec.Value() : battle != null && spec.Feat(battle) ? spec.Goal : 0;
                if (spec.Bar)
                {
                    moved |= SetValue(progress, MissionCatalog.AchTarget, Math.Min(value, spec.Goal));
                }
                if (value >= spec.Goal)
                {
                    Complete(progress, MissionCatalog.AchievementDefinition(spec).rewardPackage, "achievement " + spec.Id);
                    moved = true;
                    completedAny = true;
                }
                if (moved && !changed.Contains(progress))
                {
                    changed.Add(progress);
                }
            }
            if (!completedAny) break;
        }
        return changed;
    }

    // All achievements, plus the picked daily mission: the client only
    // knows a daily mission as "in progress" once it has its definition.
    private static List<MissionDefinitionCategory> Definitions()
    {
        var categories = MissionCatalog.AchCategories.Select(id => new MissionDefinitionCategory(id)).ToList();
        foreach (var spec in MissionCatalog.Achievements)
        {
            categories.First(c => c.categoryId == spec.Category).missionDefinitions.Add(MissionCatalog.AchievementDefinition(spec));
        }
        string selected = SelectedId(PlayerState.Current.dailyMissionsInfo);
        if (selected != null)
        {
            var daily = new MissionDefinitionCategory(MissionCatalog.DailyCategory);
            daily.missionDefinitions.Add(MissionCatalog.DailyDefinition(selected));
            categories.Add(daily);
        }
        return categories;
    }

    private static IEnumerable<TimedMisssionType> ActiveEvents(DateTime now) =>
        MissionCatalog.WeekendEvents.Where(e => e.Key == now.DayOfWeek).Select(e => e.Value);

    // What TimedMissionManager reads: [1] = action, [5] = identifier -> { 0 type, 1 start, 2 end,
    // 8 complete }, with exactly these key and value types. Always the full list, so a finished event disappears.
    private static Hashtable TimedData()
    {
        var missions = new Hashtable();
        var start = DateTime.Today;
        foreach (var type in ActiveEvents(DateTime.Now))
        {
            missions[type.ToString()] = new Hashtable
            {
                { 0, (int)type },
                { 1, start.ToBinary() },
                { 2, start.AddDays(1).ToBinary() },
                { 8, false },
            };
        }
        var data = EmptyTimedData();
        data[(byte)TimedMissionDataFields.Data] = missions;
        return data;
    }

    private static Hashtable EmptyTimedData() => new()
    {
        { (byte)TimedMissionDataFields.Action, (byte)TimedMissionAction.FullUpdate },
        { (byte)TimedMissionDataFields.Data, new Hashtable() },
    };

    // Keyed like the missions above; the client only looks up rewards here, and these events have none.
    private static Dictionary<string, TimedMissionTemplate> TimedTemplates()
    {
        var year = new DateTime(DateTime.Today.Year, 1, 1);
        return MissionCatalog.WeekendEvents.ToDictionary(e => e.Value.ToString(), e => new TimedMissionTemplate
        {
            Indent = e.Value.ToString(),
            Type = e.Value,
            StartDate = year,
            EndDate = year.AddYears(1),
            ActiveWeekDays = [e.Key],
        });
    }

    private static MissionProgressCategory ProgressCategory(string id)
    {
        var player = PlayerState.Current;
        player.missionProgress ??= [];
        var category = player.missionProgress.FirstOrDefault(c => c != null && c.categoryId == id);
        if (category == null)
        {
            category = new MissionProgressCategory(id);
            player.missionProgress.Add(category);
        }
        category.missionsProgress ??= [];
        return category;
    }

    private static MissionProgress FindProgress(string category, string missionId) =>
        ProgressCategory(category).missionsProgress.FirstOrDefault(p => p != null && p.missionId == missionId);

    private static double Number(MissionProgress progress, string name)
    {
        var value = progress.GetProgressValue(name);
        return value != null && double.TryParse(value.value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ? number : 0;
    }

    // Whole numbers only: the client rounds them for display anyway.
    private static bool SetValue(MissionProgress progress, string name, double number)
    {
        string text = Math.Floor(number).ToString("0", CultureInfo.InvariantCulture);
        progress.progressValues ??= [];
        var value = progress.GetProgressValue(name);
        if (value == null)
        {
            progress.progressValues.Add(new MissionProgress.ProgressValue(name, text));
            return true;
        }
        if (value.value == text) return false;
        value.value = text;
        return true;
    }

    private static void Complete(MissionProgress progress, Package reward, string what)
    {
        progress.status = MissionStatus.Completed;
        progress.completeDateTime = DateTime.Now;
        float credits = reward.GetTotalCredits();
        float sparks = reward.GetTotalSparks();
        RevivalLog.Info($"Missions: {what} completed, reward {credits} credits, {sparks} sparks");
        if (credits <= 0f && sparks <= 0f) return;
        var player = PlayerState.Current;
        player.credits += credits;
        player.premiumCredits += sparks;
        var conn = Garage();
        if (conn != null)
        {
            // Not EndGameLoot: that reason would replace the battle's own loot on the end screen.
            GameServer.Event(conn, EvCreditsUpdate, new()
            {
                { 213, player.credits },
                { 214, credits },
                { 234, player.premiumCredits },
                { 218, sparks },
                { 168, (byte)ItemAddedReason.TimedMissionReward },
            });
        }
    }

    private static void PushProgress(List<MissionProgress> changed, List<MissionDefinitionCategory> newDefinitions)
    {
        var conn = Garage();
        if (changed.Count == 0 || conn == null) return;
        GameServer.Event(conn, EvMissionsProgressChanged, new()
        {
            { 168, DataSerialization.ProtoSerializeObject(new MissionProgressChangeData { missionProgress = changed, newMissionsDefinitions = newDefinitions }) },
        });
    }

    // The garage connection, when the client is logged in. Mission events
    // go there whatever screen the game is on.
    private static Loopback.VirtualConnection Garage()
    {
        var peer = PhotonNetwork.networkingPeer;
        return peer != null && Loopback.StateOf(peer) == PeerStateValue.Connected ? Loopback.Get(peer) : null;
    }

    private static object Param(Dictionary<byte, object> request, byte key) =>
        request != null && request.TryGetValue(key, out var value) ? value : null;

    // Missions must never take a purchase or a battle result down with them.
    private static void Guard(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Missions: {what} failed", ex);
        }
    }
}
