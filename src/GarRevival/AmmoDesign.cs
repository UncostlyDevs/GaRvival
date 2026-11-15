using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using UnityEngine;

namespace GarRevival
{
    // The ammo the barrels fire, rebuilt from the localisation table's
    // Ammo_<Family>_<Variant>_Name/_Description entries and each barrel's
    // description. Capacities and prices are ours; every number lives here.
    internal static class AmmoDesign
    {
        // Ammo weapons also cost credits, so they use less energy per shot.
        public const float EnergyShare = 0.6f;

        // Full loads per barrel given to a new or migrated profile; buying a barrel includes one.
        public const int StarterLoads = 2;

        // Rounds a barrel holds before rate of fire and splash.
        private static readonly Dictionary<WeightType, float> ClassLoad = new Dictionary<WeightType, float>
        {
            { WeightType.Light, 240f }, { WeightType.Medium, 180f }, { WeightType.Heavy, 100f },
        };

        // A battle pays about 0.43 credits per damage point; at half the shots
        // hitting, ammo costs about a seventh of what it earns.
        private const float CreditsPerShotValue = 0.03f;
        private const float MinRoundPrice = 0.25f;

        // Barrels firing several projectiles per round.
        private static readonly Dictionary<string, int> ProjectilesPerRound = new Dictionary<string, int> { { "MW_B_Katyusha", 4 } };

        // The first type is what a new node loads. Barrels not listed are energy weapons.
        private static readonly Dictionary<string, string[]> BarrelTypes = new Dictionary<string, string[]>
        {
            // Light
            { "LW_B_Arbalet", T("Arrow_Base", "Arrow_Dmg") },
            { "LW_B_ElectroCutter", T("ElectroDart_Base", "Dart_Base", "Dart_Dmg") },
            { "LW_B_Flintlock", T("Pellet_Base", "Pellet_Dmg") },
            { "LW_B_Humiliator", T("Balloon_Base", "Balloon_Dmg", "Balloon_SpDmg", "Balloon_SpR") },
            { "LW_B_NailGun", T("Nail_Base", "Nail_Dmg") },
            { "LW_B_Revolver", T("Bullet_Free", "Bullet_Base", "Bullet_Dmg") },
            { "LW_B_SawedOff", T("Buck_Base", "Buck_Dmg") },
            { "LW_B_Shotgun", T("Shell_Base", "Shell_Dmg") },
            { "LW_B_Slingshot", T("Stone_Free", "Stone_Dmg") },
            { "LW_B_Trasher", T("Trash_Base", "Trash_Dmg") },
            // Medium
            { "MW_B_AcidCaster", T("Acidcaster_Base", "Acidcaster_Dmg") },
            { "MW_B_FalmeCaster", T("Napalm_Base", "Napalm_Dmg") },
            { "MW_B_Katyusha", T("Katyusha_Base", "Katyusha_Dmg", "Katyusha_SpDmg", "Katyusha_SpR") },
            { "MW_B_Machine Gun", T("MedBullet_Base", "MedBullet_Dmg") },
            { "MW_B_Maximus_Justice", T("Justice_Base", "Justice_Dmg") },
            { "MW_B_Maximus_Revenge", T("Revenge_Base", "Revenge_Dmg") },
            { "MW_B_Railgun", T("RailBullet_Base", "RailBullet_Dmg") },
            { "MW_B_StickyBombLauncher", T("StickyBombLauncher_Base", "StickyBombLauncher_Dmg", "StickyBombLauncher_SpDmg", "StickyBombLauncher_SpR") },
            { "MW_B_WitchBow", T("Pumpkin_Base", "Pumpkin_Dmg", "Pumpkin_SpDmg", "Pumpkin_SpR") },
            // Heavy
            { "LW_B_Bertha", T("Rocket_Base", "Rocket_SpDmg", "Rocket_SpR") },
            { "HW_B_Barry_Light", T("Rocket_Base", "Rocket_SpDmg", "Rocket_SpR") },
            { "HW_B_ChristmasRocketLauncher", T("XmassRocket_Base", "XmassRocket_SpDmg", "XmassRocket_SpR") },
            { "HW_B_Fragunov", T("Fragunov_Base", "Fragunov_Dmg") },
            { "HW_B_MachineGun", T("HeavyMGBullet_Base", "HeavyMGBullet_Dmg") },
            { "HW_B_Missile", T("Missile_Base", "Missile_Dmg", "Missile_SpDmg", "Missile_SpR") },
            { "HW_B_Plumber", T("Plumber_Base", "Plumber_Dmg") },
        };

        private sealed class Variant
        {
            public int Icon;
            public float PriceFactor;
            public float Damage = 1f, SplashDamage = 1f, SplashRange = 1f, ProjectileSpeed = 1f;
        }

        private static readonly Dictionary<string, Variant> Variants = new Dictionary<string, Variant>
        {
            { "Free", new Variant { Icon = 0, PriceFactor = 0f, Damage = 0.9f } }, // weaker, so paid ammo is worth buying
            { "Base", new Variant { Icon = 1, PriceFactor = 1f } },
            { "Dmg", new Variant { Icon = 2, PriceFactor = 1.75f, Damage = 1.15f } },
            { "SpDmg", new Variant { Icon = 3, PriceFactor = 1.75f, SplashDamage = 1.15f } },
            { "SpR", new Variant { Icon = 4, PriceFactor = 1.75f, SplashRange = 1.15f } },
            { "Spd", new Variant { Icon = 5, PriceFactor = 1.75f, ProjectileSpeed = 1.15f } },
        };

        private static string[] T(params string[] names)
        {
            return names.Select(n => "Ammo_" + n).ToArray();
        }

        private static IEnumerable<string> AllTypes
        {
            get { return BarrelTypes.Values.SelectMany(t => t).Distinct(); }
        }

        private static Variant VariantOf(string type)
        {
            return Variants[type.Substring(type.LastIndexOf('_') + 1)];
        }

        public static bool IsKnown(string type)
        {
            return type != null && AllTypes.Contains(type);
        }

        // Empty for energy weapons.
        public static string[] TypesFor(string barrelIndent)
        {
            string[] types;
            return BarrelTypes.TryGetValue(barrelIndent, out types) ? types : new string[0];
        }

        // The same at every tier, so an upgrade never changes a node's maximum.
        public static float Capacity(WeightType weight, Design.BarrelTraits traits)
        {
            float load;
            if (!ClassLoad.TryGetValue(weight, out load))
            {
                load = ClassLoad[WeightType.Medium];
            }
            return Mathf.Clamp(Mathf.Round(load * traits.RateOfFire * (traits.Splash > 0f ? 0.75f : 1f)), 30f, 900f);
        }

        // op 202. tierableData is indexed by the barrel's tier directly, and its
        // fields are a struct's, so every one must be set.
        public static Dictionary<string, AmmoTemplate> Templates()
        {
            return AllTypes.ToDictionary(type => type, type =>
            {
                Variant v = VariantOf(type);
                AmmoTemplateTierableData mods = new AmmoTemplateTierableData
                {
                    damageMod = v.Damage,
                    splashDamageMod = v.SplashDamage,
                    splashRangeMod = v.SplashRange,
                    projectileSpeedMod = v.ProjectileSpeed,
                    accuracyMod = 1f,
                    rangeMod = 1f,
                };
                return new AmmoTemplate
                {
                    indent = type,
                    uiData = new UIData { name = type + "_Name", description = type + "_Description", icon = "Textures/GUI/Shared/Ammo/Ammo_Type_" + v.Icon },
                    tierableData = Enumerable.Repeat(mods, Catalog.MaxTier + 1).ToArray(),
                };
            });
        }

        // Price per round by tier, from the damage a round does with the
        // barrels that fire it. A missing price would make the type free.
        public static Dictionary<string, PriceTemplate> Prices(IEnumerable<PartTemplate> barrels)
        {
            List<PartTemplate> list = barrels.ToList();
            return AllTypes.ToDictionary(type => type, type =>
            {
                List<PartTemplate> users = list.Where(b => TypesFor(b.indent).Contains(type)).ToList();
                return new PriceTemplate
                {
                    indent = type,
                    data = new PriceDataTemplate
                    {
                        normalBuy = Catalog.Tiers(tier => RoundPrice(type, users.Count == 0 ? 0f : users.Average(b => ShotValue(b, tier))), false),
                    },
                };
            });
        }

        private static float ShotValue(PartTemplate barrel, int tier)
        {
            WeaponBarrelVO stats = barrel.partTierableStatistics[tier].partSpesificData as WeaponBarrelVO;
            int projectiles;
            if (!ProjectilesPerRound.TryGetValue(barrel.indent, out projectiles))
            {
                projectiles = 1;
            }
            return stats == null ? 0f : (stats.Damage + stats.SpashDamage) * projectiles;
        }

        // Whole eighths of a credit add up exactly in floats, so the client's
        // Ceil of the total matches the server's.
        private static float RoundPrice(string type, float shotValue)
        {
            float perRound = Math.Max(MinRoundPrice, Mathf.Round(CreditsPerShotValue * shotValue * 8f) / 8f);
            return Mathf.Round(perRound * VariantOf(type).PriceFactor * 32f) / 32f;
        }
    }
}
