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
    // prices never shipped with the client, so they are placeholders scaled by
    // part type and weight class - see Stats() and PriceFor().
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
                report.AppendLine(indent + " | " + template.type + " | " + template.weightType + " | " + Describe(template.partTierableStatistics[0].partSpesificData));
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

        // Share of a stat's top-tier value at each tier.
        private static float TierScale(int tier)
        {
            return 0.7f + 0.075f * tier;
        }

        private static Statistic Stat(float value)
        {
            return new Statistic { maxValue = value, currentValue = value, modifier = 0f };
        }

        // Placeholder balance anchored to the original game's scale: the
        // garage's stat bars (GameplayDataExtactTools) hard-code each stat's
        // maximum per part type, e.g. muzzle damage 96, range 200, rate of fire
        // 5, chassis durability 1200, speed 23. Top tier of a heavy part gets
        // the maximum; lighter classes and lower tiers get a share of it, and
        // each part's described role (Design.cs) shifts it from there.
        private static PartTierableStatistics Stats(PartTemplate template, GameObject go, int tier)
        {
            float c = ClassScale(template.weightType);
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
                    // Wheelers fastest, hovers slowest; trackers toughest;
                    // walkers carry the most; hovers ignore terrain.
                    float speed = kind == ChassisType.Wheelled ? 1f : kind == ChassisType.Hover ? 0.75f : 0.85f;
                    float tough = kind == ChassisType.Tracked ? 1f : kind == ChassisType.Walker ? 0.85f : kind == ChassisType.Hover ? 0.8f : 0.75f;
                    float carry = kind == ChassisType.Walker ? 1f : kind == ChassisType.Tracked ? 0.85f : 0.8f;
                    float terrain = kind == ChassisType.Hover ? 1f : kind == ChassisType.Tracked ? 0.5f : kind == ChassisType.Walker ? 0.3f : 0.1f;
                    durability = 1200f * c * t * tough;
                    mass = 25f * c;
                    specific = new ChassisCaseVO
                    {
                        // Light chassis are the fastest class (km/h; the client wants m/s).
                        Speed = (template.weightType == WeightType.Light ? 23f : template.weightType == WeightType.Medium ? 19f : 15f) * (0.85f + 0.0375f * tier) * speed / GlobalConstants.visualiseSpeedMod,
                        WeightLimit = 450f * c * t * carry,
                        EnergyConsumption = 0.001f,
                        TerrainPenaltyNegate = terrain,
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
                    specific = new ChassisEngineVO { SpeedModifier = engine.Speed * t / GlobalConstants.visualiseSpeedMod, EnergyConsumption = 0.001f, WeightLimit = 40f * engine.WeightLimit * t };
                    break;
                }
                case LogicalType.BodyCase:
                {
                    // Four-armed bodies: more firepower, less durability, more energy use.
                    bool fourArms = template.dockingPoints.Count(d => d.isAssemblyRoot && d.possibleDockingTypes.Contains(LogicalType.Weapon)) > 2;
                    durability = 1122f * c * t * (fourArms ? 0.8f : 1f);
                    mass = 200f * c;
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
                    mass = 25f * c;
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
                    mass = 25f * c;
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

        // Barrels whose description names projectiles fire ammo (AmmoDesign);
        // the rest are energy weapons: AmmoPerShot 0 (WeaponBarrel
        // .AreRequermentsMeet skips the ammo check) and MaxAmmo -1 (the garage
        // skips ammo distribution). CurrentAmmo must not be negative: the bot
        // AI sums it to decide whether to go looking for ammo instead of
        // fighting. Light weapons fire fast, heavy ones hit hard and reach further.
        private static WeaponBarrelVO Barrel(PartTemplate template, int tier)
        {
            float t = TierScale(tier);
            Design.BarrelTraits traits;
            if (!Design.Barrels.TryGetValue(template.indent, out traits))
            {
                bool rocket = template.keywords.Contains("Rocket");
                traits = new Design.BarrelTraits { Splash = rocket ? 0.8f : 0f, SplashRange = rocket ? 4f : 0f };
            }
            float damage;
            float rate;
            float range;
            switch (template.weightType)
            {
                case WeightType.Light: damage = 34f; rate = 2.5f; range = 110f; break;
                case WeightType.Heavy: damage = 96f; rate = 1f; range = 200f; break;
                default: damage = 58f; rate = 1.8f; range = 150f; break;
            }
            float shotDamage = damage * traits.Damage * t;
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
                RateOfFire = 1f / Mathf.Min(5f, rate * traits.RateOfFire),
                Damage = shotDamage,
                Range = Mathf.Min(200f, range * traits.Range),
                SpashDamage = 26.88f * traits.Splash * t,
                SpashRange = traits.SplashRange,
                AmmoPerShot = usesAmmo ? 1f : 0f,
                // Heavier hits cost more energy per shot (original maximum 19.2).
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
