using System;
using System.Collections.Generic;
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

namespace GarRevival
{
    // Rebuilds the part, module and price templates the original servers
    // exported from the part prefabs. Identity, type, keywords and docking
    // points come from each prefab's PartPrefab / DockingPoint components;
    // names come from the game's localisation keys (<indent>_Name). Stats and
    // prices never shipped with the client: chassis, body and barrel numbers
    // come from RealStats, the rest are designed - see Stats() and PriceFor().
    internal static class Catalog
    {
        public const int MaxTier = 4;

        public static Dictionary<string, PartTemplate> Parts;
        public static Dictionary<string, ModuleTemplate> Modules;
        public static Dictionary<string, PriceTemplate> Prices;

        public static void EnsureBuilt()
        {
            if (Parts != null)
            {
                return;
            }
            Parts = new Dictionary<string, PartTemplate>();
            Modules = new Dictionary<string, ModuleTemplate>();
            Prices = new Dictionary<string, PriceTemplate>();

            AssetBundle bundle = DataFactory.PartsBundle().Bundle;
            StringBuilder report = new StringBuilder();
            foreach (string asset in bundle.GetAllAssetNames().OrderBy(n => n))
            {
                GameObject go = bundle.LoadAsset<GameObject>(asset);
                PartPrefab prefab = go == null ? null : go.GetComponent<PartPrefab>();
                if (prefab == null || !prefab.includeInExport)
                {
                    continue;
                }
                string indent = string.IsNullOrEmpty(prefab.indent) ? go.name : prefab.indent;
                if (Parts.ContainsKey(indent))
                {
                    RevivalLog.Info("Duplicate part indent " + indent + " in " + asset);
                    continue;
                }

                PartTemplate template = BuildPart(indent, asset, go, prefab);
                Parts[indent] = template;
                Prices[indent] = PriceFor(template);
                if (IsSingleCaseModule(template.type))
                {
                    // Heads, hats, eyewear and devices were sold as ready-made
                    // modules. The client looks module prices up by moduleName,
                    // so that is also the dictionary key; "Mod_<case id>" is
                    // the original naming (see the tutorial's robot presets).
                    template.showInShop = false;
                    Modules["Mod_" + indent] = new ModuleTemplate
                    {
                        moduleName = "Mod_" + indent,
                        type = EnumUtils.CaseToModuleType(template.type),
                        showInShop = true,
                        constructionType = ConstructionType.Sealed,
                        moduleCaseIndent = indent,
                        dockingPointToPartLinks = new List<TemplateDockingPointToPartLinks>(),
                    };
                }
                report.AppendLine(indent + " | " + template.type + " | " + template.weightType + " | " + Describe(template.partTierableStatistics[0].partSpesificData) + " | " + Origin(template));
            }
            foreach (KeyValuePair<string, PriceTemplate> ammo in AmmoDesign.Prices(Parts.Values.Where(p => p.type == LogicalType.WeaponMuzzle)))
            {
                Prices[ammo.Key] = ammo.Value;
            }

            // A report only; a second copy of the game (a server, say) may be writing it at the same moment.
            try
            {
                File.WriteAllText(Path.Combine(RevivalLog.Folder, "generated-templates.txt"), report.ToString());
            }
            catch (IOException ex)
            {
                RevivalLog.Info("generated-templates.txt not written: " + ex.Message);
            }
            RevivalLog.Info("Catalog: " + Parts.Count + " part templates, " + Modules.Count + " module templates (" +
                string.Join(", ", Parts.Values.GroupBy(p => p.type).Select(g => g.Key + " " + g.Count()).ToArray()) + ")");
            RevivalLog.Info("Weight budget, heaviest two-armed build / lowest chassis limit: " +
                string.Join(", ", new[] { WeightType.Light, WeightType.Medium, WeightType.Heavy }.Select(WeightBudget).ToArray()));
        }

        // A body of the class with two single-barrel weapons, its heaviest
        // chassis, engine, battery and head, against the class's lowest limit.
        private static string WeightBudget(WeightType weight)
        {
            float build = Heaviest(p => p.type == LogicalType.BodyCase && p.weightType == weight && WeaponSlots(p) == 2)
                + Heaviest(p => p.type == LogicalType.CassisCase && p.weightType == weight)
                + Heaviest(p => p.type == LogicalType.Engine) + Heaviest(p => p.type == LogicalType.Battery) + Heaviest(p => p.type == LogicalType.HeadCase)
                + 2f * (Heaviest(p => p.type == LogicalType.WeaponCase && p.weightType == weight) + Heaviest(p => p.type == LogicalType.WeaponMuzzle && p.weightType == weight));
            float limit = Parts.Values.Where(p => p.type == LogicalType.CassisCase && p.weightType == weight)
                .Select(p => p.partTierableStatistics[0].partSpesificData as ChassisCaseVO).Where(v => v != null)
                .Select(v => v.WeightLimit).DefaultIfEmpty(0f).Min();
            return weight + " " + Mathf.Round(build) + "/" + limit;
        }

        private static float Heaviest(Func<PartTemplate, bool> match)
        {
            return Parts.Values.Where(match).Select(p => p.partTierableStatistics[0].statistics.Weight.maxValue).DefaultIfEmpty(0f).Max();
        }

        public static bool IsSingleCaseModule(LogicalType type)
        {
            return type == LogicalType.HeadCase || type == LogicalType.HatCase || type == LogicalType.EyewearCase || type == LogicalType.DeviceCase;
        }

        private static PartTemplate BuildPart(string indent, string asset, GameObject go, PartPrefab prefab)
        {
            WeightType weight = WeightOf(indent, prefab.type);
            PartTemplate template = new PartTemplate
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
                keywords = prefab.keywords ?? new string[0],
                weightType = weight,
                minTier = 0,
                maxTier = MaxTier,
            };
            // Match what the client sees after protobuf + VoNormalizer, so
            // Utils.KeywordMach gives the same answer here as in the garage.
            foreach (DockingPointVO point in template.dockingPoints)
            {
                point.keywords = point.keywords ?? new string[0];
                point.possibleDockingTypes = point.possibleDockingTypes ?? new LogicalType[0];
            }
            template.partTierableStatistics = Enumerable.Range(0, MaxTier + 1).Select(tier => Stats(template, go, tier)).ToArray();
            return template;
        }

        // Light/Medium/Heavy from the H/M/L naming convention (HB_, MW_, LC_ ...).
        private static WeightType WeightOf(string indent, LogicalType type)
        {
            if (type != LogicalType.BodyCase && type != LogicalType.CassisCase && type != LogicalType.WeaponCase && type != LogicalType.WeaponMuzzle)
            {
                return WeightType.None;
            }
            WeightType described;
            if (Design.ClassOverrides.TryGetValue(indent, out described))
            {
                return described;
            }
            switch (indent.Length > 0 ? indent[0] : ' ')
            {
                case 'H': return WeightType.Heavy;
                case 'M': return WeightType.Medium;
                case 'L': return WeightType.Light;
                default: return WeightType.Medium;
            }
        }

        // Light/Medium/Heavy share of the original maximum for a stat.
        private static float ClassScale(WeightType weight)
        {
            switch (weight)
            {
                case WeightType.Light: return 0.6f;
                case WeightType.Medium: return 0.8f;
                default: return 1f;
            }
        }

        // Share of the heavy maxima for chassis, weapon case and barrel mass,
        // so a class's two-armed builds stay under its real weight limits.
        private static float MassScale(WeightType weight)
        {
            switch (weight)
            {
                case WeightType.Light: return 0.3f;
                case WeightType.Medium: return 0.6f;
                default: return 1f;
            }
        }

        // Top tier is 1.2 x tier 0: the bar maxima are 1.2 x the strongest real
        // part (chassis 1200 = 1.2 x Warhawk, body 1122 = 1.2 x Mantis).
        private const float TopTierGain = 0.2f;

        private static float Up(int tier)
        {
            return 1f + TopTierGain * tier / MaxTier;
        }

        // Designed values are top-tier maxima, so their tier 0 sits at max/1.2 like the real rows.
        private static float TierScale(int tier)
        {
            return Up(tier) / Up(MaxTier);
        }

        private static float Hundredths(float value)
        {
            return Mathf.Round(value * 100f) / 100f;
        }

        private static int WeaponSlots(PartTemplate template)
        {
            return template.dockingPoints.Count(d => d.isAssemblyRoot && d.possibleDockingTypes.Contains(LogicalType.Weapon));
        }

        private static Statistic Stat(float value)
        {
            return new Statistic { maxValue = value, currentValue = value, modifier = 0f };
        }

        private static string RowOf(PartTemplate template)
        {
            return RealStats.RowId(template.indent, template.type, template.weightType);
        }

        private static string Origin(PartTemplate template)
        {
            if (!RealStats.Covers(template.type))
            {
                return "designed";
            }
            string row = RowOf(template);
            return row == template.indent ? "real" : "real via " + row;
        }

        // Chassis, bodies and barrels use the real rows (RealStats). Everything
        // else is designed on the garage's stat-bar scale: each stat's maximum
        // per part type (GameplayDataExtactTools), e.g. weapon case durability
        // 628, battery capacity 270, is a top-tier heavy part; lighter classes
        // and lower tiers get a share of it, shifted by each part's described
        // role (Design.cs). Real rows grow with tier only in durability,
        // damage and splash; weights, limits, speeds and fire rates stay fixed.
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
                    ChassisType kind;
                    if (!Design.Chassis.TryGetValue(template.indent, out kind))
                    {
                        kind = ChassisTypeOf(go);
                    }
                    RealStats.ChassisRow row = RealStats.Chassis[RowOf(template)];
                    durability = Mathf.Round(row.Durability * Up(tier));
                    mass = 25.2f * m;
                    specific = new ChassisCaseVO
                    {
                        Speed = row.SpeedKmh / GlobalConstants.visualiseSpeedMod,
                        WeightLimit = row.WeightLimit,
                        EnergyConsumption = 0.001f,
                        TerrainPenaltyNegate = row.TerrainPenaltyNegate,
                        chassisType = kind,
                    };
                    break;
                }
                case LogicalType.Engine:
                {
                    Design.EngineTraits engine;
                    if (!Design.Engines.TryGetValue(template.indent, out engine))
                    {
                        engine = new Design.EngineTraits();
                    }
                    durability = 20f * t;
                    mass = 3f;
                    specific = new ChassisEngineVO { SpeedModifier = engine.Speed / GlobalConstants.visualiseSpeedMod, EnergyConsumption = 0.001f, WeightLimit = 40f * engine.WeightLimit };
                    break;
                }
                case LogicalType.BodyCase:
                {
                    // Four-armed bodies use more energy per shot.
                    bool fourArms = WeaponSlots(template) > 2;
                    RealStats.BodyRow row = RealStats.Bodies[RowOf(template)];
                    durability = Mathf.Round(row.Durability * Up(tier));
                    mass = row.Weight;
                    specific = new BodyCaseVO { EnergyConsumption = fourArms ? 0.002f : 0.001f, EnergyPerShootModifier = fourArms ? 1.3f : 1f };
                    break;
                }
                case LogicalType.Battery:
                {
                    // "The higher a battery's Capacity is - the lower its Regeneration rate is."
                    Design.BatteryTraits battery;
                    if (!Design.Batteries.TryGetValue(template.indent, out battery))
                    {
                        battery = new Design.BatteryTraits { Quality = 0.8f, HighCapacity = true };
                    }
                    durability = 1f;
                    mass = 1f;
                    specific = new BatteryCaseVO
                    {
                        EnergyCapacity = 270f * battery.Quality * t * (battery.HighCapacity ? 1f : 0.6f),
                        EnergyRegeneration = 335f * battery.Quality * t * (battery.HighCapacity ? 0.5f : 1f),
                    };
                    break;
                }
                case LogicalType.WeaponCase:
                {
                    Design.MechanismTraits mechanism;
                    if (!Design.Mechanisms.TryGetValue(template.indent, out mechanism))
                    {
                        mechanism = new Design.MechanismTraits();
                    }
                    durability = 628f * c * t;
                    mass = 25.2f * m;
                    // Case values multiply the barrel's (WeaponBarrel.GetDamage etc.).
                    specific = new WeaponCaseVO
                    {
                        fireDelay = mechanism.FireDelay,
                        accuracyModidifer = mechanism.Accuracy,
                        damageModidifer = mechanism.Damage,
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
            Design.BarrelTraits traits;
            if (Design.Barrels.TryGetValue(indent, out traits))
            {
                return traits;
            }
            bool rocket = keywords.Contains("Rocket");
            return new Design.BarrelTraits { Splash = rocket ? 0.8f : 0f, SplashRange = rocket ? 4f : 0f };
        }

        // Barrels whose description names projectiles fire ammo (AmmoDesign);
        // the rest are energy weapons: AmmoPerShot 0 (WeaponBarrel
        // .AreRequermentsMeet skips the ammo check) and MaxAmmo -1 (the garage
        // skips ammo distribution). CurrentAmmo must not be negative: the bot
        // AI sums it to decide whether to go looking for ammo instead of
        // fighting. Damage, splash and rate come from the barrel's real row; a
        // sibling's row is scaled by the two barrels' Design traits.
        private static WeaponBarrelVO Barrel(PartTemplate template, int tier)
        {
            Design.BarrelTraits traits = TraitsOf(template.indent, template.keywords);
            string rowId = RowOf(template);
            RealStats.BarrelRow row = RealStats.Barrels[rowId];
            Design.BarrelTraits rowTraits = TraitsOf(rowId, new string[0]);
            float shotDamage = Hundredths(row.Damage * traits.Damage / rowTraits.Damage * Up(tier));
            float splash = rowTraits.Splash > 0f ? Hundredths(row.Splash * traits.Splash / rowTraits.Splash * Up(tier)) : 0f;
            float shotsPerSecond = row.ShotsPerSecond * traits.RateOfFire / rowTraits.RateOfFire;
            float range = template.weightType == WeightType.Light ? 110f : template.weightType == WeightType.Heavy ? 200f : 150f;
            string[] ammo = AmmoDesign.TypesFor(template.indent);
            bool usesAmmo = ammo.Length > 0;
            return new WeaponBarrelVO
            {
                ActiveEfectToExecute = WeaponAction(template.indent),
                AdditionalEffects = new AdditionalGemaPlayActions[0],
                MaxAmmo = usesAmmo ? AmmoDesign.Capacity(template.weightType, traits) : -1f,
                CurrentAmmo = 0f,
                // Original UI maxima: accuracy 1, rate of fire 5/s, range 200.
                // RateOfFire is the delay between shots in seconds (WeaponBarrel.GetRateOfFire).
                Accuracy = Mathf.Min(1f, traits.Accuracy + 0.0125f * tier),
                RateOfFire = 1f / Mathf.Min(5f, shotsPerSecond),
                Damage = shotDamage,
                Range = Mathf.Min(200f, range * traits.Range),
                SpashDamage = splash,
                SpashRange = traits.SplashRange,
                AmmoPerShot = usesAmmo ? 1f : 0f,
                // Heavier hits cost more energy per shot (original maximum 19.2 at damage 96).
                EnergyConsumptionPerShoot = 19.2f * Mathf.Clamp(shotDamage / 96f, 0.15f, 1f) * (usesAmmo ? AmmoDesign.EnergyShare : 1f),
                usableAmmoTypes = ammo,
            };
        }

        private static readonly Dictionary<string, GamePlayActions> WeaponOverrides = new Dictionary<string, GamePlayActions>
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

        // Most barrels are named after their weapon action (LW_B_Revolver ->
        // Revolver); the rest are mapped by hand above.
        private static GamePlayActions WeaponAction(string indent)
        {
            GamePlayActions action;
            if (WeaponOverrides.TryGetValue(indent, out action))
            {
                return action;
            }
            string bare = Normalize(indent.Substring(indent.LastIndexOf('_') + 1));
            foreach (GamePlayActions candidate in Enum.GetValues(typeof(GamePlayActions)))
            {
                if (Normalize(candidate.ToString()) == bare)
                {
                    return candidate;
                }
            }
            RevivalLog.Info("No weapon action for " + indent + ", using AssaultRifle");
            return GamePlayActions.AssaultRifle;
        }

        private static string Normalize(string s)
        {
            return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

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

        // The prefab's own hover flag first; otherwise guessed from the model:
        // wheel colliders mean wheels, legs a walker, tracks tracked.
        private static ChassisType ChassisTypeOf(GameObject go)
        {
            ChassisCasePrefab chassis = go.GetComponent<ChassisCasePrefab>();
            if (chassis != null && chassis.hovering)
            {
                return ChassisType.Hover;
            }
            if (go.GetComponentsInChildren<WheelCollider>(true).Length > 0)
            {
                return ChassisType.Wheelled;
            }
            string names = string.Join(" ", go.GetComponentsInChildren<Transform>(true).Select(x => x.name.ToLowerInvariant()).ToArray());
            if (names.Contains("leg") || names.Contains("foot") || names.Contains("knee"))
            {
                return ChassisType.Walker;
            }
            if (names.Contains("track"))
            {
                return ChassisType.Tracked;
            }
            if (names.Contains("wheel") || names.Contains("tire") || names.Contains("tyre"))
            {
                return ChassisType.Wheelled;
            }
            return ChassisType.Hover;
        }

        private static PriceTemplate PriceFor(PartTemplate template)
        {
            float w = ClassScale(template.weightType) / 0.8f;
            float basePrice;
            switch (template.type)
            {
                case LogicalType.CassisCase: basePrice = 3000f * w; break;
                case LogicalType.BodyCase: basePrice = 3000f * w; break;
                case LogicalType.WeaponCase: basePrice = 1500f * w; break;
                case LogicalType.WeaponMuzzle: basePrice = 2000f * w; break;
                case LogicalType.Engine: basePrice = 1200f; break;
                case LogicalType.Battery: basePrice = 800f; break;
                case LogicalType.HeadCase: basePrice = 1500f; break;
                case LogicalType.DeviceCase: basePrice = 2500f; break;
                default: basePrice = 500f; break;
            }
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

        internal static TierLink[] Tiers(Func<int, float> credits, bool whole = true)
        {
            return Enumerable.Range(0, MaxTier + 1).Select(tier => new TierLink
            {
                tier = tier,
                items = new[] { new PricePackageItem { type = PackageType.Credits, quantity = whole ? Mathf.Round(credits(tier)) : credits(tier), indent = string.Empty } },
            }).ToArray();
        }

        public static float BuyPrice(string indent, int tier)
        {
            PriceTemplate price;
            return Prices.TryGetValue(indent, out price) ? CreditsAt(price.data.normalBuy, tier) : 0f;
        }

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

        public static float SellPrice(string indent, int tier)
        {
            PriceTemplate price;
            return Prices.TryGetValue(indent, out price) ? CreditsAt(price.data.normalSell, tier) : 0f;
        }

        private static float CreditsAt(TierLink[] links, int tier)
        {
            if (links == null)
            {
                return 0f;
            }
            foreach (TierLink link in links)
            {
                if (link.tier == tier && link.items != null)
                {
                    return link.items.Where(i => i.type == PackageType.Credits).Sum(i => i.quantity);
                }
            }
            return 0f;
        }

        private static string Describe(AbstractPartSpesificData data)
        {
            WeaponBarrelVO barrel = data as WeaponBarrelVO;
            if (barrel != null)
            {
                return "weapon " + barrel.ActiveEfectToExecute;
            }
            ChassisCaseVO chassis = data as ChassisCaseVO;
            if (chassis != null)
            {
                return "chassis " + chassis.chassisType;
            }
            DeviceCaseVO device = data as DeviceCaseVO;
            if (device != null)
            {
                return "device " + device.efectToExecute;
            }
            return data == null ? "-" : data.GetType().Name;
        }
    }
}
