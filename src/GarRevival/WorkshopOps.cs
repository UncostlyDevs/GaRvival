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

namespace GarRevival
{
    // The garage's upgrade and upkeep operations: part upgrades and the tech
    // tree (162, 158, 159), repairs (205), inventory slots (196), renting
    // (180), the spark exchange (160), timed boosts (157), consumable slots
    // (163) and buying consumables (218 with type Package).
    //
    // Everything is kept in the profile's own fields (partInUpgrade,
    // techLevels, timedBuffs, inventory, ...), so it is saved with it. All
    // times are UTC: the client compares boosts, rentals and rush prices with
    // DateTime.UtcNow.
    internal static partial class WorkshopOps
    {
        // Messages the original server used; the client has texts for them.
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
            switch (op)
            {
                case 162:
                case 158:
                case 159:
                case 205:
                case 196:
                case 180:
                case 160:
                case 157:
                case 163:
                    break;
                case 218: // Buying is GameServer's, except for the consumable packages sold here
                    if (FindPackage(request) == null)
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
            try
            {
                if (!_profileReady)
                {
                    Prepare();
                }
                // Settle anything that came due, so the request is judged
                // against the same state the client is about to be told.
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
                RevivalLog.Error("Workshop operation " + op + " failed", ex);
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

        // ---- keeping the profile current ----

        // Call just before the profile goes to the client (DataFactory.Player):
        // adds what older saves lack and settles whatever came due while the
        // game was closed. Nothing is announced; the client is about to
        // receive the whole profile. Never throws, so it cannot stop a login.
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
            PlayerVO player = PlayerState.Current;
            long now = DateTime.UtcNow.Ticks;
            bool changed = false;
            if (player.inventory == null)
            {
                player.inventory = new InventoryItems();
                changed = true;
            }
            if (player.partInUpgrade == null)
            {
                player.partInUpgrade = new List<PartUpgradeTimeSpan>();
            }
            if (player.techLevels == null)
            {
                player.techLevels = new List<TechCategoryLink>();
            }
            if (player.timedBuffs == null)
            {
                player.timedBuffs = new List<TimedBuffVO>();
            }
            // The client only updates tech levels it already has an entry for.
            foreach (TechCategory category in WorkshopData.UpgradeTable.Keys)
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

        // Once a second while the garage is showing: finish upgrades whose
        // time is up, end boosts, unlock tech tiers the player's level has
        // reached. In a battle it waits; the times are absolute, so nothing is lost.
        public static void Poll()
        {
            if (!_profileReady)
            {
                return;
            }
            long now = DateTime.UtcNow.Ticks;
            if (now < _nextPoll)
            {
                return;
            }
            _nextPoll = now + TimeSpan.TicksPerSecond;
            try
            {
                Loopback.VirtualConnection conn = Loopback.Get(PhotonNetwork.networkingPeer);
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

        // ---- paying ----

        // Takes credits and sparks together. With upsell the client has
        // offered to cover missing credits with sparks at the exchange rate
        // (SpendAction.TryUpsell). Returns the failure message, or null when paid.
        private static string Pay(Loopback.VirtualConnection conn, float credits, float sparks, bool upsell)
        {
            PlayerVO player = PlayerState.Current;
            float converted = 0f;
            if (player.credits < credits)
            {
                if (!upsell)
                {
                    return NotEnoughCredits;
                }
                converted = (float)Math.Ceiling((credits - player.credits) / CommonConstants.CurrencyConvertionRate);
            }
            if (player.premiumCredits < sparks + converted)
            {
                return NotEnoughSparks;
            }
            ChangeBalance(conn, converted * CommonConstants.CurrencyConvertionRate - credits, -(sparks + converted));
            return null;
        }

        // Event 220 carries both currencies; GameServer.ChangeCredits only
        // fills in the credits half.
        private static void ChangeBalance(Loopback.VirtualConnection conn, float creditsDelta, float sparksDelta)
        {
            if (sparksDelta == 0f)
            {
                GameServer.ChangeCredits(conn, creditsDelta, ItemAddedReason.Buy);
                return;
            }
            PlayerVO player = PlayerState.Current;
            player.credits += creditsDelta;
            player.premiumCredits += sparksDelta;
            GameServer.Event(conn, 220, new Dictionary<byte, object>
            {
                { 213, player.credits },
                { 214, creditsDelta },
                { 234, player.premiumCredits },
                { 218, sparksDelta },
                { 168, (byte)ItemAddedReason.Buy },
            });
        }

        // ---- shared helpers ----

        private static bool TryInt(Dictionary<byte, object> request, byte key, out int value)
        {
            value = 0;
            object raw;
            if (!request.TryGetValue(key, out raw) || raw == null)
            {
                return false;
            }
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

        private static bool Flag(Dictionary<byte, object> request, byte key)
        {
            object raw;
            return request.TryGetValue(key, out raw) && raw is bool && (bool)raw;
        }

        private static string Text(Dictionary<byte, object> request, byte key)
        {
            object raw;
            return request.TryGetValue(key, out raw) ? raw as string : null;
        }

        private static List<int> Ids(Dictionary<byte, object> request, byte key)
        {
            object raw;
            byte[] bytes = request.TryGetValue(key, out raw) ? raw as byte[] : null;
            return (bytes == null ? null : DataSerialization.ProtoDeserializeObject<List<int>>(bytes)) ?? new List<int>();
        }

        private static PartTemplate TemplateOf(PartVO part)
        {
            PartTemplate template;
            return Catalog.Parts.TryGetValue(part.indent, out template) && template.partTierableStatistics != null ? template : null;
        }

        // Gives a part the stats of a tier, the same way PlayerState does when
        // it loads a profile, so an upgraded part reads the same after a restart.
        // Damage is carried over as a share of the new maximum.
        private static void SetTier(PartVO part, PartTemplate template, int tier)
        {
            tier = Math.Max(0, Math.Min(Math.Min(template.maxTier, template.partTierableStatistics.Length - 1), tier));
            PartTierableStatistics stats = template.partTierableStatistics[tier];
            float health = part.statistics == null || part.statistics.Durability.maxValue <= 0f
                ? 1f
                : part.statistics.Durability.currentValue / part.statistics.Durability.maxValue;
            part.statistics = stats.statistics.ShallowCopy();
            part.statistics.Durability.currentValue = part.statistics.Durability.maxValue * Math.Max(0f, Math.Min(1f, health));
            part.partSpesificData = stats.partSpesificData;
            part.Durability2CreditsRato = stats.Durability2CreditsRato;
            part.currentTier = tier;
        }

        private static List<TimedBuffVO> ActiveBoosts(long now)
        {
            return PlayerState.Current.timedBuffs.Where(b => b.endTime.Ticks > now && WorkshopData.TimedBuffs().ContainsKey(b.indent)).ToList();
        }

        // The strongest active boost with this keyword, as the client's
        // DataManager.GetBonus*Coef methods count it.
        private static float BoostScale(string keyword)
        {
            float best = 0f;
            foreach (TimedBuffVO boost in ActiveBoosts(DateTime.UtcNow.Ticks))
            {
                TimedBuffTemplate template = WorkshopData.TimedBuffs()[boost.indent];
                if (template.keywords.Contains(keyword))
                {
                    best = Math.Max(best, template.scale);
                }
            }
            return best;
        }

        // ---- for the battle code ----

        // What battle rewards are multiplied by while a boost or VIP is
        // active. The end-of-battle screen assumes the server applied these
        // (GarUIEndBattleRewardsController shows the difference as the bonus).
        public static float CreditsBoost()
        {
            return 1f + CommonConstants.BONUS_CREDITS_VALUE * BoostScale(CommonConstants.BONUS_CREDITS) + CommonConstants.VIP_CREDITS_VALUE * BoostScale(CommonConstants.VIP);
        }

        public static float ExperienceBoost()
        {
            return 1f + CommonConstants.BONUS_EXPERIENCE_VALUE * BoostScale(CommonConstants.BONUS_EXPERIENCE) + CommonConstants.BONUS_EXPERIENCE_VALUE * BoostScale(CommonConstants.VIP);
        }
    }
}
