using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using JunkWars.Client;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Packages;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using UnityEngine;

namespace GarRevival;

// Part, module and price templates built from the part prefabs (PartPrefab, DockingPoint).
// Chassis, body and barrel stats come from StatTable (tools/infer_stats.py); the rest are designed in Stats() and PriceFor().
internal static class Catalog
{
    public const int MaxTier = StatTable.MaxTier;

    private static readonly Dictionary<string, GamePlayActions> WeaponOverrides = new()
    {
        { "HW_B_Barry_Light", GamePlayActions.RocketLauncher },
        { "LW_B_Bertha", GamePlayActions.Mortar },
        { "LW_B_ElectroCutter", GamePlayActions.Electrocuter },
        { "MW_B_FalmeCaster", GamePlayActions.Flamecaster },
        { "HW_B_MachineGun", GamePlayActions.HeavyMachineGun },
        { "HW_B_Missile", GamePlayActions.MissileLauncher },
        { "HW_B_Necrotron3000", GamePlayActions.HeavyLaser },
        { "LW_B_Arbalet", GamePlayActions.Crossbow },
        { "LW_B_Humiliator", GamePlayActions.AutomaticPistol },
        { "LW_B_Trasher", GamePlayActions.TrashGun },
        { "MW_B_Katyusha", GamePlayActions.KatyushaRocketLauncher },
        { "MW_B_Laser", GamePlayActions.MediumLaser },
        { "MW_B_Machine Gun", GamePlayActions.AssaultRifle },
        { "MW_B_Maximus_Justice", GamePlayActions.Justice },
        { "MW_B_Maximus_Revenge", GamePlayActions.Revenge },
    };

    public static Dictionary<string, PartTemplate> Parts;
    public static Dictionary<string, ModuleTemplate> Modules;
    public static Dictionary<string, PriceTemplate> Prices;

    public static void EnsureBuilt()
    {
        if (Parts != null) return;
        Parts = new();
        Modules = new();
        Prices = new();

        var bundle = DataFactory.PartsBundle().Bundle;
        var report = new StringBuilder();
        foreach (string asset in bundle.GetAllAssetNames().OrderBy(n => n))
        {
            var go = bundle.LoadAsset<GameObject>(asset);
            PartPrefab prefab = go == null ? null : go.GetComponent<PartPrefab>();
            if (prefab == null || !prefab.includeInExport) continue;
            string indent = string.IsNullOrEmpty(prefab.indent) ? go.name : prefab.indent;
            if (Parts.ContainsKey(indent))
            {
                RevivalLog.Info($"Duplicate part indent {indent} in {asset}");
                continue;
            }

            var template = BuildPart(indent, asset, go, prefab);
            Parts[indent] = template;
            Prices[indent] = PriceFor(template);
            if (IsSingleCaseModule(template.type))
            {
                // Heads, hats, eyewear and devices are sold as ready-made modules. The client
                // looks module prices up by moduleName, so that is also the key.
                template.showInShop = false;
                Modules["Mod_" + indent] = new ModuleTemplate
                {
                    moduleName = "Mod_" + indent,
                    type = EnumUtils.CaseToModuleType(template.type),
                    showInShop = true,
                    constructionType = ConstructionType.Sealed,
                    moduleCaseIndent = indent,
                    dockingPointToPartLinks = [],
                };
            }
            report.AppendLine($"{indent} | {template.type} | {template.weightType} | {Describe(template.partTierableStatistics[0].partSpesificData)} | {Origin(template)}");
        }
        foreach (var ammo in AmmoDesign.Prices(Parts.Values.Where(p => p.type == LogicalType.WeaponMuzzle)))
        {
            Prices[ammo.Key] = ammo.Value;
        }

        // A second copy of the game (a server, say) may be writing the report at the same moment.
        try
        {
            File.WriteAllText(Path.Combine(RevivalLog.Folder, "generated-templates.txt"), report.ToString());
        }
        catch (IOException ex)
        {
            RevivalLog.Info("generated-templates.txt not written: " + ex.Message);
        }
        WritePartStats();
        RevivalLog.Info($"Catalog: {Parts.Count} part templates, {Modules.Count} module templates (" +
            string.Join(", ", Parts.Values.GroupBy(p => p.type).Select(g => g.Key + " " + g.Count()).ToArray()) + ")");
        RevivalLog.Info("Weight budget, heaviest two-armed build / lowest chassis limit: " +
            string.Join(", ", new[] { WeightType.Light, WeightType.Medium, WeightType.Heavy }.Select(WeightBudget).ToArray()));
    }

    // part-stats.csv: every part's tier-0 values, for tools/edit-settings.pyw and part-overrides.csv.
    private static void WritePartStats()
    {
        var csv = new StringBuilder("partId,type,class,origin,durability,weight,speed,weightLimit,damage,splash,rateOfFire,capacity,regen,price\n");
        foreach (var p in Parts.Values.OrderBy(p => p.type).ThenBy(p => p.indent))
        {
            var s = p.partTierableStatistics[0];
            var d = s.partSpesificData;
            string F(float? v) => v is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
            float? speed = d is ChassisCaseVO c ? c.Speed * GlobalConstants.visualiseSpeedMod : d is ChassisEngineVO e ? e.SpeedModifier * GlobalConstants.visualiseSpeedMod : null;
            float? limit = d is ChassisCaseVO c2 ? c2.WeightLimit : d is ChassisEngineVO e2 ? e2.WeightLimit : null;
            var barrel = d as WeaponBarrelVO;
            var battery = d as BatteryCaseVO;
            csv.Append(string.Join(",", [p.indent, p.type.ToString(), p.weightType.ToString(), Origin(p),
                F(s.statistics.Durability.maxValue), F(s.statistics.Weight.maxValue), F(speed), F(limit),
                F(barrel?.Damage), F(barrel?.SpashDamage), F(barrel == null ? null : 1f / barrel.RateOfFire),
                F(battery?.EnergyCapacity), F(battery?.EnergyRegeneration), F(BuyPrice(p.indent, 0))])).Append('\n');
        }
        try
        {
            File.WriteAllText(Path.Combine(RevivalLog.Folder, "part-stats.csv"), csv.ToString());
        }
        catch (IOException ex)
        {
            RevivalLog.Info("part-stats.csv not written: " + ex.Message);
        }
    }

    // A two-weapon body of the class with its heaviest chassis, engine, battery and head,
    // against the class's lowest chassis limit.
    private static string WeightBudget(WeightType weight)
    {
        float build = Heaviest(p => p.type == LogicalType.BodyCase && p.weightType == weight && WeaponSlots(p) == 2)
            + Heaviest(p => p.type == LogicalType.CassisCase && p.weightType == weight)
            + Heaviest(p => p.type == LogicalType.Engine) + Heaviest(p => p.type == LogicalType.Battery) + Heaviest(p => p.type == LogicalType.HeadCase)
            + 2f * (Heaviest(p => p.type == LogicalType.WeaponCase && p.weightType == weight) + Heaviest(p => p.type == LogicalType.WeaponMuzzle && p.weightType == weight));
        float limit = Parts.Values.Where(p => p.type == LogicalType.CassisCase && p.weightType == weight)
            .Select(p => p.partTierableStatistics[0].partSpesificData as ChassisCaseVO).Where(v => v != null)
            .Select(v => v.WeightLimit).DefaultIfEmpty(0f).Min();
        return $"{weight} {Mathf.Round(build)}/{limit}";
    }

    private static float Heaviest(Func<PartTemplate, bool> match) =>
        Parts.Values.Where(match).Select(p => p.partTierableStatistics[0].statistics.Weight.maxValue).DefaultIfEmpty(0f).Max();

    public static bool IsSingleCaseModule(LogicalType type) =>
        type is LogicalType.HeadCase or LogicalType.HatCase or LogicalType.EyewearCase or LogicalType.DeviceCase;

    private static PartTemplate BuildPart(string indent, string asset, GameObject go, PartPrefab prefab)
    {
        var template = new PartTemplate
        {
            // ResourcesManager loads parts as "Prefabs/" + prefabPath.
            prefabPath = asset.StartsWith("prefabs/", StringComparison.Ordinal) ? asset.Substring("prefabs/".Length) : asset,
            indent = indent,
            uiData = new UIData { name = indent + "_Name", description = indent + "_Description", icon = string.Empty },
            dockingPoints = go.GetComponentsInChildren<DockingPoint>(true).Select(p => p.GetSerializableVO()).ToArray(),
            type = prefab.type,
            role = prefab.type.ToString().EndsWith("Case", StringComparison.Ordinal) ? PartRole.Case : PartRole.Part,
            purchasable = true,
            showInShop = true,
            keywords = prefab.keywords ?? [],
            weightType = WeightOf(indent, prefab.type),
            minTier = 0,
            maxTier = prefab.type == LogicalType.DeviceCase ? StatTable.MaxDeviceTier : MaxTier,
        };
        // Match what the client sees after protobuf + VoNormalizer, so
        // Utils.KeywordMach gives the same answer here as in the garage.
        foreach (var point in template.dockingPoints)
        {
            point.keywords ??= [];
            point.possibleDockingTypes ??= [];
        }
        template.partTierableStatistics = Enumerable.Range(0, MaxTier + 1).Select(tier => Stats(template, go, tier)).ToArray();
        return template;
    }

    // Light/Medium/Heavy from the H/M/L naming convention (HB_, MW_, LC_ ...).
    private static WeightType WeightOf(string indent, LogicalType type)
    {
        if (type is not (LogicalType.BodyCase or LogicalType.CassisCase or LogicalType.WeaponCase or LogicalType.WeaponMuzzle)) return WeightType.None;
        if (Design.ClassOverrides.TryGetValue(indent, out var described)) return described;
        return (indent.Length > 0 ? indent[0] : ' ') switch
        {
            'H' => WeightType.Heavy,
            'L' => WeightType.Light,
            _ => WeightType.Medium,
        };
    }

    // Light/Medium/Heavy share of a stat's bar maximum.
    private static float ClassScale(WeightType weight) => weight switch
    {
        WeightType.Light => 0.6f,
        WeightType.Medium => 0.8f,
        _ => 1f,
    };

    // Share of the heavy maxima for chassis, weapon case and barrel mass,
    // so a class's two-armed builds stay under its real weight limits.
    private static float MassScale(WeightType weight) => weight switch
    {
        WeightType.Light => 0.3f,
        WeightType.Medium => 0.6f,
        _ => 1f,
    };

    // Designed values are top-tier maxima (the garage's bar maxima), scaled down the fitted durability curve.
    private static float TierScale(int tier) => StatTable.DurabilityCurve(tier) / StatTable.DurabilityCurve(MaxTier);

    private static float Hundredths(float value) => Mathf.Round(value * 100f) / 100f;

    // An override is a tier-0 value; higher tiers follow the durability curve.
    private static float Tiered(PartTemplate template, string stat, int tier, float builtIn) =>
        PartOverrides.Get(template.indent, stat) is float v ? v * StatTable.DurabilityCurve(tier) : builtIn;

    private static int WeaponSlots(PartTemplate template) =>
        template.dockingPoints.Count(d => d.isAssemblyRoot && d.possibleDockingTypes.Contains(LogicalType.Weapon));

    private static Statistic Stat(float value) => new() { maxValue = value, currentValue = value, modifier = 0f };

    // The part's own row; a part the table does not know takes a plain part of its class.
    private static T RowOf<T>(Dictionary<string, T> table, PartTemplate template, string light, string medium, string heavy) =>
        table.TryGetValue(template.indent, out var row) ? row
            : table[template.weightType == WeightType.Light ? light : template.weightType == WeightType.Heavy ? heavy : medium];

    private static string Origin(PartTemplate template) => StatTable.Origin.TryGetValue(template.indent, out var origin) ? origin : "designed";

    // Designed stats use the garage's stat-bar scale: each stat's per-type bar maximum
    // (e.g. weapon case durability 628, battery capacity 270) is a top-tier heavy part,
    // scaled down by class and tier and shifted by the part's Design traits.
    // Real rows grow with tier only in durability, damage and splash.
    private static PartTierableStatistics Stats(PartTemplate template, GameObject go, int tier)
    {
        float c = ClassScale(template.weightType);
        float m = MassScale(template.weightType);
        float t = TierScale(tier);
        float durability;
        float mass;
        AbstractPartSpesificData specific = null;
        switch (template.type)
        {
            case LogicalType.CassisCase:
            {
                var kind = Design.Chassis.TryGetValue(template.indent, out var described) ? described : ChassisTypeOf(go);
                var row = RowOf(StatTable.Chassis, template, "LC_Buzzer", "MC_Bugsy", "HC_Daggerjaw");
                durability = Mathf.Round(row.Durability * StatTable.DurabilityCurve(tier));
                mass = 25.2f * m;
                specific = new ChassisCaseVO
                {
                    Speed = PartOverrides.Or(template.indent, "speed", row.SpeedKmh) / GlobalConstants.visualiseSpeedMod,
                    WeightLimit = PartOverrides.Or(template.indent, "weightLimit", row.WeightLimit),
                    EnergyConsumption = 0.001f,
                    // Guide terrain rating: good, average, poor.
                    TerrainPenaltyNegate = row.Terrain > 0 ? 1f : row.Terrain == 0 ? 0.5f : 0.1f,
                    chassisType = kind,
                };
                break;
            }
            case LogicalType.Engine:
            {
                var engine = Design.Engines.TryGetValue(template.indent, out var e) ? e : new Design.EngineTraits();
                durability = 20f * t;
                mass = 3f;
                float speedKmh = PartOverrides.Or(template.indent, "speed", StatTable.EngineSpeedKmh.TryGetValue(template.indent, out var measuredSpeed) ? measuredSpeed : engine.Speed);
                float weightLimit = PartOverrides.Or(template.indent, "weightLimit", StatTable.EngineWeightLimit.TryGetValue(template.indent, out var measuredLimit) ? measuredLimit : 40f * engine.WeightLimit);
                specific = new ChassisEngineVO { SpeedModifier = speedKmh / GlobalConstants.visualiseSpeedMod, EnergyConsumption = 0.001f, WeightLimit = weightLimit };
                break;
            }
            case LogicalType.BodyCase:
            {
                // Four-armed bodies use more energy per shot.
                bool fourArms = WeaponSlots(template) > 2;
                var row = RowOf(StatTable.Bodies, template, "LB_Jackhammer", "MB_Chromax", "HB_Chopper");
                durability = Mathf.Round(row.Durability * StatTable.DurabilityCurve(tier));
                mass = row.Weight;
                specific = new BodyCaseVO { EnergyConsumption = fourArms ? 0.002f : 0.001f, EnergyPerShootModifier = fourArms ? 1.3f : 1f };
                break;
            }
            case LogicalType.Battery:
            {
                // The higher a battery's capacity, the lower its regeneration.
                var battery = Design.Batteries.TryGetValue(template.indent, out var b) ? b : new Design.BatteryTraits { Quality = 0.8f, HighCapacity = true };
                durability = 1f;
                mass = 1f;
                specific = new BatteryCaseVO
                {
                    EnergyCapacity = Tiered(template, "capacity", tier, 270f * battery.Quality * t * (battery.HighCapacity ? 1f : 0.6f)),
                    EnergyRegeneration = Tiered(template, "regen", tier, 335f * battery.Quality * t * (battery.HighCapacity ? 0.5f : 1f)),
                };
                break;
            }
            case LogicalType.WeaponCase:
            {
                var mechanism = Design.Mechanisms.TryGetValue(template.indent, out var mt) ? mt : new Design.MechanismTraits();
                durability = 628f * c * t;
                mass = 25.2f * m;
                // Case values multiply the barrel's (WeaponBarrel.GetDamage etc.).
                specific = new WeaponCaseVO
                {
                    fireDelay = mechanism.FireDelay,
                    accuracyModidifer = mechanism.Accuracy,
                    damageModidifer = StatTable.DamageModifier.TryGetValue(template.indent, out var measuredDamage) ? measuredDamage : mechanism.Damage,
                    splashDamageModidifer = mechanism.Splash,
                    projectileSpeedModidifer = mechanism.ProjectileSpeed,
                    energyConsumptionModidifer = mechanism.Energy,
                    durabilityModidifer = 1f,
                };
                break;
            }
            case LogicalType.WeaponMuzzle:
                durability = 240f * c * t;
                mass = 25.5f * m;
                specific = Barrel(template, tier);
                break;
            case LogicalType.DeviceCase:
                durability = 10f;
                mass = 1f;
                specific = Device(template.indent, tier);
                break;
            case LogicalType.HeadCase:
                durability = 360f * t;
                mass = 10f;
                break;
            default:
                durability = 30f * t;
                mass = 1f;
                break;
        }
        durability = Mathf.Round(Tiered(template, "durability", tier, durability));
        mass = PartOverrides.Or(template.indent, "weight", mass);
        return new PartTierableStatistics
        {
            statistics = new BaseStatistics { Durability = Stat(durability), Weight = Stat(mass) },
            partSpesificData = specific,
            itemLevel = 1f + tier,
            Durability2CreditsRato = 1f,
        };
    }

    private static Design.BarrelTraits TraitsOf(string indent, string[] keywords)
    {
        if (Design.Barrels.TryGetValue(indent, out var traits)) return traits;
        bool rocket = keywords.Contains("Rocket");
        return new Design.BarrelTraits { Splash = rocket ? 0.8f : 0f, SplashRange = rocket ? 4f : 0f };
    }

    // Barrels with ammo types fire ammo (AmmoDesign); the rest are energy weapons:
    // AmmoPerShot 0 skips WeaponBarrel.AreRequermentsMeet's ammo check, MaxAmmo -1 skips
    // garage ammo distribution. CurrentAmmo must not be negative: the bot AI sums it to
    // decide whether to look for ammo.
    private static WeaponBarrelVO Barrel(PartTemplate template, int tier)
    {
        var traits = TraitsOf(template.indent, template.keywords);
        var row = RowOf(StatTable.Barrels, template, "LW_B_Flintlock", "MW_B_Laser", "HW_B_Fragunov");
        float shotDamage = Hundredths(PartOverrides.Or(template.indent, "damage", row.Damage) * StatTable.DamageCurve(tier));
        float splash = Hundredths(PartOverrides.Or(template.indent, "splash", row.Splash) * StatTable.DamageCurve(tier));
        float shotsPerSecond = PartOverrides.Or(template.indent, "rateOfFire", row.ShotsPerSecond);
        float range = template.weightType == WeightType.Light ? 110f : template.weightType == WeightType.Heavy ? 200f : 150f;
        string[] ammo = AmmoDesign.TypesFor(template.indent);
        bool usesAmmo = ammo.Length > 0;
        return new WeaponBarrelVO
        {
            ActiveEfectToExecute = WeaponAction(template.indent),
            AdditionalEffects = [],
            MaxAmmo = usesAmmo ? AmmoDesign.Capacity(template.weightType, traits) : -1f,
            CurrentAmmo = 0f,
            // UI maxima: accuracy 1, rate of fire 5/s, range 200.
            // RateOfFire is the delay between shots in seconds (WeaponBarrel.GetRateOfFire).
            Accuracy = Mathf.Min(1f, traits.Accuracy + 0.05f * tier / MaxTier),
            RateOfFire = 1f / Mathf.Min(5f, shotsPerSecond),
            Damage = shotDamage,
            Range = Mathf.Min(200f, range * traits.Range),
            SpashDamage = splash,
            SpashRange = traits.SplashRange,
            AmmoPerShot = usesAmmo ? 1f : 0f,
            // Heavier hits cost more energy per shot (maximum 19.2 at damage 96).
            EnergyConsumptionPerShoot = 19.2f * Mathf.Clamp(shotDamage / 96f, 0.15f, 1f) * (usesAmmo ? AmmoDesign.EnergyShare : 1f),
            usableAmmoTypes = ammo,
        };
    }

    // Most barrels are named after their weapon action (LW_B_Revolver -> Revolver).
    private static GamePlayActions WeaponAction(string indent)
    {
        if (WeaponOverrides.TryGetValue(indent, out var action)) return action;
        string bare = Normalize(indent.Substring(indent.LastIndexOf('_') + 1));
        foreach (GamePlayActions candidate in Enum.GetValues(typeof(GamePlayActions)))
        {
            if (Normalize(candidate.ToString()) == bare) return candidate;
        }
        RevivalLog.Info($"No weapon action for {indent}, using AssaultRifle");
        return GamePlayActions.AssaultRifle;
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static DeviceCaseVO Device(string indent, int tier)
    {
        float t = TierScale(tier);
        DeviceActions action;
        AbstractDeviceSpesificData data;
        switch (indent)
        {
            case "Device_ControllableQuadcopter": action = DeviceActions.ControllableQuadcopter; data = new DroneQuadcopterVO { Range = 35f, Accuracy = 0.8f, Damage = 9.2f * t, FireDelay = 0.2f }; break;
            case "Device_EggPunch": action = DeviceActions.TurretRocketEaster; data = new TurrentRocketVO { Range = 35f, Accuracy = 0.8f, SplashDamage = 80f * t, SplashRange = 4f, FireDelay = 2f }; break;
            case "Device_EMP": action = DeviceActions.LandMineEnergyDrain; data = new LandMineEnergyVO(); break;
            case "Device_Invisibility": action = DeviceActions.Invisibility; data = new BasicDeviceDataVO(); break;
            case "Device_LandMineDamage": action = DeviceActions.LandMineDamage; data = new LandMineDamageVO(); break;
            case "Device_LandMineEnergyDrain": action = DeviceActions.LandMineEnergyDrain; data = new LandMineEnergyVO(); break;
            case "Device_LandMineSlow": action = DeviceActions.LandMineSlow; data = new LandMineSlowVO { Range = 6f, SlowPercent = 0.5f, SlowDuration = 4f }; break;
            case "Device_MiniMap": action = DeviceActions.MiniMap; data = new MiniMapVO(); break;
            case "Device_Shield": action = DeviceActions.Shield; data = new ShieldVO { ShieldDurability = 1200f * t }; break;
            case "Device_SpyCam": action = DeviceActions.StickyCamera; data = new StickyCameraVO(); break;
            case "Device_TurrentFlamethrower": action = DeviceActions.TurrentFlamethrower; data = new TurrentFlamethrowerVO(); break;
            case "Device_TurrentMachineGun": action = DeviceActions.TurrentMachineGun; data = new TurrentMachineGunVO(); break;
            case "Device_TurrentRocket": action = DeviceActions.TurrentRocket; data = new TurrentRocketVO { Range = 35f, Accuracy = 0.8f, SplashDamage = 80f * t, SplashRange = 4f, FireDelay = 2f }; break;
            default: action = DeviceActions.Shield; data = new ShieldVO { ShieldDurability = 1200f * t }; break;
        }
        data.LiveTime = 60f;
        data.Durability = 1200f * t;
        return new DeviceCaseVO { efectToExecute = action, activationCostEnergy = 60f, cooldown = 30f, activationDelay = 0.5f, deviceSpesificData = data };
    }

    // The prefab's hover flag first; otherwise guessed from the model:
    // wheel colliders mean wheels, legs a walker, tracks tracked.
    private static ChassisType ChassisTypeOf(GameObject go)
    {
        var chassis = go.GetComponent<ChassisCasePrefab>();
        if (chassis != null && chassis.hovering) return ChassisType.Hover;
        if (go.GetComponentsInChildren<WheelCollider>(true).Length > 0) return ChassisType.Wheelled;
        string names = string.Join(" ", go.GetComponentsInChildren<Transform>(true).Select(x => x.name.ToLowerInvariant()).ToArray());
        if (names.Contains("leg") || names.Contains("foot") || names.Contains("knee")) return ChassisType.Walker;
        if (names.Contains("track")) return ChassisType.Tracked;
        if (names.Contains("wheel") || names.Contains("tire") || names.Contains("tyre")) return ChassisType.Wheelled;
        return ChassisType.Hover;
    }

    private static PriceTemplate PriceFor(PartTemplate template)
    {
        float w = ClassScale(template.weightType) / 0.8f;
        float basePrice = template.type switch
        {
            LogicalType.CassisCase or LogicalType.BodyCase => 3000f * w,
            LogicalType.WeaponCase => 1500f * w,
            LogicalType.WeaponMuzzle => 2000f * w,
            LogicalType.Engine => 1200f,
            LogicalType.Battery => 800f,
            LogicalType.HeadCase => 1500f,
            LogicalType.DeviceCase => 2500f,
            _ => 500f,
        };
        basePrice = PartOverrides.Or(template.indent, "price", basePrice);
        return new PriceTemplate
        {
            indent = template.indent,
            data = new PriceDataTemplate
            {
                normalBuy = Tiers(tier => basePrice * (1 + tier)),
                normalSell = Tiers(tier => basePrice * (1 + tier) * 0.5f),
                upgrade = Tiers(tier => basePrice * 0.5f * (1 + tier)),
                repair = Tiers(tier => basePrice * 0.1f),
                decompose = Tiers(tier => basePrice * 0.25f),
            },
        };
    }

    internal static TierLink[] Tiers(Func<int, float> credits, bool whole = true) =>
        Enumerable.Range(0, MaxTier + 1).Select(tier => new TierLink
        {
            tier = tier,
            items = [new PricePackageItem { type = PackageType.Credits, quantity = whole ? Mathf.Round(credits(tier)) : credits(tier), indent = string.Empty }],
        }).ToArray();

    public static float BuyPrice(string indent, int tier) =>
        Prices.TryGetValue(indent, out var price) ? CreditsAt(price.data.normalBuy, tier) : 0f;

    // Same as DataManager.GetShopModulePriceData: the case plus its parts.
    public static float ModuleBuyPrice(ModuleTemplate module)
    {
        float price = BuyPrice(module.moduleCaseIndent, 0);
        if (module.dockingPointToPartLinks != null)
        {
            price += module.dockingPointToPartLinks.Sum(l => BuyPrice(l.partIndent, 0));
        }
        return price;
    }

    public static float SellPrice(string indent, int tier) =>
        Prices.TryGetValue(indent, out var price) ? CreditsAt(price.data.normalSell, tier) : 0f;

    private static float CreditsAt(TierLink[] links, int tier)
    {
        if (links == null) return 0f;
        foreach (var link in links)
        {
            if (link.tier == tier && link.items != null) return link.items.Where(i => i.type == PackageType.Credits).Sum(i => i.quantity);
        }
        return 0f;
    }

    private static string Describe(AbstractPartSpesificData data) => data switch
    {
        WeaponBarrelVO barrel => "weapon " + barrel.ActiveEfectToExecute,
        ChassisCaseVO chassis => "chassis " + chassis.chassisType,
        DeviceCaseVO device => "device " + device.efectToExecute,
        null => "-",
        _ => data.GetType().Name,
    };
}
