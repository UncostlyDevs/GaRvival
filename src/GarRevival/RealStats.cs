using System.Collections.Generic;
using JunkWars.Common.Robot.Enums;

namespace GarRevival
{
    // Tier-0 part stats from the last live balance, transcribed from the tables in
    // https://steamcommunity.com/sharedfiles/filedetails/?id=1105439577 (Aug 2017).
    // Durability is the guide's value / 0.35: whole numbers whose top tier (x1.2)
    // lands on the garage's bar maxima (Warhawk 1000 -> 1200, Mantis 935 -> 1122).
    // Weights, speeds (km/h), weight limits and damage are as published.
    internal static class RealStats
    {
        public sealed class ChassisRow
        {
            public float Durability;
            public float SpeedKmh;
            public float WeightLimit;
            public float TerrainPenaltyNegate;
        }

        public sealed class BodyRow
        {
            public float Durability;
            public float Weight;
        }

        // Damage and Splash per projectile; Acidcaster splash is per tick (shown x2 /s).
        public sealed class BarrelRow
        {
            public float Damage;
            public float Splash;
            public float ShotsPerSecond;
        }

        // Terrain as the guide rates it: + good, o average, - poor.
        private static ChassisRow C(float durability, float speedKmh, float weightLimit, char terrain)
        {
            float negate = terrain == '+' ? 1f : terrain == 'o' ? 0.5f : 0.1f;
            return new ChassisRow { Durability = durability, SpeedKmh = speedKmh, WeightLimit = weightLimit, TerrainPenaltyNegate = negate };
        }

        private static BodyRow B(float durability, float weight)
        {
            return new BodyRow { Durability = durability, Weight = weight };
        }

        private static BarrelRow W(float damage, float splash, float shotsPerSecond)
        {
            return new BarrelRow { Damage = damage, Splash = splash, ShotsPerSecond = shotsPerSecond };
        }

        public static readonly Dictionary<string, ChassisRow> Chassis = new Dictionary<string, ChassisRow>
        {
            { "LC_Meerkat", C(310, 23, 129.9f, '-') }, { "LC_Dynamo", C(305, 20, 160.2f, '+') }, { "LC_Drifter", C(340, 22, 140.1f, 'o') },
            { "LC_Dune", C(391, 21, 150.3f, 'o') }, { "LC_Ural", C(418, 23, 128.9f, '-') }, { "LC_Zip-Line", C(256, 23, 129.7f, '-') },
            { "LC_Piranha", C(465, 20, 160.4f, '+') }, { "LC_Tumbleweed", C(525, 20, 160.5f, '+') }, { "LC_Buzzer", C(364, 23, 130f, '-') },
            { "LC_Chicken", C(445, 22, 140f, 'o') }, { "LC_Creeper", C(415, 22, 140.2f, 'o') },
            { "MC_Off-Road_Warrior", C(510, 19, 255f, '-') }, { "MC_Frog", C(477, 18, 268f, 'o') }, { "MC_Cucaracha", C(673, 17, 282.3f, 'o') },
            { "MC_Prospector", C(563, 18, 268.2f, 'o') }, { "MC_Wheelbarrow", C(635, 17, 282.1f, 'o') }, { "MC_Troll", C(628, 17, 282.1f, 'o') },
            { "MC_Samum", C(512, 16, 295.2f, '+') }, { "MC_Bugsy", C(501, 19, 255f, '-') }, { "MC_Chimney", C(693, 16, 295.3f, '+') },
            { "MC_HotRod", C(460, 19, 254.8f, '-') },
            { "HC_Daggerjaw", C(725, 15, 399.8f, '-') }, { "HC_Raptor", C(799, 14, 417f, 'o') }, { "HC_Warhawk", C(1000, 12, 450f, '+') },
            { "HC_AO", C(876, 13, 433f, 'o') }, { "HC_Bahemoth", C(706, 14, 416.8f, 'o') }, { "HC_Bucephalas", C(815, 15, 400f, '-') },
            { "HC_Rollover", C(854, 13, 433f, 'o') },
        };

        public static readonly Dictionary<string, BodyRow> Bodies = new Dictionary<string, BodyRow>
        {
            { "LB_Lizgar", B(402, 40.1f) }, { "LB_Jackhammer", B(352, 40f) }, { "LB_Ghast", B(248, 195f) }, { "LB_Spitfire", B(253, 195.3f) },
            { "LB_Shinobi", B(297, 39.8f) }, { "LB_Sheriff", B(303, 40f) }, { "LB_Flex", B(407, 40.2f) }, { "LB_Bandit", B(303, 40.1f) },
            { "LB_Firefighter", B(377, 39.9f) },
            { "MB_Dervish", B(273, 260.2f) }, { "MB_Clunker", B(449, 99.9f) }, { "MB_Eimer-Kreiger", B(301, 260.3f) },
            { "MB_Six_Strings_Samurai", B(468, 103f) }, { "MB_Electra", B(745, 100.3f) }, { "MB_Chromax", B(511, 100f) },
            { "HB_Chopper", B(933, 160f) }, { "HB_Pazersturm", B(410, 340.2f) }, { "HB_Mantis", B(935, 160f) }, { "HB_Forge", B(406, 340f) },
            { "HB_GasLeak", B(812, 159.9f) }, { "MB_Headlight", B(406, 339.8f) },
        };

        public static readonly Dictionary<string, BarrelRow> Barrels = new Dictionary<string, BarrelRow>
        {
            { "LW_B_Revolver", W(17.36f, 0f, 2.3f) }, { "LW_B_SawedOff", W(29.4f, 0f, 1f) }, { "LW_B_Humiliator", W(21.77f, 7.77f, 1f) },
            { "LW_B_NailGun", W(8.29f, 0f, 4f) }, { "LW_B_SmallLaser", W(11.1f, 0f, 3f) }, { "LW_B_Trasher", W(35.41f, 0f, 0.7f) },
            { "LW_B_Shotgun", W(5.25f, 0f, 1f) }, { "LW_B_Slingshot", W(16.24f, 0f, 1.8f) }, { "LW_B_Arbalet", W(28.24f, 0f, 1.3f) },
            { "LW_B_AntKiller", W(22.61f, 0f, 1.3f) }, { "LW_B_Flintlock", W(21.34f, 0f, 1.6f) }, { "LW_B_ElectroCutter", W(35.92f, 0f, 1.1f) },
            { "MW_B_AcidCaster", W(6.27f, 9.645f, 2f) }, { "MW_B_DeathRay", W(11.03f, 0f, 2.5f) }, { "MW_B_Katyusha", W(11.2f, 4.2f, 0.5f) },
            { "MW_B_Machine Gun", W(9.72f, 0f, 3f) }, { "MW_B_FalmeCaster", W(8.89f, 0f, 5f) }, { "MW_B_Laser", W(30.56f, 0f, 1f) },
            { "MW_B_StickyBombLauncher", W(0f, 5.6f, 2f) }, { "MW_B_Railgun", W(11.93f, 0f, 2f) },
            { "LW_B_Bertha", W(49f, 15.68f, 0.5f) }, { "HW_B_Fragunov", W(31.35f, 0f, 0.7f) }, { "HW_B_Necrotron3000", W(31.35f, 0f, 0.7f) },
            { "HW_B_Plumber", W(11.2f, 0f, 0.6f) }, { "HW_B_MachineGun", W(13.85f, 0f, 2f) }, { "HW_B_Missile", W(39.2f, 11.9f, 0.7f) },
        };

        // Parts the guide leaves out take the row of the closest real part
        // (barrels scaled by their Design.Barrels traits).
        private static readonly Dictionary<string, string> Siblings = new Dictionary<string, string>
        {
            { "HC_ChristmasSleigh", "HC_Warhawk" },          // heavy hover
            { "MC_Maximus_Pride", "MC_Bugsy" },              // medium wheeler
            { "LB_Cuckoo", "LB_Jackhammer" },                // light, two arms
            { "MB_Maximus_Lagacy", "MB_Chromax" },           // medium, two arms
            { "HW_B_Barry_Light", "LW_B_Bertha" },           // rockets
            { "HW_B_ChristmasRocketLauncher", "LW_B_Bertha" },
            { "MW_B_Maximus_Justice", "MW_B_Laser" },        // single heavy shots
            { "MW_B_Maximus_Revenge", "MW_B_Laser" },
            { "MW_B_WitchBow", "MW_B_Katyusha" },            // splash projectiles
        };

        public static bool Covers(LogicalType type)
        {
            return type == LogicalType.CassisCase || type == LogicalType.BodyCase || type == LogicalType.WeaponMuzzle;
        }

        // The row a chassis, body or barrel takes its numbers from: its own,
        // a named sibling's, or a plain part of its class.
        public static string RowId(string indent, LogicalType type, WeightType weight)
        {
            string sibling;
            if (Chassis.ContainsKey(indent) || Bodies.ContainsKey(indent) || Barrels.ContainsKey(indent))
            {
                return indent;
            }
            if (Siblings.TryGetValue(indent, out sibling))
            {
                return sibling;
            }
            int i = weight == WeightType.Light ? 0 : weight == WeightType.Heavy ? 2 : 1;
            switch (type)
            {
                case LogicalType.CassisCase: return new[] { "LC_Buzzer", "MC_Bugsy", "HC_Daggerjaw" }[i];
                case LogicalType.BodyCase: return new[] { "LB_Jackhammer", "MB_Chromax", "HB_Chopper" }[i];
                default: return new[] { "LW_B_Flintlock", "MW_B_Laser", "HW_B_Fragunov" }[i];
            }
        }
    }
}
