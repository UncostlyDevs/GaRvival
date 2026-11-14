using System.Collections.Generic;
using JunkWars.Common.Robot.Enums;

namespace GarRevival
{
    // Per-part design traits, transcribed from each part's official in-game
    // description (<id>_Description in the localisation tables) and the
    // official wiki (gar.fandom.com). The original numbers are lost; these
    // keep every part's role relative to the others. Catalog.Stats() turns
    // them into numbers on the original game's scale.
    internal static class Design
    {
        // "Light Wheeler Chassis", "Heavy Tracker Chassis", ...
        // Wheelers are fastest but handle terrain worst; hovers are slowest but
        // ignore terrain; trackers are toughest; walkers carry the most.
        public static readonly Dictionary<string, ChassisType> Chassis = new Dictionary<string, ChassisType>
        {
            { "HC_AO", ChassisType.Tracked }, { "HC_Bahemoth", ChassisType.Walker }, { "HC_Bucephalas", ChassisType.Wheelled },
            { "HC_ChristmasSleigh", ChassisType.Hover }, { "HC_Daggerjaw", ChassisType.Wheelled }, { "HC_Raptor", ChassisType.Walker },
            { "HC_Rollover", ChassisType.Tracked }, { "HC_Warhawk", ChassisType.Hover },
            { "LC_Buzzer", ChassisType.Wheelled }, { "LC_Chicken", ChassisType.Walker }, { "LC_Creeper", ChassisType.Walker },
            { "LC_Drifter", ChassisType.Walker }, { "LC_Dune", ChassisType.Tracked }, { "LC_Dynamo", ChassisType.Hover },
            { "LC_Meerkat", ChassisType.Wheelled }, { "LC_Piranha", ChassisType.Hover }, { "LC_Tumbleweed", ChassisType.Hover },
            { "LC_Ural", ChassisType.Wheelled }, { "LC_Zip-Line", ChassisType.Wheelled },
            { "MC_Bugsy", ChassisType.Wheelled }, { "MC_Chimney", ChassisType.Hover }, { "MC_Cucaracha", ChassisType.Tracked },
            { "MC_Frog", ChassisType.Walker }, { "MC_HotRod", ChassisType.Wheelled }, { "MC_Maximus_Pride", ChassisType.Wheelled },
            { "MC_Off-Road_Warrior", ChassisType.Wheelled }, { "MC_Prospector", ChassisType.Walker }, { "MC_Samum", ChassisType.Hover },
            { "MC_Troll", ChassisType.Tracked }, { "MC_Wheelbarrow", ChassisType.Wheelled },
        };

        // Where the description's class differs from the id prefix.
        public static readonly Dictionary<string, WeightType> ClassOverrides = new Dictionary<string, WeightType>
        {
            { "MB_Headlight", WeightType.Heavy }, // "Heavy body: Accepts 4 Weapons and 2 Batteries"
        };

        // "A basic high-capacity, low-regen battery" ... basic 0.6, balanced
        // 0.8, advanced 1.0 of the original maximum.
        public sealed class BatteryTraits
        {
            public float Quality;
            public bool HighCapacity;
        }

        public static readonly Dictionary<string, BatteryTraits> Batteries = new Dictionary<string, BatteryTraits>
        {
            { "Battery_01", new BatteryTraits { Quality = 0.6f, HighCapacity = true } },  // Assault
            { "Battery_02", new BatteryTraits { Quality = 0.6f, HighCapacity = false } }, // Tactical
            { "Battery_03", new BatteryTraits { Quality = 0.8f, HighCapacity = true } },  // Protector
            { "Battery_04", new BatteryTraits { Quality = 0.8f, HighCapacity = false } }, // Skirmisher
            { "Battery_05", new BatteryTraits { Quality = 1f, HighCapacity = false } },   // Trickster
            { "Battery_06", new BatteryTraits { Quality = 1f, HighCapacity = true } },    // Master
        };

        // Additive speed and weight threshold, as a share of the original
        // maxima (+1 m/s, +-40 weight).
        public sealed class EngineTraits
        {
            public float Speed;
            public float WeightLimit;
        }

        public static readonly Dictionary<string, EngineTraits> Engines = new Dictionary<string, EngineTraits>
        {
            { "Engine_01", new EngineTraits() },                                       // Roach Box: no change
            { "Engine_02", new EngineTraits { Speed = 0.5f, WeightLimit = -0.5f } },  // Heavy Duty: +speed, -threshold
            { "Engine_03", new EngineTraits() },                                       // NoobEdge: no change
            { "Engine_04", new EngineTraits { Speed = -0.5f, WeightLimit = 0.75f } }, // Porter: +threshold, -speed
            { "Engine_05", new EngineTraits { Speed = 1f } },                          // Turbine: substantial +speed
            { "Engine_06", new EngineTraits { WeightLimit = 1f } },                    // Yak: +threshold
            { "Engine_Maximus_Power", new EngineTraits() },
        };

        // Firing mechanism multipliers on the barrels it holds.
        public sealed class MechanismTraits
        {
            public float RateOfFire = 1f;
            public float Accuracy = 1f;
            public float Damage = 1f;
            public float Energy = 1f;
            public float ProjectileSpeed = 1f;
            public float Splash = 1f;
        }

        public static readonly Dictionary<string, MechanismTraits> Mechanisms = new Dictionary<string, MechanismTraits>
        {
            { "HW_C_Ballistic", new MechanismTraits() },                                                     // basic heavy launcher
            { "HW_C_Cannon", new MechanismTraits { Damage = 1.15f, ProjectileSpeed = 1.2f, Splash = 1.15f } }, // missile speed + impact damage
            { "LW_C_Hiccs", new MechanismTraits { RateOfFire = 1.3f, Accuracy = 0.8f } },                   // semi-auto, less accurate
            { "LW_C_Injector", new MechanismTraits() },                                                     // basic small arms
            { "LW_C_Revolver", new MechanismTraits { RateOfFire = 1.2f } },                                 // +ROF
            { "LW_C_Trigun", new MechanismTraits { Damage = 0.5f } },                                       // 3 barrels, "50% DMG for 3 Barrels"
            { "MW_C_Aeroblaster", new MechanismTraits { Damage = 0.63f } },                                 // 2 barrels, "63% DMG each"
            { "MW_C_Arclight", new MechanismTraits { Damage = 1.15f, Energy = 1.1f } },                     // +damage, +energy use
            { "MW_C_Lancer", new MechanismTraits() },                                                       // basic assault
            { "MW_C_Maximus_Arm", new MechanismTraits() },
            { "MW_C_Maximus_Braclet", new MechanismTraits { RateOfFire = 1.2f } },
            { "MW_C_Staccato", new MechanismTraits { RateOfFire = 1.3f, Accuracy = 0.9f } },                // fully automatic
        };

        // Barrel role relative to its weight class. Accuracy is absolute
        // (0-1); Splash is a share of the original splash maximum.
        public sealed class BarrelTraits
        {
            public float Damage = 1f;
            public float RateOfFire = 1f;
            public float Accuracy = 0.85f;
            public float Range = 1f;
            public float Splash;
            public float SplashRange;
        }

        private static BarrelTraits B(float damage, float rate, float accuracy, float range, float splash = 0f, float splashRange = 0f)
        {
            return new BarrelTraits { Damage = damage, RateOfFire = rate, Accuracy = accuracy, Range = range, Splash = splash, SplashRange = splashRange };
        }

        public static readonly Dictionary<string, BarrelTraits> Barrels = new Dictionary<string, BarrelTraits>
        {
            // Light
            { "LW_B_AntKiller", B(1.3f, 0.6f, 0.95f, 1.4f) },        // Ant Killer: high precision, high damage
            { "LW_B_Arbalet", B(1.4f, 0.5f, 0.9f, 1.2f) },           // Reaper: high-velocity, high-damage bayonets
            { "LW_B_ElectroCutter", B(0.9f, 0.8f, 0.85f, 0.9f) },    // Stingray: low-velocity darts
            { "LW_B_Flintlock", B(1f, 1f, 0.85f, 1f) },              // good balance of damage and accuracy
            { "LW_B_Humiliator", B(0.8f, 0.7f, 0.8f, 0.9f, 0.6f, 4f) }, // corrosive balloons, splash
            { "LW_B_NailGun", B(0.55f, 2f, 0.6f, 0.7f) },            // Nailer: low accuracy, devastating rate of fire
            { "LW_B_Revolver", B(0.8f, 1.5f, 0.7f, 0.8f) },          // Slugger: close encounters
            { "LW_B_SawedOff", B(1.5f, 0.6f, 1f, 0.25f) },           // Sprayshot: extremely close, hits everything in the cone
            { "LW_B_Shotgun", B(1.2f, 0.7f, 0.7f, 0.5f) },           // Sandblaster: five-pellet cone
            { "LW_B_Slingshot", B(0.7f, 0.8f, 1f, 1.6f) },           // low damage, unsurpassed precision, from distance
            { "LW_B_SmallLaser", B(0.6f, 1.8f, 0.85f, 0.9f) },       // Pewpew: low damage, superior rate of fire
            { "LW_B_Trasher", B(1.5f, 0.6f, 1f, 0.25f) },            // extremely close, hits everything in the area
            // Medium
            { "MW_B_AcidCaster", B(0.8f, 0.8f, 0.85f, 0.8f, 0.8f, 5f) },        // Oxidian: area of effect
            { "MW_B_DeathRay", B(0.6f, 2f, 0.85f, 1f) },                        // high rate-of-fire energy pistol
            { "MW_B_FalmeCaster", B(1.5f, 1.5f, 1f, 0.25f) },                   // Dragonbelch: close range, huge damage
            { "MW_B_Katyusha", B(0.5f, 0.5f, 0.8f, 1f, 0.6f, 4f) },             // Hailfire: four missiles per shot
            { "MW_B_Laser", B(1.1f, 0.8f, 1f, 1.4f) },                          // Blaster: high-precision rifle
            { "MW_B_Machine Gun", B(0.8f, 1.8f, 0.75f, 1f) },                   // high rate of fire
            { "MW_B_Maximus_Justice", B(1.5f, 0.6f, 0.95f, 1.2f) },             // heavy bolt, devastating impact
            { "MW_B_Maximus_Revenge", B(1.4f, 0.7f, 0.95f, 1.2f) },             // disc with tremendous speed
            { "MW_B_Railgun", B(1.6f, 0.5f, 1f, 1.4f) },                        // powerful, penetrates walls
            { "MW_B_StickyBombLauncher", B(1.2f, 0.6f, 0.85f, 0.8f, 1f, 5f) }, // Chewing Gun: sticky explosive
            { "MW_B_WitchBow", B(1f, 0.8f, 0.85f, 1f, 0.7f, 4f) },              // pumpkins with splash damage
            // Heavy
            { "HW_B_Barry_Light", B(0.9f, 0.7f, 0.85f, 1f, 0.8f, 4f) },
            { "LW_B_Bertha", B(1f, 0.6f, 0.85f, 1f, 1f, 5f) },                  // Rocket Launcher: most powerful, devastating blast
            { "HW_B_ChristmasRocketLauncher", B(0.9f, 0.7f, 0.85f, 1f, 0.8f, 4f) },
            { "HW_B_Fragunov", B(1f, 0.7f, 1f, 1f) },                           // high-accuracy sniper rifle
            { "HW_B_MachineGun", B(0.35f, 4f, 0.7f, 0.8f) },                    // Thunderbolt: storm of bullets
            { "HW_B_Missile", B(0.8f, 1.3f, 0.85f, 1f, 0.5f, 2.5f) },           // narrow blast, short reload
            { "HW_B_Necrotron3000", B(0.9f, 1.2f, 0.95f, 1f) },                 // top-of-the-line energy caster
            { "HW_B_Plumber", B(1f, 0.8f, 0.7f, 0.4f) },                        // five-pellet cone
        };
    }
}
