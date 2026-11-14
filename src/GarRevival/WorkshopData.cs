using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Utils;

namespace GarRevival
{
    // The workshop's startup data: the tech tree and part-upgrade table, the
    // timed boosts and the consumable price list. The original tables lived in
    // the server database and are lost; every number here is designed. What is
    // not designed is the shape, which the client fixes: tech tiers 1-10 (1-3
    // for devices), boosts named CR / XP / REPAIRS / VIP with six VIP plans,
    // rent for 3 days at 10% of the selling price.
    internal static class WorkshopData
    {
        // ---- designed values ----

        // Seconds to reach a tier from the one below. Index = tier.
        private static readonly int[] UpgradeSeconds = { 0, 60, 300, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400 };

        // Player level at which a tech tier unlocks by itself. Index = tier.
        private static readonly int[] UnlockAtLevel = { 1, 2, 5, 10, 15, 20, 30, 40, 50, 65, 80 };
        private static readonly int[] DeviceUnlockAtLevel = { 1, 5, 15, 30 };

        // Sparks to unlock a tech tier early, and to skip a whole tier's
        // upgrade time (the client charges the share that is still left).
        private const float UnlockSparksPerTier = 20f;
        private const float HurrySparksPerTier = 5f;

        // Boosts: minutes and sparks per plan. Scale 1 doubles credits or
        // experience and makes repairs free (the client's own formulas).
        private static readonly int[] BoostMinutes = { 60, 1440, 10080 };
        private static readonly float[] BoostSparks = { 10f, 60f, 300f };
        // The VIP screen has six plan buttons (indexes 0-5).
        private static readonly int[] VipMinutes = { 1440, 4320, 10080, 43200, 129600, 259200 };
        private static readonly float[] VipSparks = { 30f, 80f, 150f, 500f, 1200f, 2000f };

        // Credits for one consumable.
        private static readonly Dictionary<ConsumableBoostType, float> ConsumableCredits = new Dictionary<ConsumableBoostType, float>
        {
            { ConsumableBoostType.SpeedUp, 250f },
            { ConsumableBoostType.EnergyRefill, 250f },
            { ConsumableBoostType.ChassisRepair, 400f },
            { ConsumableBoostType.HeadRepair, 400f },
        };

        // "Rent this part for 10% of its Selling Price", "Rent for: 3 days".
        public const float RentShareOfSellPrice = 0.1f;
        public const int RentDays = 3;

        // The exchange window's own limit per transaction.
        public const float MaxSparksPerExchange = 100000f;

        // ---- fixed by the client ----

        // The tech tree screen has slots for tiers 1-10, three for devices.
        private const int OriginalMaxTier = 10;
        private const int OriginalMaxDeviceTier = 3;

        public const string PackagePrefix = "Pack_ConsumableBoost_";

        // ---- tech tree and upgrade times ----

        private static Dictionary<TechCategory, PartsUpgardeCategoryData> _upgrades;

        // Replaces DataFactory.Upgrades().
        public static Dictionary<TechCategory, PartsUpgardeCategoryData> Upgrades()
        {
            return UpgradeTable;
        }

        public static Dictionary<TechCategory, PartsUpgardeCategoryData> UpgradeTable
        {
            get
            {
                if (_upgrades == null)
                {
                    _upgrades = new Dictionary<TechCategory, PartsUpgardeCategoryData>();
                    foreach (TechCategory category in Enum.GetValues(typeof(TechCategory)))
                    {
                        _upgrades[category] = Category(category);
                    }
                }
                return _upgrades;
            }
        }

        // The arrays cover all ten tiers even when fewer can be reached: the
        // tech tree's tooltips index them for every slot on screen.
        private static PartsUpgardeCategoryData Category(TechCategory category)
        {
            bool device = category == TechCategory.Device;
            int[] levels = Enumerable.Range(0, OriginalMaxTier + 1)
                .Select(tier => device ? (tier < DeviceUnlockAtLevel.Length ? DeviceUnlockAtLevel[tier] : CommonConstants.MaxUserLevel) : UnlockAtLevel[tier]).ToArray();
            return new PartsUpgardeCategoryData
            {
                category = category,
                // No tech tier beyond what the catalog's parts can use.
                maxTechTier = Math.Min(Catalog.MaxTier, device ? OriginalMaxDeviceTier : OriginalMaxTier),
                tierUpgardeDuration = (int[])UpgradeSeconds.Clone(),
                selfUnlockPerPlayerLevel = levels,
                tierForceUnlockPrice = Sparks(tier => UnlockSparksPerTier * tier),
                tierHurryPrice = Sparks(tier => HurrySparksPerTier * tier),
            };
        }

        private static PricePackageItem[] Sparks(Func<int, float> price)
        {
            return Enumerable.Range(0, OriginalMaxTier + 1)
                .Select(tier => new PricePackageItem { type = PackageType.PremiumCredits, quantity = price(tier), indent = string.Empty }).ToArray();
        }

        // The parts the garage's upgrade screen lists (UpgradeManager.IsUpgradeable).
        public static PartsUpgardeCategoryData CategoryOf(LogicalType partType)
        {
            switch (partType)
            {
                case LogicalType.CassisCase:
                case LogicalType.BodyCase:
                case LogicalType.WeaponCase:
                case LogicalType.WeaponMuzzle:
                case LogicalType.HeadCase:
                case LogicalType.DeviceCase:
                    break;
                default:
                    return null;
            }
            TechCategory category;
            PartsUpgardeCategoryData data;
            return EnumUtils.GetTechCategoryByPartType(partType, out category) && UpgradeTable.TryGetValue(category, out data) ? data : null;
        }

        public static long UpgradeTicks(PartsUpgardeCategoryData data, int tier)
        {
            return tier >= 0 && tier < data.tierUpgardeDuration.Length ? data.tierUpgardeDuration[tier] * TimeSpan.TicksPerSecond : 0L;
        }

        // The highest tier a player of this level has without paying.
        public static int TierUnlockedAtLevel(PartsUpgardeCategoryData data, int level)
        {
            int tier = 0;
            while (tier < data.maxTechTier && data.selfUnlockPerPlayerLevel[tier + 1] <= level)
            {
                tier++;
            }
            return tier;
        }

        // ---- timed boosts ----

        private static Dictionary<string, TimedBuffTemplate> _buffs;

        // Replaces DataFactory.TimedBuffs(). The ids are the ones the client
        // has names and icons for; the keyword is what each one does
        // (DataManager.GetBonusCreditsCoef and friends look it up).
        public static Dictionary<string, TimedBuffTemplate> TimedBuffs()
        {
            if (_buffs == null)
            {
                _buffs = new Dictionary<string, TimedBuffTemplate>
                {
                    { "CR", Buff("CR", CommonConstants.BONUS_CREDITS, BoostMinutes, BoostSparks) },
                    { "XP", Buff("XP", CommonConstants.BONUS_EXPERIENCE, BoostMinutes, BoostSparks) },
                    { "REPAIRS", Buff("REPAIRS", CommonConstants.BONUS_REPAIRS, BoostMinutes, BoostSparks) },
                    { "VIP", Buff("VIP", CommonConstants.VIP, VipMinutes, VipSparks) },
                };
            }
            return _buffs;
        }

        private static TimedBuffTemplate Buff(string indent, string keyword, int[] minutes, float[] sparks)
        {
            return new TimedBuffTemplate
            {
                indent = indent,
                // Never empty: the client copies this list without a null check.
                keywords = new List<string> { keyword },
                duration = (int[])minutes.Clone(),
                showInShop = true,
                scale = 1f,
                price = sparks.Select(s => new PricePackageGroup
                {
                    items = new[] { new PricePackageItem { type = PackageType.PremiumCredits, quantity = s, indent = string.Empty } },
                }).ToArray(),
            };
        }

        // ---- consumables ----

        private static List<PackagePriceTemplate.PricePackage> _packages;

        // One package per battle consumable; the Supply screen looks them up
        // by item type to show a price and to buy (operation 218, type Package).
        // Kept out of the store list so only that screen sells them.
        public static List<PackagePriceTemplate.PricePackage> ConsumablePackages()
        {
            if (_packages == null)
            {
                _packages = ConsumableCredits.Select(entry => new PackagePriceTemplate.PricePackage
                {
                    indent = PackagePrefix + entry.Key,
                    showInShop = false,
                    itemPackage = new Package
                    {
                        items = new List<PackageItem> { new PackageItem { type = PackageType.ConsumableBoost, quantity = 1f, indent = entry.Key.ToString() } },
                    },
                    normalBuy = new[] { new PricePackageItem { type = PackageType.Credits, quantity = entry.Value, indent = string.Empty } },
                }).ToList();
            }
            return _packages;
        }

        // Replaces DataFactory.PackagePrices(); other areas can add their own
        // packages to the list it returns.
        public static PackagePriceTemplate PackagePrices()
        {
            PackagePriceTemplate template = new PackagePriceTemplate();
            template.packages.AddRange(ConsumablePackages());
            return template;
        }

        // ---- prices kept in the catalog ----

        public static PricePackageItem[] PriceAt(TierLink[] links, int tier)
        {
            if (links != null)
            {
                foreach (TierLink link in links)
                {
                    if (link.tier == tier && link.items != null)
                    {
                        return link.items;
                    }
                }
            }
            return new PricePackageItem[0];
        }

        // Credits to rent one part: the catalog's rent price when it has one
        // (that is what the store shows), otherwise the original rule.
        public static float RentCredits(string partIndent)
        {
            PriceTemplate price;
            if (!Catalog.Prices.TryGetValue(partIndent, out price))
            {
                return 0f;
            }
            PartTemplate template;
            int tier = Catalog.Parts.TryGetValue(partIndent, out template) ? template.minTier : 0;
            PricePackageItem[] rent = PriceAt(price.data.rent, tier);
            return rent.Length > 0
                ? rent.GetTotalCredits()
                : (float)Math.Round(PriceAt(price.data.normalSell, tier).GetTotalCredits() * RentShareOfSellPrice);
        }
    }
}
