using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Utils;

namespace GarRevival;

// Workshop startup data: tech tree and upgrade table, timed boosts, consumable prices.
// The numbers are designed; the client fixes the shape: tech tiers 1-10 (1-3 for devices),
// boosts CR / XP / REPAIRS / VIP with six VIP plans, rent for 3 days at 10% of the selling price.
internal static class WorkshopData
{
    // Seconds to reach a tier from the one below. Index = tier.
    private static readonly int[] UpgradeSeconds = { 0, 60, 300, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400 };

    // Player level at which a tech tier unlocks by itself. Index = tier.
    private static readonly int[] UnlockAtLevel = { 1, 2, 5, 10, 15, 20, 30, 40, 50, 65, 80 };
    private static readonly int[] DeviceUnlockAtLevel = { 1, 5, 15, 30 };

    // Sparks to unlock a tech tier early, and to skip a whole tier's upgrade time
    // (the client charges the share that is still left).
    private const float UnlockSparksPerTier = 20f;
    private const float HurrySparksPerTier = 5f;

    // Boosts: minutes and sparks per plan. Scale 1 doubles credits or experience
    // and makes repairs free (the client's own formulas).
    private static readonly int[] BoostMinutes = { 60, 1440, 10080 };
    private static readonly float[] BoostSparks = { 10f, 60f, 300f };
    // The VIP screen has six plan buttons (indexes 0-5).
    private static readonly int[] VipMinutes = { 1440, 4320, 10080, 43200, 129600, 259200 };
    private static readonly float[] VipSparks = { 30f, 80f, 150f, 500f, 1200f, 2000f };

    // Credits for one consumable.
    private static readonly Dictionary<ConsumableBoostType, float> ConsumableCredits = new()
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

    // The tech tree screen has slots for tiers 1-10, three for devices.
    private const int OriginalMaxTier = 10;
    private const int OriginalMaxDeviceTier = 3;

    public const string PackagePrefix = "Pack_ConsumableBoost_";

    private static Dictionary<TechCategory, PartsUpgardeCategoryData> _upgrades;
    private static Dictionary<string, TimedBuffTemplate> _buffs;
    private static List<PackagePriceTemplate.PricePackage> _packages;

    public static Dictionary<TechCategory, PartsUpgardeCategoryData> Upgrades() => UpgradeTable;

    public static Dictionary<TechCategory, PartsUpgardeCategoryData> UpgradeTable =>
        _upgrades ??= Enum.GetValues(typeof(TechCategory)).Cast<TechCategory>().ToDictionary(c => c, Category);

    // The arrays cover all ten tiers even when fewer can be reached:
    // the tech tree's tooltips index them for every slot on screen.
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

    private static PricePackageItem[] Sparks(Func<int, float> price) =>
        Enumerable.Range(0, OriginalMaxTier + 1)
            .Select(tier => new PricePackageItem { type = PackageType.PremiumCredits, quantity = price(tier), indent = string.Empty }).ToArray();

    // The parts the garage's upgrade screen lists (UpgradeManager.IsUpgradeable).
    public static PartsUpgardeCategoryData CategoryOf(LogicalType partType)
    {
        if (partType is not (LogicalType.CassisCase or LogicalType.BodyCase or LogicalType.WeaponCase
            or LogicalType.WeaponMuzzle or LogicalType.HeadCase or LogicalType.DeviceCase)) return null;
        return EnumUtils.GetTechCategoryByPartType(partType, out var category) && UpgradeTable.TryGetValue(category, out var data) ? data : null;
    }

    public static long UpgradeTicks(PartsUpgardeCategoryData data, int tier) =>
        tier >= 0 && tier < data.tierUpgardeDuration.Length ? data.tierUpgardeDuration[tier] * TimeSpan.TicksPerSecond : 0L;

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

    // The ids are the ones the client has names and icons for; the keyword is what each one
    // does (DataManager.GetBonusCreditsCoef and friends look it up).
    public static Dictionary<string, TimedBuffTemplate> TimedBuffs() =>
        _buffs ??= new()
        {
            { "CR", Buff("CR", CommonConstants.BONUS_CREDITS, BoostMinutes, BoostSparks) },
            { "XP", Buff("XP", CommonConstants.BONUS_EXPERIENCE, BoostMinutes, BoostSparks) },
            { "REPAIRS", Buff("REPAIRS", CommonConstants.BONUS_REPAIRS, BoostMinutes, BoostSparks) },
            { "VIP", Buff("VIP", CommonConstants.VIP, VipMinutes, VipSparks) },
        };

    private static TimedBuffTemplate Buff(string indent, string keyword, int[] minutes, float[] sparks) => new()
    {
        indent = indent,
        // Never empty: the client copies this list without a null check.
        keywords = [keyword],
        duration = (int[])minutes.Clone(),
        showInShop = true,
        scale = 1f,
        price = sparks.Select(s => new PricePackageGroup
        {
            items = [new PricePackageItem { type = PackageType.PremiumCredits, quantity = s, indent = string.Empty }],
        }).ToArray(),
    };

    // One package per battle consumable; the Supply screen looks them up by item type to show a
    // price and to buy (op 218, type Package). Kept out of the store list so only that screen sells them.
    public static List<PackagePriceTemplate.PricePackage> ConsumablePackages() =>
        _packages ??= ConsumableCredits.Select(entry => new PackagePriceTemplate.PricePackage
        {
            indent = PackagePrefix + entry.Key,
            showInShop = false,
            itemPackage = new Package
            {
                items = [new PackageItem { type = PackageType.ConsumableBoost, quantity = 1f, indent = entry.Key.ToString() }],
            },
            normalBuy = [new PricePackageItem { type = PackageType.Credits, quantity = entry.Value, indent = string.Empty }],
        }).ToList();

    // Other areas add their own packages to the list this returns.
    public static PackagePriceTemplate PackagePrices()
    {
        var template = new PackagePriceTemplate();
        template.packages.AddRange(ConsumablePackages());
        return template;
    }

    public static PricePackageItem[] PriceAt(TierLink[] links, int tier)
    {
        if (links != null)
        {
            foreach (var link in links)
            {
                if (link.tier == tier && link.items != null) return link.items;
            }
        }
        return [];
    }

    // Credits to rent one part: the catalog's rent price when it has one (what the store shows),
    // otherwise 10% of the selling price.
    public static float RentCredits(string partIndent)
    {
        if (!Catalog.Prices.TryGetValue(partIndent, out var price)) return 0f;
        int tier = Catalog.Parts.TryGetValue(partIndent, out var template) ? template.minTier : 0;
        var rent = PriceAt(price.data.rent, tier);
        return rent.Length > 0
            ? rent.GetTotalCredits()
            : (float)Math.Round(PriceAt(price.data.normalSell, tier).GetTotalCredits() * RentShareOfSellPrice);
    }
}
