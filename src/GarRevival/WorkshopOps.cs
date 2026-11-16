using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ExitGames.Client.Photon;
using JunkWars.Client;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Inventory;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival;

// Garage upgrade and upkeep ops: upgrades and tech tree (162, 158, 159), repairs (205), inventory
// slots (196), renting (180), spark exchange (160), timed boosts (157), consumable slots (163) and
// consumable packages (218). State lives in the profile's own fields, so it is saved with it.
// Times are UTC: the client compares boosts, rentals and rush prices with DateTime.UtcNow.
internal static partial class WorkshopOps
{
    // Message keys the client has texts for.
    private const string InvalidRequest = "SM_0019";
    private const string InvalidData = "SM_0016";
    private const string InvalidPart = "SM_0020";
    private const string PartNotOwned = "SM_0060";
    private const string NotEnoughCredits = "SM_0058";
    private const string NotEnoughCreditsToRepair = "SM_0057";
    private const string NotEnoughSparks = "SM_0132";
    private const string NotEnoughComponents = "SM_0131";
    private const string InvalidSparkAmount = "SM_0139";
    private const string RentedCannotBeUpgraded = "SM_0140";
    private const string BoostAlreadyActive = "SM_0134";
    private const string InternalError = "SM_0089";

    private static bool _profileReady;
    private static long _nextPoll;

    public static bool Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        bool handled = op switch
        {
            162 or 158 or 159 or 205 or 196 or 180 or 160 or 157 or 163 => true,
            218 => FindPackage(request) != null, // other purchases are GameServer's
            _ => false,
        };
        if (!handled) return false;
        try
        {
            if (!_profileReady)
            {
                Prepare();
            }
            // Settle anything that came due, so the request is judged against
            // the state the client is about to be told.
            Advance(conn, DateTime.UtcNow.Ticks);
            switch (op)
            {
                case 162: UpgradeItem(conn, op, request); break;
                case 158: HurryUpgrade(conn, op, request); break;
                case 159: UnlockTechTier(conn, op, request); break;
                case 205: RepairItems(conn, op, request); break;
                case 196: BuyInventorySlots(conn, op, request); break;
                case 180: RentItem(conn, op, request); break;
                case 160: ConvertCurrency(conn, op, request); break;
                case 157: ActivateTimedBuff(conn, op, request); break;
                case 163: SelectConsumableBoost(conn, op, request); break;
                case 218: BuyPackage(conn, op, request); break;
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Workshop operation {op} failed", ex);
            if (op == 163)
            {
                ReplyBoostSlots(conn, op, null);
            }
            else
            {
                GameServer.Fail(conn, op, InternalError);
            }
        }
        return true;
    }

    // Call just before the profile goes to the client (DataFactory.Player): fills missing fields and
    // settles what came due while the game was closed, silently since the whole profile follows.
    // Never throws, so it cannot stop a login.
    public static void PrepareProfile()
    {
        try
        {
            Prepare();
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not prepare the profile's workshop data", ex);
        }
    }

    private static void Prepare()
    {
        var player = PlayerState.Current;
        long now = DateTime.UtcNow.Ticks;
        bool changed = false;
        if (player.inventory == null)
        {
            player.inventory = new();
            changed = true;
        }
        player.partInUpgrade ??= [];
        player.techLevels ??= [];
        player.timedBuffs ??= [];
        // The client only updates tech levels it already has an entry for.
        foreach (var category in WorkshopData.UpgradeTable.Keys)
        {
            if (!player.techLevels.Any(l => l.category == category))
            {
                player.techLevels.Add(new TechCategoryLink { category = category, currentTier = 0 });
                changed = true;
            }
        }
        changed |= ReturnExpiredRentals(player, now);
        changed |= Advance(null, now);
        if (changed)
        {
            PlayerState.Save();
        }
        _profileReady = true;
    }

    // Once a second in the garage: finish due upgrades, end boosts, unlock tech tiers the level has
    // reached. In a battle it waits; the times are absolute, so nothing is lost.
    public static void Poll()
    {
        if (!_profileReady) return;
        long now = DateTime.UtcNow.Ticks;
        if (now < _nextPoll) return;
        _nextPoll = now + TimeSpan.TicksPerSecond;
        try
        {
            var conn = Loopback.Get(PhotonNetwork.networkingPeer);
            // The client must already hold a profile for the events to land on.
            if (GameManager.Instance.currentGameState == GameState.Garage && conn.State == PeerStateValue.Connected
                && PlayerManager.Instance.player.valueObject != null)
            {
                Advance(conn, now);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Workshop upkeep failed; trying again in a minute", ex);
            _nextPoll = now + TimeSpan.TicksPerMinute;
        }
    }

    // conn == null: change the profile without telling the client.
    private static bool Advance(Loopback.VirtualConnection conn, long now)
    {
        bool changed = FinishUpgrades(conn, now, false);
        changed |= ExpireBoosts(conn, now);
        changed |= SyncTechLevels(conn);
        if (changed && conn != null)
        {
            PlayerState.Save();
        }
        return changed;
    }

    // Takes credits and sparks together. With upsell the client offered to cover missing credits
    // with sparks at the exchange rate (SpendAction.TryUpsell). Returns the failure message, or null when paid.
    private static string Pay(Loopback.VirtualConnection conn, float credits, float sparks, bool upsell)
    {
        var player = PlayerState.Current;
        float converted = 0f;
        if (player.credits < credits)
        {
            if (!upsell) return NotEnoughCredits;
            converted = (float)Math.Ceiling((credits - player.credits) / CommonConstants.CurrencyConvertionRate);
        }
        if (player.premiumCredits < sparks + converted) return NotEnoughSparks;
        ChangeBalance(conn, converted * CommonConstants.CurrencyConvertionRate - credits, -(sparks + converted));
        return null;
    }

    // Event 220 carries both currencies; GameServer.ChangeCredits only fills in the credits half.
    private static void ChangeBalance(Loopback.VirtualConnection conn, float creditsDelta, float sparksDelta)
    {
        if (sparksDelta == 0f)
        {
            GameServer.ChangeCredits(conn, creditsDelta, ItemAddedReason.Buy);
            return;
        }
        var player = PlayerState.Current;
        player.credits += creditsDelta;
        player.premiumCredits += sparksDelta;
        GameServer.Event(conn, 220, new()
        {
            { 213, player.credits },
            { 214, creditsDelta },
            { 234, player.premiumCredits },
            { 218, sparksDelta },
            { 168, (byte)ItemAddedReason.Buy },
        });
    }

    private static bool TryInt(Dictionary<byte, object> request, byte key, out int value)
    {
        value = 0;
        if (!request.TryGetValue(key, out var raw) || raw == null) return false;
        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Flag(Dictionary<byte, object> request, byte key) => request.TryGetValue(key, out var raw) && raw is bool b && b;

    private static string Text(Dictionary<byte, object> request, byte key) => request.TryGetValue(key, out var raw) ? raw as string : null;

    private static List<int> Ids(Dictionary<byte, object> request, byte key) =>
        request.TryGetValue(key, out var raw) && raw is byte[] bytes ? DataSerialization.ProtoDeserializeObject<List<int>>(bytes) ?? [] : [];

    private static PartTemplate TemplateOf(PartVO part) =>
        Catalog.Parts.TryGetValue(part.indent, out var template) && template.partTierableStatistics != null ? template : null;

    // Gives a part a tier's stats the way PlayerState does on load, so an upgraded part reads the
    // same after a restart. Damage carries over as a share of the new maximum.
    private static void SetTier(PartVO part, PartTemplate template, int tier)
    {
        tier = Math.Max(0, Math.Min(Math.Min(template.maxTier, template.partTierableStatistics.Length - 1), tier));
        var stats = template.partTierableStatistics[tier];
        float health = part.statistics == null || part.statistics.Durability.maxValue <= 0f
            ? 1f
            : part.statistics.Durability.currentValue / part.statistics.Durability.maxValue;
        part.statistics = stats.statistics.ShallowCopy();
        part.statistics.Durability.currentValue = part.statistics.Durability.maxValue * Math.Max(0f, Math.Min(1f, health));
        part.partSpesificData = stats.partSpesificData;
        part.Durability2CreditsRato = stats.Durability2CreditsRato;
        part.currentTier = tier;
    }

    private static List<TimedBuffVO> ActiveBoosts(long now) =>
        PlayerState.Current.timedBuffs.Where(b => b.endTime.Ticks > now && WorkshopData.TimedBuffs().ContainsKey(b.indent)).ToList();

    // The strongest active boost with this keyword, as DataManager.GetBonus*Coef counts it.
    private static float BoostScale(string keyword)
    {
        float best = 0f;
        foreach (var boost in ActiveBoosts(DateTime.UtcNow.Ticks))
        {
            var template = WorkshopData.TimedBuffs()[boost.indent];
            if (template.keywords.Contains(keyword))
            {
                best = Math.Max(best, template.scale);
            }
        }
        return best;
    }

    // Battle reward multipliers while a boost or VIP is active. The end-of-battle screen assumes the
    // server applied these (GarUIEndBattleRewardsController shows the difference as the bonus).
    public static float CreditsBoost() =>
        1f + CommonConstants.BONUS_CREDITS_VALUE * BoostScale(CommonConstants.BONUS_CREDITS) + CommonConstants.VIP_CREDITS_VALUE * BoostScale(CommonConstants.VIP);

    public static float ExperienceBoost() =>
        1f + CommonConstants.BONUS_EXPERIENCE_VALUE * BoostScale(CommonConstants.BONUS_EXPERIENCE) + CommonConstants.BONUS_EXPERIENCE_VALUE * BoostScale(CommonConstants.VIP);
}
