using JunkWars.Common.Inventory;

namespace GarRevival
{
    // Every number in the cosmetics area that the original servers kept in
    // their database and that is now lost: the paint palette, what things
    // cost, what crates hold and how often they drop. The client fixes the
    // shape (which currency a price is read in, the four colour categories,
    // the lock-pick chances it prints); the amounts are ours.
    internal static class CosmeticsDesign
    {
        internal sealed class ColorDef
        {
            public int Index;
            public string Name;
            public string Category;
            public int Rgb;
            public bool CrateOnly;
        }

        // The client has localised labels for exactly these four categories
        // (Label_TintCategory_<name>).
        public const string Starter = "Starter";
        public const string Common = "Common";
        public const string Rare = "Rare";
        public const string Epic = "Epic";

        // Sparks per colour. BuyColorAction only reads the spark part of a
        // colour's price, so colours cannot be sold for credits.
        public static float ColorPrice(string category)
        {
            switch (category)
            {
                case Common: return 5f;
                case Rare: return 15f;
                case Epic: return 40f;
                default: return 0f; // Starter colours come with the profile
            }
        }

        // Indexes are saved inside every painted part: never renumber, only add.
        // Crate-only colours stay hidden in the paint screen until owned.
        public static readonly ColorDef[] Colors =
        {
            C(0, "White", Starter, 0xF2F2F2), C(1, "Black", Starter, 0x1E1E1E), C(2, "Grey", Starter, 0x808080), C(3, "Red", Starter, 0xC0281E),
            C(4, "Blue", Starter, 0x1F4FB4), C(5, "Yellow", Starter, 0xE8C21A), C(6, "Green", Starter, 0x2E8B3A), C(7, "Orange", Starter, 0xE0701A),

            C(100, "Navy", Common, 0x14284F), C(101, "SkyBlue", Common, 0x5AA9E6), C(102, "Teal", Common, 0x1E8C8C), C(103, "Olive", Common, 0x6B7A2A),
            C(104, "Lime", Common, 0x8FD12B), C(105, "Brown", Common, 0x6B4423), C(106, "Sand", Common, 0xD2B48C), C(107, "Maroon", Common, 0x6E1423),
            C(108, "Pink", Common, 0xE87AA4), C(109, "Purple", Common, 0x6A2C91), C(110, "Cream", Common, 0xF0E6C8), C(111, "Charcoal", Common, 0x3A3F44),

            C(200, "Crimson", Rare, 0xDC143C), C(201, "Cobalt", Rare, 0x0047AB), C(202, "Emerald", Rare, 0x0FA36B), C(203, "Amber", Rare, 0xFFB000),
            C(204, "Violet", Rare, 0x8F00FF), C(205, "Turquoise", Rare, 0x30D5C8), C(206, "Magenta", Rare, 0xD4148C), C(207, "Rust", Rare, 0xB7410E),
            C(220, "Midnight", Rare, 0x0B1026, true), C(221, "Mint", Rare, 0x98E6B8, true), C(222, "Coral", Rare, 0xFF6F61, true), C(223, "Brass", Rare, 0xB08D57, true),

            C(300, "Gold", Epic, 0xFFD700), C(301, "Chrome", Epic, 0xC8D0D8), C(302, "NeonGreen", Epic, 0x39FF14), C(303, "HotPink", Epic, 0xFF1493),
            C(304, "ElectricBlue", Epic, 0x00BFFF), C(305, "Lava", Epic, 0xFF4500),
            C(320, "Ultraviolet", Epic, 0x5B0FCE, true), C(321, "Obsidian", Epic, 0x0A0A0A, true), C(322, "Plasma", Epic, 0x00FFD0, true), C(323, "Bloodmoon", Epic, 0x8B0000, true),
        };

        private static ColorDef C(int index, string name, string category, int rgb, bool crateOnly = false)
        {
            return new ColorDef { Index = index, Name = name, Category = category, Rgb = rgb, CrateOnly = crateOnly };
        }

        // The paint screen charges one spray per part painted or stripped.
        public const int PaintSpraysPerPart = 1;

        // Store packs: { quantity, price }. Sprays and multi-tools cost credits,
        // keys cost sparks: those are the currencies the crate window prints.
        // Quantities are the ones the game has pile models for.
        public static readonly int[][] PaintPacks = { new[] { 1, 500 }, new[] { 5, 2250 }, new[] { 10, 4000 }, new[] { 20, 7000 } };
        public static readonly int[][] LockpickPacks = { new[] { 1, 2000 }, new[] { 5, 9000 } };
        public static readonly int[] KeyPackSizes = { 1, 5 };
        public const float KeyBulkDiscount = 0.9f;

        internal sealed class CrateDef
        {
            public CrateType Type;
            public int KeySparks;
            // Always in the crate. The upper figures are for a top-rank player.
            public int CreditsMin, CreditsMax, CreditsMinTopRank, CreditsMaxTopRank;
            public float SparksChance;
            public int SparksMin, SparksMax;
            public float PaintChance;
            public int PaintMin, PaintMax;
            public float LockpickChance;
            // One colour the player does not own yet, by name.
            public float ColorChance;
            public string[] Colors;
            // One seasonal or set item: a part id, or "Mod_<case id>" for a ready-made module.
            public float ItemChance;
            public string[] Items;
        }

        public static readonly CrateDef[] Crates =
        {
            new CrateDef
            {
                Type = CrateType.Bronze, KeySparks = 3,
                CreditsMin = 500, CreditsMax = 1500, CreditsMinTopRank = 1000, CreditsMaxTopRank = 3000,
                SparksChance = 0.10f, SparksMin = 2, SparksMax = 5,
                PaintChance = 0.60f, PaintMin = 1, PaintMax = 2,
                LockpickChance = 0.15f,
                ColorChance = 0.25f, Colors = new[] { "Navy", "Teal", "Olive", "Brown" },
                ItemChance = 0.05f, Items = new[] { "Mod_AE_Halloween_Frankie", "Mod_AE_Halloween_Mummy", "Mod_AE_Halloween_Scream", "Mod_AE_Halloween_Skull" },
            },
            new CrateDef
            {
                Type = CrateType.Silver, KeySparks = 8,
                CreditsMin = 1500, CreditsMax = 4000, CreditsMinTopRank = 3000, CreditsMaxTopRank = 8000,
                SparksChance = 0.25f, SparksMin = 5, SparksMax = 10,
                PaintChance = 0.70f, PaintMin = 1, PaintMax = 3,
                LockpickChance = 0.25f,
                ColorChance = 0.35f, Colors = new[] { "Midnight", "Mint", "Coral", "Brass" },
                ItemChance = 0.10f, Items = new[] { "Mod_AH_SantaHat", "Mod_AH_Rudolph", "Mod_AH_Christmas_Elf", "Mod_AH_Valantine_Fool" },
            },
            new CrateDef
            {
                Type = CrateType.Gold, KeySparks = 20,
                CreditsMin = 4000, CreditsMax = 10000, CreditsMinTopRank = 8000, CreditsMaxTopRank = 20000,
                SparksChance = 0.50f, SparksMin = 10, SparksMax = 25,
                PaintChance = 0.80f, PaintMin = 2, PaintMax = 4,
                LockpickChance = 0.35f,
                ColorChance = 0.40f, Colors = new[] { "Ultraviolet", "Obsidian", "Plasma", "Bloodmoon" },
                ItemChance = 0.20f, Items = new[] { "Mod_HE_Pumpkin", "Mod_HE_Snowman", "Mod_Device_EggPunch", "Mod_AH_Easter_Commando" },
            },
            new CrateDef
            {
                Type = CrateType.Platinum, KeySparks = 45,
                CreditsMin = 10000, CreditsMax = 25000, CreditsMinTopRank = 20000, CreditsMaxTopRank = 50000,
                SparksChance = 1f, SparksMin = 25, SparksMax = 60,
                PaintChance = 1f, PaintMin = 3, PaintMax = 6,
                LockpickChance = 0.50f,
                ColorChance = 1f, Colors = new[] { "Ultraviolet", "Obsidian", "Plasma", "Bloodmoon" },
                ItemChance = 0.40f, Items = new[] { "HC_ChristmasSleigh", "HW_B_ChristmasRocketLauncher", "MW_B_Maximus_Justice", "MW_B_Maximus_Revenge" },
            },
        };

        // What a finished battle can leave in the inventory. Each chance is
        // multiplied by the performance factor below and capped at 100%.
        public static readonly float[] BattleCrateChance = { 0.30f, 0.12f, 0.04f, 0.01f }; // Bronze, Silver, Gold, Platinum
        public const float BattlePaintChance = 0.30f;
        public const int BattlePaintMin = 1, BattlePaintMax = 2;
        public const float BattleLockpickChance = 0.08f;
        public const float WinFactor = 0.5f;
        public const float KillFactor = 0.1f;
        public const float MaxKillFactor = 0.5f;

        // The loot tables can scale with a 1..10 rank; ours follows the level.
        public const int LevelsPerRank = 5;

        // Daily login streak, one entry per day in a row. The reward window has
        // fifteen day tiles and shows one number per tile, so each day pays a
        // single currency: { credits, sparks }. The window marks days 5, 10
        // and 15 with three stars and a sparks icon.
        public static readonly int[][] DailyLogin =
        {
            new[] { 1000, 0 }, new[] { 1500, 0 }, new[] { 2000, 0 }, new[] { 3000, 0 }, new[] { 0, 25 },
            new[] { 3500, 0 }, new[] { 4000, 0 }, new[] { 5000, 0 }, new[] { 6000, 0 }, new[] { 0, 50 },
            new[] { 8000, 0 }, new[] { 10000, 0 }, new[] { 12000, 0 }, new[] { 15000, 0 }, new[] { 0, 100 },
        };

        // One-off welcome pack (operation 147; only some builds of the client ask for it).
        public const int SpecialPackPaint = 10;
        public const int SpecialPackLockpicks = 3;
        public const int SpecialPackSparks = 50;
        public const CrateType SpecialPackCrate = CrateType.Bronze;
    }
}
