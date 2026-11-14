using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using JunkWars.Common.Arena;
using JunkWars.Common.Missions;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;

namespace GarRevival
{
    // What one finished battle tells the missions.
    internal sealed class BattleFacts
    {
        public bool Won;
        public bool Drawn;
        public bool Survived;
        public bool LastStanding;   // deathmatch: every team mate destroyed, I was not
        public bool Session;        // played with other people
        public bool SessionHost;
        public GameModeType Mode = GameModeType.TeamDeadmach;
        public int Kills;
        public int Shots;
        public float Damage;
        public float Accuracy;      // percent
        public int Weapons;         // enemy modules destroyed, by kind
        public int Heads;
        public int Chassis;
        public int Modules;
        public int RobotWeapons;    // weapons on the robot I fought with
        public bool PlantedBomb;
        public bool DefusedBomb;
        public Dictionary<string, int> Extra = new Dictionary<string, int>();

        public int Tally(string name)
        {
            int value;
            return Extra.TryGetValue(name, out value) ? value : 0;
        }
    }

    // One kind of daily mission. The key is the client's localization key
    // ("<key>_Name", "<key>_Description") and icon name; the target names are
    // the {placeholders} in its description text.
    internal sealed class DailySpec
    {
        public string Key;
        public string Target;
        public int[] Goals;
        public string[] ExtraNames = new string[0];
        public int[][] ExtraValues = new int[0][];
        // (battle, this tier's extra values) -> progress gained
        public Func<BattleFacts, int[], float> Gain;
        public bool Offered = true;

        public int[] Extras(int tier)
        {
            return tier < ExtraValues.Length ? ExtraValues[tier] : new int[0];
        }
    }

    internal sealed class DailyRef
    {
        public DailySpec Spec;
        public int Tier;
        public DateTime Day;
    }

    // One achievement. Value is a standing figure (level, lifetime kills, a
    // counter); Feat is something a single battle either did or did not do.
    internal sealed class AchSpec
    {
        public string Id;
        public string Category;
        public double Goal = 1;
        public int Points;
        public bool Bar;
        public Func<double> Value;
        public Func<BattleFacts, bool> Feat;
    }

    // Every number here is designed: the originals lived on the server. The
    // mission and achievement names, descriptions and placeholders are the
    // client's own (its localization table).
    internal static class MissionCatalog
    {
        public const string DailyCategory = "DailySelectableMissions";
        public const string AchProgression = "Ach_Progression";
        public const string AchBattle = "Ach_Battle";
        public const string AchSocial = "Ach_Social";
        public const string AchMisc = "Ach_Misc";
        public static readonly string[] AchCategories = { AchProgression, AchBattle, AchSocial, AchMisc };

        // The progress figure of an achievement with a bar.
        public const string AchTarget = "Target";

        // ---- daily missions ----

        // Three are offered each day: an easy, a medium and a hard one.
        public const int DailyOffers = 3;
        public static readonly int[] DailyCredits = { 1500, 3000, 5000 };
        public static readonly int[] DailySparks = { 0, 5, 10 };

        public static readonly DailySpec[] Dailies =
        {
            new DailySpec { Key = "Daily_Killer", Target = "EnemyCount", Goals = new[] { 5, 10, 15 }, Gain = (b, x) => b.Kills },
            new DailySpec { Key = "Daily_Domination", Target = "GamesCount", Goals = new[] { 2, 3, 5 }, Gain = (b, x) => b.Won ? 1 : 0 },
            new DailySpec { Key = "Daily_Assault", Target = "TDMGamesCount", Goals = new[] { 1, 2, 3 }, Gain = (b, x) => b.Won && b.Mode == GameModeType.TeamDeadmach ? 1 : 0 },
            new DailySpec { Key = "Daily_Sabotage", Target = "CTBGamesCount", Goals = new[] { 1, 2, 3 }, Gain = (b, x) => b.Won && b.Mode == GameModeType.CaptureTheFlag ? 1 : 0 },
            new DailySpec { Key = "Daily_Demolition", Target = "BSGamesCount", Goals = new[] { 1, 2, 3 }, Gain = (b, x) => b.Won && b.Mode == GameModeType.DestoryBase ? 1 : 0 },
            new DailySpec { Key = "Daily_Punisher", Target = "DamageNumber", Goals = new[] { 5000, 10000, 15000 }, Gain = (b, x) => b.Damage },
            new DailySpec
            {
                Key = "Daily_DoubleKill", Target = "MultiKillGamesCount", Goals = new[] { 1, 2, 3 },
                ExtraNames = new[] { "KillsCount" }, ExtraValues = new[] { new[] { 2 }, new[] { 2 }, new[] { 3 } },
                Gain = (b, x) => b.Kills >= x[0] ? 1 : 0,
            },
            new DailySpec
            {
                Key = "Daily_Marksman", Target = "GamesCount", Goals = new[] { 1, 2, 3 },
                ExtraNames = new[] { "TargetAccuracyPercent", "KillsCount" },
                ExtraValues = new[] { new[] { 35, 1 }, new[] { 40, 1 }, new[] { 45, 2 } },
                Gain = (b, x) => b.Accuracy >= x[0] && b.Kills >= x[1] ? 1 : 0,
            },
            new DailySpec { Key = "Daily_Peacekeeper", Target = "ModulesCount", Goals = new[] { 3, 5, 8 }, Gain = (b, x) => b.Weapons },
            new DailySpec { Key = "Daily_BountyHunter", Target = "ModulesCount", Goals = new[] { 2, 3, 5 }, Gain = (b, x) => b.Heads },
            new DailySpec { Key = "Daily_Crippler", Target = "ModulesCount", Goals = new[] { 2, 3, 5 }, Gain = (b, x) => b.Chassis },
            // Not offered: battles do not report battery captures or bomb
            // plants per player yet (MissionsOps.NoteBattleEvent feeds them).
            new DailySpec { Key = "Daily_Rogue", Target = "CapturedBatteriesCount", Goals = new[] { 1, 2, 3 }, Gain = (b, x) => b.Tally("CapturedBatteries"), Offered = false },
            new DailySpec { Key = "Daily_Subversion", Target = "PlantedBombsCount", Goals = new[] { 1, 2, 3 }, Gain = (b, x) => Math.Max(b.Tally("PlantedBombs"), b.PlantedBomb ? 1 : 0), Offered = false },
        };

        // "Daily_Killer_20261113t2": the client strips the last "_..." to get
        // the localization key, so the day and tier ride in the suffix and
        // the definition can be rebuilt from the id alone.
        public static string DailyId(DailySpec spec, DateTime day, int tier)
        {
            return spec.Key + "_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "t" + tier;
        }

        public static DailyRef ParseDaily(string id)
        {
            int cut = id == null ? -1 : id.LastIndexOf('_');
            if (cut < 0 || id.Length - cut != 11 || id[cut + 9] != 't')
            {
                return null;
            }
            string key = id.Substring(0, cut);
            DailySpec spec = Dailies.FirstOrDefault(d => d.Key == key);
            DateTime day;
            int tier = id[cut + 10] - '0';
            if (spec == null || tier < 0 || tier >= spec.Goals.Length ||
                !DateTime.TryParseExact(id.Substring(cut + 1, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
            {
                return null;
            }
            return new DailyRef { Spec = spec, Tier = tier, Day = day };
        }

        public static MissionDefinition DailyDefinition(string id)
        {
            DailyRef daily = ParseDaily(id);
            if (daily == null)
            {
                return null;
            }
            MissionDefinition def = NewDefinition(id, "Daily");
            // The progress figure first: the UI shows the first target that
            // has a value. The rest only fill the description's placeholders.
            def.targetValues.Add(Pair(daily.Spec.Target, daily.Spec.Goals[daily.Tier]));
            for (int i = 0; i < daily.Spec.ExtraNames.Length; i++)
            {
                def.targetValues.Add(Pair(daily.Spec.ExtraNames[i], daily.Spec.Extras(daily.Tier)[i]));
            }
            def.rewardPackage = Reward(DailyCredits[daily.Tier], DailySparks[daily.Tier], 0);
            return def;
        }

        // ---- achievements ----

        // Credits paid with an achievement, by its points.
        public static int AchievementCredits(int points)
        {
            return points >= 50 ? 50000 : points >= 25 ? 20000 : points >= 20 ? 10000 : points >= 15 ? 5000 : points >= 10 ? 2500 : 1000;
        }

        private static List<AchSpec> _achievements;

        public static List<AchSpec> Achievements
        {
            get { return _achievements ?? (_achievements = BuildAchievements()); }
        }

        public static MissionDefinition AchievementDefinition(AchSpec spec)
        {
            MissionDefinition def = NewDefinition(spec.Id, "Achievement");
            if (spec.Bar)
            {
                def.targetValues.Add(Pair(AchTarget, (int)spec.Goal));
            }
            def.rewardPackage = Reward(AchievementCredits(spec.Points), 0, spec.Points);
            return def;
        }

        private static List<AchSpec> BuildAchievements()
        {
            List<AchSpec> list = new List<AchSpec>();
            Func<double> level = () => PlayerState.Current.level;
            Func<double> games = () => Stats.battlesParticipated;
            Func<double> wins = () => Stats.battlesWon;
            Func<double> kills = () => Stats.kills;
            Func<double> friends = () => PlayerState.Current.friendList == null ? 0 : PlayerState.Current.friendList.Count;
            Func<double> done = () => MissionsOps.CompletedAchievements();
            Func<double> league = () => LadderOps.League(PlayerState.Current) + 1;

            // Progress
            Count(list, AchProgression, level, "Greenhorn", 2, 5, "MagnificentSeven", 7, 10, "ComingOfAge", 20, 15, "Veteran", 35, 20, "OldDog", 55, 25, "LegendOftheWest", 80, 50);
            Count(list, AchProgression, games, "RideIntoTown", 20, 5, "StickingAround", 100, 10, "FortheLoveOftheGame", 500, 15, "FaithfulFan", 1000, 20, "GARFanatic", 2500, 25);
            Count(list, AchProgression, wins, "Pretender", 10, 5, "RisingUnderdog", 100, 10, "TitleContender", 500, 15, "TitleHolder", 1000, 20, "UndisputedChampion", 2000, 25);
            Count(list, AchProgression, Counter("partsBought"), "FromSmallBeginnings", 1, 5, "BePrepared", 5, 5, "HomeArsenal", 10, 10, "ParaBellum", 20, 15);
            Count(list, AchProgression, () => PlayerState.Current.credits, "Thrifty", 20000, 5);
            Count(list, AchProgression, Counter("creditsSpent"), "LethalBlind", 10000, 5, "RaisingTheStakes", 50000, 10, "CallingTheShots", 150000, 15, "AllIn", 500000, 20);
            Count(list, AchProgression, OwnedPaidPartTypes, "Completionist", Math.Max(1, PaidPartTypes().Count), 25);
            // "Omnipotent" (100 achievements) is left out: fewer than 100 exist.
            Count(list, AchProgression, done, "Achiever", 25, 10, "Over-achiever", 50, 15, "Robot", 75, 25);
            foreach (AchSpec spec in Count(list, AchProgression, league, "RecruitLeague", 1, 5, "ExperiencedLeague", 2, 10, "VeteranLeague", 3, 15, "TerminatorLeague", 4, 20, "MasterLeague", 5, 25))
            {
                spec.Bar = false;
            }

            // Battle
            Count(list, AchBattle, () => Stats.maxDamageInBattle, "SonOfaGun", 2500, 10, "MobileArtillery", 5000, 20);
            Feat(list, AchBattle, "Deadeye", 10, b => b.Accuracy >= 55f && b.Kills >= 1);
            Feat(list, AchBattle, "Assassin", 15, b => b.Accuracy >= 60f && b.Kills >= 2);
            Feat(list, AchBattle, "GrimReaper", 20, b => b.Accuracy >= 65f && b.Kills >= 3);
            Feat(list, AchBattle, "ShootFromtheHip", 10, b => b.Kills >= 3 && b.Accuracy < 30f);
            Count(list, AchBattle, kills, "FirstBlood", 10, 5, "OpenSeason", 50, 5, "CheaperBytheDozen", 120, 10, "ScrapDiscount", 250, 10, "FireSale", 500, 15, "BlackFriday", 750, 20, "AllRobotsMustGo", 1200, 25);
            // Kills in one battle stand in for "players of the opposing team".
            Count(list, AchBattle, () => Stats.maxKillsInBattle, "Terminator", 3, 10, "Hunter-Killer", 4, 15, "LivingApokalypse", 5, 20);
            Count(list, AchBattle, Counter("lastStanding"), "LastBotStanding", 1, 10, "Survivor", 10, 15, "Cockroach", 25, 20, "Deathproof", 50, 25);
            Count(list, AchBattle, Counter("maxModulesInBattle"), "Disassembler", 10, 15);
            Feat(list, AchBattle, "Pacifist", 15, b => b.Won && b.Shots == 0);
            Feat(list, AchBattle, "Saboteur", 15, b => b.Won && b.Mode == GameModeType.DestoryBase && (b.PlantedBomb || b.Tally("PlantedBombs") > 0));
            Feat(list, AchBattle, "BombDisposal", 15, b => b.Won && b.Mode == GameModeType.DestoryBase && (b.DefusedBomb || b.Tally("DefusedBombs") > 0));
            Feat(list, AchBattle, "MasterSapper", 25, b => b.Won && b.Mode == GameModeType.DestoryBase && (b.PlantedBomb || b.Tally("PlantedBombs") > 0) && (b.DefusedBomb || b.Tally("DefusedBombs") > 0));
            // Nothing reports these yet; MissionsOps.Report("ach.<name>") completes them.
            Untracked(list, AchBattle, "LastStand", 1, 10, "Turret", 2, 15, "PlayingOnInstinct", 1, 10, "BlindFury", 3, 20, "RoadRunner", 1, 15,
                "MVP", 1, 20, "DramaticTension", 1, 15, "Vivisectionist", 1, 15, "ToyingWithYourPrey", 1, 25, "WasteNotWantNot", 1, 10);

            // Social
            Count(list, AchSocial, friends, "Sociable", 3, 5, "Recruiter", 10, 10, "RingLeader", 20, 15);
            Count(list, AchSocial, Counter("squadsCreated"), "CallToArms", 1, 5);
            Count(list, AchSocial, Counter("squadWins"), "TeamPlayer", 1, 5, "StrenghtInNumbers", 10, 15);
            Count(list, AchSocial, Counter("squadLeaderWins"), "Warlord", 50, 25);

            // Misc
            Count(list, AchMisc, Counter("portraitsBought"), "DorianGray", 1, 5, "ShapeShifter", 3, 10);
            Count(list, AchMisc, Counter("accessoriesBought"), "Vain", 1, 5, "MirrorMirror", 3, 10);
            Count(list, AchMisc, () => HasRenamedRobot() ? 1 : 0, "GiveNametoTheirTerror", 1, 5);
            Feat(list, AchMisc, "AvatarOfDeath", 15, b => b.RobotWeapons >= 4);
            Count(list, AchMisc, () => MissionStore.BestLoginWindow(7), "BusyWeek", 5, 5);
            Count(list, AchMisc, () => MissionStore.BestLoginWindow(30), "DoubleDuty", 20, 10);
            Count(list, AchMisc, () => MissionStore.BestLoginWindow(61), "CampaignOfWar", 35, 15);
            Count(list, AchMisc, () => MissionStore.BestLoginWindow(92), "EternalVigil", 45, 25);
            return list;
        }

        private static LifetimeStatisticsVO Stats
        {
            get { return PlayerState.Current.lifetimeStatistics ?? (PlayerState.Current.lifetimeStatistics = new LifetimeStatisticsVO()); }
        }

        private static Func<double> Counter(string name)
        {
            return () => MissionStore.Get(name);
        }

        // rows: name, goal, points, name, goal, points, ...
        private static List<AchSpec> Count(List<AchSpec> list, string category, Func<double> value, params object[] rows)
        {
            List<AchSpec> added = new List<AchSpec>();
            for (int i = 0; i + 2 < rows.Length; i += 3)
            {
                double goal = Convert.ToDouble(rows[i + 1], CultureInfo.InvariantCulture);
                added.Add(new AchSpec { Id = "Ach_" + rows[i], Category = category, Goal = goal, Points = (int)rows[i + 2], Value = value, Bar = goal > 1 });
            }
            list.AddRange(added);
            return added;
        }

        private static void Untracked(List<AchSpec> list, string category, params object[] rows)
        {
            for (int i = 0; i + 2 < rows.Length; i += 3)
            {
                Count(list, category, Counter("ach." + rows[i]), rows[i], rows[i + 1], rows[i + 2]);
            }
        }

        private static void Feat(List<AchSpec> list, string category, string name, int points, Func<BattleFacts, bool> feat)
        {
            list.Add(new AchSpec { Id = "Ach_" + name, Category = category, Points = points, Feat = feat });
        }

        // New robots are called "Robot <id>"; the starter is "Starter".
        private static bool HasRenamedRobot()
        {
            RobotAssemblyVO[] robots = PlayerState.Current.ownedRobotAssemblies ?? new RobotAssemblyVO[0];
            return robots.Any(r => r != null && !string.IsNullOrEmpty(r.assemblyName) && r.assemblyName != "Starter" && r.assemblyName != "Robot " + r.assemblyID);
        }

        private static HashSet<LogicalType> PaidPartTypes()
        {
            HashSet<LogicalType> types = new HashSet<LogicalType>();
            try
            {
                Catalog.EnsureBuilt();
                foreach (PartTemplate part in Catalog.Parts.Values)
                {
                    if (part.purchasable && Catalog.BuyPrice(part.indent, 0) > 0f)
                    {
                        types.Add(part.type);
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Missions: could not read the part catalog", ex);
            }
            return types;
        }

        private static double OwnedPaidPartTypes()
        {
            HashSet<LogicalType> wanted = PaidPartTypes();
            HashSet<LogicalType> owned = new HashSet<LogicalType>();
            foreach (PartVO part in PlayerState.Current.ownedParts ?? new List<PartVO>())
            {
                if (wanted.Contains(part.type) && Catalog.BuyPrice(part.indent, 0) > 0f)
                {
                    owned.Add(part.type);
                }
            }
            return owned.Count;
        }

        // ---- ladder (local: there is nobody else to rank against) ----

        // Battles before a league is assigned.
        public const int LadderPlacementBattles = 5;
        public const double LadderWin = 25;
        public const double LadderDraw = 5;
        public const double LadderLoss = -10;
        public const double LadderPerKill = 2;
        public const double LadderKillCap = 10;
        // Points at which Recruit, Experienced, Veteran, Terminator and Master begin.
        public static readonly double[] LeagueFloors = { 0, 300, 800, 1600, 3000 };
        // Master has no ceiling; this many points span its 100 positions.
        public const double MasterSpan = 5000;

        // ---- weekend events (the client's "timed missions") ----

        public const float EventMultiplier = 2f;
        public static readonly KeyValuePair<DayOfWeek, TimedMisssionType>[] WeekendEvents =
        {
            new KeyValuePair<DayOfWeek, TimedMisssionType>(DayOfWeek.Saturday, TimedMisssionType.DoubleExperience),
            new KeyValuePair<DayOfWeek, TimedMisssionType>(DayOfWeek.Sunday, TimedMisssionType.DoubleCredits),
        };

        // ---- building blocks ----

        // MissionDefinition's constructor is protected, and a subclass would
        // not serialize, so create the real type directly.
        private static MissionDefinition NewDefinition(string id, string typeName)
        {
            MissionDefinition def = (MissionDefinition)Activator.CreateInstance(typeof(MissionDefinition), true);
            def.missionId = id;
            def.MissionTypeName = typeName;
            return def;
        }

        private static MissionDefinition.KeyValuePair Pair(string name, int value)
        {
            return new MissionDefinition.KeyValuePair { name = name, value = value.ToString(CultureInfo.InvariantCulture) };
        }

        private static Package Reward(int credits, int sparks, int points)
        {
            Package package = new Package();
            if (credits > 0)
            {
                package.items.Add(new PackageItem { type = PackageType.Credits, quantity = credits });
            }
            if (sparks > 0)
            {
                package.items.Add(new PackageItem { type = PackageType.PremiumCredits, quantity = sparks });
            }
            if (points > 0)
            {
                package.items.Add(new PackageItem { type = PackageType.AchievementPoints, quantity = points });
            }
            return package;
        }
    }
}
