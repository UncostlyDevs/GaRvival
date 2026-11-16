using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JunkWars.Common.Garage.ValueObjects;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival;

// The server's copy of the player profile, saved to GARRevival/save/profile.dat after every change.
// The client keeps its own copy and is told about changes through events.
internal static class PlayerState
{
    // TutorialManager.IsTutorialDone
    private const int TutorialCompleted = 128;

    private static PlayerVO _player;

    // Set while playing on a server that keeps its own profiles.
    private static string _server;
    private static float _serverCredits;
    private static float _serverSparks;

    // Raised after the profile folder changed, so state kept beside the profile can be reloaded.
    public static event Action ProfileChanged;

    public static string ServerProfile => _server;

    // The folder the current profile (and anything saved beside it) lives in.
    public static string Folder
    {
        get
        {
            string own = Path.Combine(RevivalLog.Folder, "save" + Instance.Suffix);
            return _server == null ? own : Path.Combine(Path.Combine(own, "servers"), _server);
        }
    }

    private static string SavePath => Path.Combine(Folder, "profile.dat");

    public static PlayerVO Current
    {
        get
        {
            EnsureLoaded();
            return _player;
        }
    }

    // Switches to one server's profile (created with its starting money), or back to the own one with null.
    // The caller makes the client load it.
    public static void UseServerProfile(string serverId, float startingCredits, float startingSparks)
    {
        if (_player != null)
        {
            Save();
        }
        _server = serverId;
        _serverCredits = startingCredits;
        _serverSparks = startingSparks;
        _player = null;
        ProfileChanged?.Invoke();
    }

    private static void EnsureLoaded()
    {
        if (_player != null) return;
        Catalog.EnsureBuilt();
        if (File.Exists(SavePath))
        {
            try
            {
                _player = DataSerialization.ProtoDeserializeObject<PlayerVO>(File.ReadAllBytes(SavePath));
                VoNormalizer.FillNullCollections(_player);
                RefreshPartStats(_player);
                RevivalLog.Info($"Loaded profile: {_player.ownedParts.Count} parts, {_player.ownedModuleAssemblies.Count} modules, {_player.ownedRobotAssemblies.Length} robots");
            }
            catch (Exception ex)
            {
                string broken = SavePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(SavePath, broken);
                RevivalLog.Error($"Profile could not be read; moved to {broken} and starting fresh", ex);
                _player = null;
            }
        }
        if (_player == null)
        {
            _player = NewPlayer();
            if (_server != null)
            {
                _player.credits = _serverCredits;
                _player.premiumCredits = _serverSparks;
            }
            RevivalLog.Info("Created new profile with a starter robot" + (_server == null ? string.Empty : " for server " + _server));
        }
        if (AmmoOps.PrepareProfile(_player) || !File.Exists(SavePath))
        {
            Save();
        }
        if (Settings.SkipTutorial && _player.tutorialState != TutorialCompleted)
        {
            _player.tutorialState = TutorialCompleted;
            RevivalLog.Info("skipTutorial is on: tutorial marked as completed");
        }
        if (Settings.UnlimitedCredits && _server == null) // a server's economy is the server's to decide
        {
            _player.credits = Settings.UnlimitedCreditsAmount;
            _player.premiumCredits = Settings.UnlimitedPremiumAmount;
            RevivalLog.Info("unlimitedCredits is on: credits and sparks refilled");
        }
    }

    // Parts carry a copy of their template's stats; re-derive them so catalog balance changes reach old profiles.
    private static void RefreshPartStats(PlayerVO player)
    {
        foreach (var part in player.ownedParts)
        {
            if (!Catalog.Parts.TryGetValue(part.indent, out var template))
            {
                RevivalLog.Info($"Saved part {part.partID} uses unknown template {part.indent}");
                continue;
            }
            var stats = template.partTierableStatistics[Math.Max(0, Math.Min(template.maxTier, part.currentTier))];
            // Battle wear is kept as a share of the durability.
            float worn = part.statistics != null && part.statistics.Durability.maxValue > 0f
                ? Math.Max(0f, Math.Min(1f, part.statistics.Durability.currentValue / part.statistics.Durability.maxValue)) : 1f;
            part.statistics = stats.statistics.ShallowCopy();
            part.statistics.Durability.currentValue = part.statistics.Durability.maxValue * worn;
            part.partSpesificData = stats.partSpesificData;
            part.Durability2CreditsRato = stats.Durability2CreditsRato;
            part.uiData = template.uiData.ShallowCopy();
            part.type = template.type;
            part.role = template.role;
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath));
        string temp = SavePath + ".tmp";
        File.WriteAllBytes(temp, DataSerialization.ProtoSerializeObject(_player));
        if (File.Exists(SavePath))
        {
            File.Delete(SavePath);
        }
        File.Move(temp, SavePath);
    }

    private static PlayerVO NewPlayer()
    {
        string nick = Instance.Profile ?? SteamBridge.PersonaName() ?? "Pilot";
        // Names show as "<name>.<4 hex>"; the tag keeps two players with the same name apart.
        var steamId = SteamBridge.SteamId();
        string identity = Instance.Profile ?? (steamId.HasValue ? ((ulong)steamId.Value).ToString() : nick);
        uint hash = 2166136261;
        foreach (char c in identity)
        {
            hash = (hash ^ c) * 16777619;
        }
        var player = new PlayerVO
        {
            playerName = nick,
            fullName = nick + "." + (hash & 0xFFFF).ToString("x4"),
            level = 1,
            credits = 250000f,
            premiumCredits = 1000f,
            ownedModuleAssemblies = [],
            ownedParts = [],
            ammoDistribution = new AmmoDistributionVO(),
            ownedAmmo = [],
            ownedGarages = [],
            ownedGarageAccessories = [],
            currentGarageID = string.Empty,
            lifetimeStatistics = new LifetimeStatisticsVO(),
            inventorySlots = 200,
            ownedPortraits = new List<int>(DataFactory.PortraitIndices),
            currentPortraitIndex = DataFactory.PortraitIndices.FirstOrDefault(),
            selectedRobotIndex = 0,
        };
        player.ownedRobotAssemblies = [StarterRobot(player), EmptyRobot(player), EmptyRobot(player)];
        return player;
    }

    // Parts and modules share one id space so an id is never ambiguous.
    private static int NextItemId(PlayerVO player)
    {
        int next = Math.Max(player.lastUsedPartID, player.lastUsedModuleID) + 1;
        player.lastUsedPartID = next;
        player.lastUsedModuleID = next;
        return next;
    }

    public static PartVO CreatePart(PlayerVO player, PartTemplate template, int tier = 0)
    {
        tier = Math.Max(template.minTier, Math.Min(template.maxTier, tier));
        var stats = template.partTierableStatistics[tier];
        var part = new PartVO
        {
            partID = NextItemId(player),
            indent = template.indent,
            purchasable = template.purchasable,
            type = template.type,
            role = template.role,
            Durability2CreditsRato = stats.Durability2CreditsRato,
            statistics = stats.statistics.ShallowCopy(),
            uiData = template.uiData.ShallowCopy(),
            partSpesificData = stats.partSpesificData,
            currentTier = tier,
        };
        player.ownedParts.Add(part);
        return part;
    }

    // A module made of a case part plus parts docked into its slots.
    public static ModuleAssemblyVO CreateModule(PlayerVO player, PartTemplate caseTemplate, IEnumerable<KeyValuePair<string, PartTemplate>> slotParts, List<PartVO> createdParts)
    {
        var casePart = CreatePart(player, caseTemplate);
        createdParts.Add(casePart);
        var moduleType = EnumUtils.CaseToModuleType(caseTemplate.type);
        var module = new ModuleAssemblyVO
        {
            moduleID = NextItemId(player),
            moduleName = caseTemplate.uiData.name,
            constructionType = Catalog.IsSingleCaseModule(caseTemplate.type) ? ConstructionType.Sealed : ConstructionType.Editable,
            type = moduleType,
            gameplayType = EnumUtils.ModuleLogicalTypeToGameplayType(moduleType),
            moduleCaseID = casePart.partID,
            dockingPointToPartLinks = [],
        };
        foreach (var slot in slotParts)
        {
            var part = CreatePart(player, slot.Value);
            createdParts.Add(part);
            module.dockingPointToPartLinks.Add(new DockingPointToPartLinks { dockingPointName = slot.Key, partID = part.partID });
        }
        player.ownedModuleAssemblies.Add(module);
        return module;
    }

    public static ModuleAssemblyVO CreateModuleFromTemplate(PlayerVO player, ModuleTemplate template, List<PartVO> createdParts)
    {
        var links = (template.dockingPointToPartLinks ?? [])
            .Select(l => new KeyValuePair<string, PartTemplate>(l.dockingPointName, Catalog.Parts[l.partIndent]));
        return CreateModule(player, Catalog.Parts[template.moduleCaseIndent], links, createdParts);
    }

    public static RobotAssemblyVO EmptyRobot(PlayerVO player)
    {
        player.lastUsedRobotID++;
        return new RobotAssemblyVO
        {
            assemblyID = player.lastUsedRobotID,
            assemblyName = "Robot " + player.lastUsedRobotID,
            rootPartID = -1,
            autoResupply = true,
            dockingPointToModuleLinks = [],
        };
    }

    private static IEnumerable<PartTemplate> OfType(LogicalType type) =>
        Catalog.Parts.Values.Where(p => p.type == type).OrderBy(p => p.indent, StringComparer.Ordinal);

    private static DockingPointVO[] Slots(PartTemplate template, LogicalType type, bool moduleSlot) =>
        template.dockingPoints.Where(d => d.isAssemblyRoot == moduleSlot && d.possibleDockingTypes.Contains(type)).ToArray();

    // A medium robot: chassis + engine, body + batteries, a weapon in every weapon slot and a head.
    // Picks are deterministic so every new profile gets the same robot.
    private static RobotAssemblyVO StarterRobot(PlayerVO player)
    {
        var robot = EmptyRobot(player);
        robot.assemblyName = "Starter";
        var created = new List<PartVO>();

        var chassisCase = OfType(LogicalType.CassisCase).Where(c => Slots(c, LogicalType.Engine, false).Length > 0 && Slots(c, LogicalType.Body, true).Length > 0)
            .OrderBy(c => c.weightType == WeightType.Medium ? 0 : 1).First();
        var engine = OfType(LogicalType.Engine).First();
        var chassis = CreateModule(player, chassisCase,
            new[] { new KeyValuePair<string, PartTemplate>(Slots(chassisCase, LogicalType.Engine, false)[0].Name, engine) }, created);
        robot.rootPartID = chassis.moduleID;

        var bodyCase = OfType(LogicalType.BodyCase).Where(b => Slots(b, LogicalType.Head, true).Length > 0 && Slots(b, LogicalType.Weapon, true).Length > 0)
            .OrderBy(b => b.weightType == WeightType.Medium ? 0 : 1).First();
        var battery = OfType(LogicalType.Battery).First();
        var body = CreateModule(player, bodyCase,
            Slots(bodyCase, LogicalType.Battery, false).Select(s => new KeyValuePair<string, PartTemplate>(s.Name, battery)), created);
        Link(robot, chassis, Slots(chassisCase, LogicalType.Body, true)[0], body);

        foreach (var weaponSlot in Slots(bodyCase, LogicalType.Weapon, true))
        {
            var weapon = StarterWeapon(player, created);
            if (weapon != null)
            {
                Link(robot, body, weaponSlot, weapon);
            }
        }

        var headCase = OfType(LogicalType.HeadCase).First();
        var head = CreateModule(player, headCase, new KeyValuePair<string, PartTemplate>[0], created);
        Link(robot, body, Slots(bodyCase, LogicalType.Head, true)[0], head);

        RevivalLog.Info("Starter robot: " + string.Join(", ", created.Select(p => p.indent).ToArray()));
        return robot;
    }

    // A medium weapon case with a matching barrel in each muzzle slot.
    private static ModuleAssemblyVO StarterWeapon(PlayerVO player, List<PartVO> created)
    {
        foreach (var weaponCase in OfType(LogicalType.WeaponCase).OrderBy(c => c.weightType == WeightType.Medium ? 0 : 1))
        {
            var muzzleSlots = Slots(weaponCase, LogicalType.WeaponMuzzle, false);
            var barrels = new List<KeyValuePair<string, PartTemplate>>();
            foreach (var slot in muzzleSlots)
            {
                var barrel = OfType(LogicalType.WeaponMuzzle).FirstOrDefault(m =>
                    global::Utils.KeywordMach(slot.keywords, m.keywords) && (m.weightType == WeightType.None || m.weightType == weaponCase.weightType));
                if (barrel == null) break;
                barrels.Add(new KeyValuePair<string, PartTemplate>(slot.Name, barrel));
            }
            if (muzzleSlots.Length > 0 && barrels.Count == muzzleSlots.Length)
            {
                return CreateModule(player, weaponCase, barrels, created);
            }
        }
        RevivalLog.Info("No weapon case/barrel combination matched for the starter robot");
        return null;
    }

    private static void Link(RobotAssemblyVO robot, ModuleAssemblyVO parent, DockingPointVO slot, ModuleAssemblyVO child) =>
        robot.dockingPointToModuleLinks.Add(new RobotAssemblyChildVO { parentModuleID = parent.moduleID, parentDockingPointName = slot.Name, childModuleID = child.moduleID });

    public static ModuleAssemblyVO FindModule(int id) => Current.ownedModuleAssemblies.FirstOrDefault(m => m.moduleID == id);

    public static PartVO FindPart(int id) => Current.ownedParts.FirstOrDefault(p => p.partID == id);

    public static List<int> PartIdsOf(ModuleAssemblyVO module)
    {
        var ids = new List<int> { module.moduleCaseID };
        ids.AddRange(module.dockingPointToPartLinks.Select(l => l.partID));
        return ids;
    }
}
