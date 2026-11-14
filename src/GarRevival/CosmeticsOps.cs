using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;
using JunkWars.Common.Utils.Enums;

namespace GarRevival
{
    // Part skins, paint, crates and reward claims. Request and response keys
    // are the ones LoadbalancingPeer / NetworkingPeer use. Failure texts are
    // the original server's message ids (SM_xxxx): the client still has their
    // translations and shows them in its error pop-up.
    internal static partial class CosmeticsOps
    {
        private const string InvalidRequest = "SM_0019";  // Invalid operation request.
        private const string InvalidData = "SM_0016";     // Invalid data supplied.
        private const string NoSuchPart = "SM_0060";      // You do not have such part in your inventory.
        private const string NotEnoughCredits = "SM_0058";
        private const string NotEnoughSparks = "SM_0132";
        private const string NotEnoughPaint = "SM_0155";  // Not enough Paint Components.
        private const string ColorAlreadyOwned = "SM_0156";
        private const string NoSuchColor = "SM_0157";
        private const string InvalidColor = "SM_0158";
        private const string ServerError = "SM_0089";     // Internal server error.

        public static bool Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            switch (op)
            {
                case 147:
                case 150:
                case 151:
                case 154:
                case 155:
                case 156:
                case 193:
                    break;
                default:
                    return false;
            }
            try
            {
                request = request ?? new Dictionary<byte, object>();
                switch (op)
                {
                    case 147: GetSpecialPack(conn, op); break;
                    case 150: UnlockColor(conn, op, request); break;
                    case 151: SetPartTinting(conn, op, request); break;
                    case 154: SetPartSkin(conn, op, request); break;
                    case 155: UnlockPartSkin(conn, op, request); break;
                    case 156: OpenCrate(conn, op, request); break;
                    case 193: ClaimReward(conn, op, request); break;
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Cosmetics operation " + op + " failed", ex);
                Fail(conn, op, ServerError);
            }
            return true;
        }

        // ---- paint ----

        // 150: buy a colour. The client adds it to its list from the answer and
        // treats an answer without key 235 as the failure.
        private static void UnlockColor(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int index;
            if (!TryGet(request, 235, out index))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            PlayerVO player = PlayerState.Current;
            EnsureStarterColors(player);
            CosmeticsDesign.ColorDef color = ColorByIndex(index);
            if (color == null)
            {
                Fail(conn, op, NoSuchColor);
                return;
            }
            if (player.ownedTintColors.Contains(index))
            {
                Fail(conn, op, ColorAlreadyOwned);
                return;
            }
            if (color.CrateOnly)
            {
                Fail(conn, op, InvalidColor);
                return;
            }
            float price = CosmeticsDesign.ColorPrice(color.Category);
            if (player.premiumCredits < price)
            {
                Fail(conn, op, NotEnoughSparks);
                return;
            }
            player.premiumCredits -= price;
            player.ownedTintColors.Add(index);
            AnnounceCurrency(conn, 0f, -price, ItemAddedReason.Buy);
            PlayerState.Save();
            RevivalLog.Info("Unlocked colour " + index + " (" + color.Name + ") for " + price + " sparks");
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 235, index } });
        }

        // 151: paint one part (or strip its paint). Costs a paint spray either
        // way, as the paint screen tells the player.
        private static void SetPartTinting(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int partId;
            if (!TryGet(request, 233, out partId))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            byte[] raw;
            TintingDataVO tinting = TryGet(request, 160, out raw) ? DataSerialization.ProtoDeserializeObject<TintingDataVO>(raw) : null;
            tinting = tinting ?? new TintingDataVO(); // an all-default object travels as nothing: paint stripped
            PlayerVO player = PlayerState.Current;
            EnsureStarterColors(player);
            PartVO part = PlayerState.FindPart(partId);
            if (part == null)
            {
                Fail(conn, op, NoSuchPart);
                return;
            }
            foreach (int index in new[] { tinting.primaryTintColorIndex, tinting.secondaryTintColorIndex, tinting.tertiaryTintColorIndex })
            {
                if (index != -1 && (ColorByIndex(index) == null || !player.ownedTintColors.Contains(index)))
                {
                    Fail(conn, op, InvalidColor);
                    return;
                }
            }
            if (Owned<PaintComponentVO>(player, p => true) < CosmeticsDesign.PaintSpraysPerPart)
            {
                Fail(conn, op, NotEnoughPaint);
                return;
            }
            InventoryItems used = new InventoryItems();
            used.AddItem(PackageType.PaintComponent, new PaintComponentVO { quantity = CosmeticsDesign.PaintSpraysPerPart });
            TakeFromInventory(conn, player, used);
            part.tintingData = tinting;
            PlayerState.Save();
            RevivalLog.Info("Painted part " + partId + " (" + part.indent + "): " + (tinting.tintEnabled ? "colours " + tinting.primaryTintColorIndex + "/" + tinting.secondaryTintColorIndex + "/" + tinting.tertiaryTintColorIndex : "paint removed"));
            GameServer.Reply(conn, op, new Dictionary<byte, object>
            {
                { 160, DataSerialization.ProtoSerializeObject(tinting) },
                { 233, partId },
            });
        }

        // ---- part skins ----

        // 155: buy (168 true) or rent (168 false) a skin for every part of one
        // template. The answer carries the activation time; a bought skin keeps
        // the value object's default, "never", so it cannot run out.
        private static void UnlockPartSkin(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            string indent;
            byte index;
            bool buy;
            if (!TryGet(request, 233, out indent) || !TryGet(request, 218, out index))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            if (!TryGet(request, 168, out buy))
            {
                buy = true;
            }
            PlayerVO player = PlayerState.Current;
            PartSkinsTemplate template;
            PartSkinData skin = PartSkins().TryGetValue(indent, out template) ? template.GetSkinByIndex(index) : null;
            PricePackageItem[] price = skin == null ? null : buy ? (skin.normalBuy ?? skin.premiumBuy) : skin.rent;
            if (skin == null || !skin.purchasable || price == null)
            {
                Fail(conn, op, InvalidData);
                return;
            }
            PartSkinsVO owned = player.ownedPartSkins.FirstOrDefault(s => s.BelongsTo(indent));
            PartSkinsVO.Skin current = owned == null ? null : owned.GetSkinByIndex(index);
            if (current != null && SkinUsable(current, template))
            {
                Fail(conn, op, InvalidData);
                return;
            }
            float credits = price.Where(p => p.type == PackageType.Credits).Sum(p => p.quantity);
            float sparks = price.Where(p => p.type == PackageType.PremiumCredits).Sum(p => p.quantity);
            if (player.credits < credits || player.premiumCredits < sparks)
            {
                Fail(conn, op, player.credits < credits ? NotEnoughCredits : NotEnoughSparks);
                return;
            }
            if (owned == null)
            {
                owned = new PartSkinsVO { partIndent = indent };
                player.ownedPartSkins.Add(owned);
            }
            owned.skins.RemoveAll(s => s.Index == index); // a rental that ran out
            long activation = buy ? long.MinValue : DateTime.Now.Ticks;
            owned.skins.Add(new PartSkinsVO.Skin(index, activation));
            player.credits -= credits;
            player.premiumCredits -= sparks;
            AnnounceCurrency(conn, -credits, -sparks, ItemAddedReason.Buy);
            PlayerState.Save();
            RevivalLog.Info((buy ? "Bought" : "Rented") + " skin " + index + " of " + indent);
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 233, indent }, { 218, index }, { 168, activation } });
        }

        // 154: put an owned skin (0 = the part's own look) on one part.
        private static void SetPartSkin(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int partId;
            byte index;
            if (!TryGet(request, 233, out partId) || !TryGet(request, 218, out index))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            PlayerVO player = PlayerState.Current;
            PartVO part = PlayerState.FindPart(partId);
            if (part == null)
            {
                Fail(conn, op, NoSuchPart);
                return;
            }
            if (index != 0)
            {
                PartSkinsTemplate template;
                PartSkinsVO owned = player.ownedPartSkins.FirstOrDefault(s => s.BelongsTo(part.indent));
                PartSkinsVO.Skin skin = owned == null ? null : owned.GetSkinByIndex(index);
                if (skin == null || !PartSkins().TryGetValue(part.indent, out template) || !template.ContainsSkinIndex(index) || !SkinUsable(skin, template))
                {
                    Fail(conn, op, InvalidData);
                    return;
                }
            }
            part.skinIndex = index;
            PlayerState.Save();
            RevivalLog.Info("Part " + partId + " (" + part.indent + ") now wears skin " + index);
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 233, partId }, { 218, index } });
        }

        private static bool SkinUsable(PartSkinsVO.Skin skin, PartSkinsTemplate template)
        {
            return skin.ActivationDateTicks == long.MinValue || template.lifetimeDays <= 0
                || DateTime.Now < new DateTime(skin.ActivationDateTicks).AddDays(template.lifetimeDays);
        }

        // ---- crates ----

        // 156: open one crate of a type with its key, or try with a multi-tool.
        // The client reads key 168 of every failed answer: true plays the
        // "multi-tool broke" animation, false shows the message.
        private static void OpenCrate(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            byte rawType;
            bool useLockpick;
            if (!TryGet(request, 168, out rawType) || !Enum.IsDefined(typeof(CrateType), rawType))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            TryGet(request, 167, out useLockpick);
            CrateType type = (CrateType)rawType;
            PlayerVO player = PlayerState.Current;
            LootTemplate table = CrateLoot(type);
            if (table == null || Owned<CrateVO>(player, c => c.type == type) < 1)
            {
                Fail(conn, op, InvalidData);
                return;
            }
            InventoryItems used = new InventoryItems();
            if (useLockpick)
            {
                if (Owned<LockpickVO>(player, l => true) < 1)
                {
                    Fail(conn, op, InvalidData);
                    return;
                }
                used.AddItem(PackageType.Lockpick, new LockpickVO { quantity = 1 });
                // The chance the crate window shows: the shared table plus boosts.
                float chance = CrateVO.cLockpickProbabilities[type] + LockpickBonus(player);
                if (Rng.NextDouble() >= chance)
                {
                    TakeFromInventory(conn, player, used); // it breaks whether or not it works
                    PlayerState.Save();
                    RevivalLog.Info("Multi-tool failed on a " + type + " crate (chance " + chance.ToString("0.####") + ")");
                    Fail(conn, op, "Multi-tool broke", true);
                    return;
                }
            }
            else
            {
                if (Owned<CrateKeyVO>(player, k => k.type == type) < 1)
                {
                    Fail(conn, op, InvalidData);
                    return;
                }
                used.AddItem(PackageType.CrateKey, new CrateKeyVO { type = type, quantity = 1 });
            }
            used.AddItem(PackageType.Crate, new CrateVO { type = type, quantity = 1 });
            TakeFromInventory(conn, player, used);

            List<PackageItem> loot = Roll(table, ContextFor(player, 1f, !useLockpick), player);
            Announce(conn, Give(player, loot), ItemAddedReason.OpenCrate, false);
            PlayerState.Save();
            RevivalLog.Info("Opened a " + type + " crate with a " + (useLockpick ? "multi-tool" : "key") + ": " + Describe(loot));
            GameServer.Reply(conn, op);
        }

        // Same sum as DataManager.GetBonusUnlockChance: the best running
        // "unlock_chance" boost plus one VIP boost.
        private static float LockpickBonus(PlayerVO player)
        {
            if (player.timedBuffs == null || player.timedBuffs.Count == 0)
            {
                return 0f;
            }
            Dictionary<string, TimedBuffTemplate> templates = WorkshopData.TimedBuffs();
            float best = 0f;
            float vip = 0f;
            foreach (TimedBuffVO buff in player.timedBuffs)
            {
                TimedBuffTemplate template;
                if (buff.endTime <= DateTime.Now || templates == null || !templates.TryGetValue(buff.indent, out template) || template.keywords == null)
                {
                    continue;
                }
                float value = CommonConstants.BONUS_UNLOCK_CHANCE_VALUE * template.scale;
                if (template.keywords.Contains(CommonConstants.BONUS_UNLOCK_CHANCE))
                {
                    best = Math.Max(best, value);
                }
                if (vip == 0f && template.keywords.Contains(CommonConstants.VIP))
                {
                    vip = value;
                }
            }
            return best + vip;
        }

        // Called after a battle's credits and experience have been announced.
        // Rolls the drop, stores it and sends the "items added" event with the
        // end-of-game reason, which the garage's battle rewards window waits for.
        public static void GrantBattleLoot(EndBattleData result, bool won)
        {
            try
            {
                Loopback.VirtualConnection conn = Loopback.Get(PhotonNetwork.networkingPeer);
                PlayerVO player = PlayerState.Current;
                int kills = result == null ? 0 : Math.Max(0, result.kills);
                float performance = 1f + (won ? CosmeticsDesign.WinFactor : 0f) + Math.Min(CosmeticsDesign.MaxKillFactor, CosmeticsDesign.KillFactor * kills);
                List<PackageItem> loot = Roll(BattleLoot(), ContextFor(player, performance, false), player);
                Announce(conn, Give(player, loot), ItemAddedReason.EndGameLoot, true);
                PlayerState.Save();
                RevivalLog.Info("Battle loot (x" + performance.ToString("0.0") + "): " + Describe(loot));
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Could not grant battle loot", ex);
            }
        }

        // ---- store packs ----

        // Operation 218 with item type Package. False when the pack is not one
        // of ours, so the caller can try elsewhere.
        public static bool TryBuyPackage(Loopback.VirtualConnection conn, byte op, string indent)
        {
            PackagePriceTemplate.PricePackage pack = Packages().Find(p => p.indent == indent);
            if (pack == null)
            {
                return false;
            }
            PlayerVO player = PlayerState.Current;
            float credits = pack.normalBuy.Where(p => p.type == PackageType.Credits).Sum(p => p.quantity);
            float sparks = pack.normalBuy.Where(p => p.type == PackageType.PremiumCredits).Sum(p => p.quantity);
            if (player.credits < credits || player.premiumCredits < sparks)
            {
                GameServer.Fail(conn, op, player.credits < credits ? NotEnoughCredits : NotEnoughSparks);
                return true;
            }
            Grant grant = Give(player, pack.itemPackage.items);
            player.credits -= credits;
            player.premiumCredits -= sparks;
            grant.Credits -= credits;
            grant.Sparks -= sparks;
            Announce(conn, grant, ItemAddedReason.Buy, false);
            PlayerState.Save();
            RevivalLog.Info("Bought " + indent + " for " + credits + " credits, " + sparks + " sparks");
            GameServer.Reply(conn, op);
            return true;
        }

        // ---- rewards ----

        // 147: the one-off welcome pack. The client marks it received on success
        // and carries on with its startup either way.
        private static void GetSpecialPack(Loopback.VirtualConnection conn, byte op)
        {
            PlayerVO player = PlayerState.Current;
            if (player.specialPackReceived)
            {
                Fail(conn, op, "Special pack already received");
                return;
            }
            string crate = CosmeticsDesign.SpecialPackCrate.ToString();
            List<PackageItem> pack = new List<PackageItem>
            {
                new PackageItem { type = PackageType.PaintComponent, quantity = CosmeticsDesign.SpecialPackPaint },
                new PackageItem { type = PackageType.Lockpick, quantity = CosmeticsDesign.SpecialPackLockpicks },
                new PackageItem { type = PackageType.Crate, indent = crate, quantity = 1 },
                new PackageItem { type = PackageType.CrateKey, indent = crate, quantity = 1 },
                new PackageItem { type = PackageType.PremiumCredits, quantity = CosmeticsDesign.SpecialPackSparks },
            };
            Announce(conn, Give(player, pack), ItemAddedReason.Special, false);
            player.specialPackReceived = true;
            PlayerState.Save();
            RevivalLog.Info("Special pack: " + Describe(pack));
            GameServer.Reply(conn, op);
        }

        // 193: the client has already shown the reward from the promotions data
        // it got at startup; this makes it real. It reads key 168 of the answer.
        private static void ClaimReward(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            byte rawType;
            if (!TryGet(request, 168, out rawType))
            {
                Fail(conn, op, InvalidRequest);
                return;
            }
            PlayerVO player = PlayerState.Current;
            PromotionsTemplate template = DailyRewardsTemplate();
            switch ((ClaimRewardType)rawType)
            {
                case ClaimRewardType.DailyLogin:
                {
                    int day = DailyLoginIndex(player);
                    if (day < 0)
                    {
                        Fail(conn, op, "Today's login reward has already been claimed");
                        return;
                    }
                    Package reward = template.DailyLoginRewards[day];
                    Announce(conn, Give(player, reward.items), ItemAddedReason.DailyLogin, false);
                    player.playerPromotions.dailyLoginCount = day + 1;
                    player.playerPromotions.dailyLoginLastDate = DateTime.Today;
                    RevivalLog.Info("Daily login reward, day " + (day + 1) + ": " + Describe(reward.items));
                    break;
                }
                case ClaimRewardType.SocialShareLevelUp:
                {
                    // Once per level reached.
                    if (player.playerPromotions == null)
                    {
                        player.playerPromotions = new PlayerPromotionsVO();
                    }
                    if (player.level <= player.playerPromotions.lastLevelUpShare)
                    {
                        Fail(conn, op, "Level-up reward already claimed for level " + player.level);
                        return;
                    }
                    player.playerPromotions.lastLevelUpShare = player.level;
                    player.credits += template.SocialShareLevelUpRewardCredits;
                    AnnounceCurrency(conn, template.SocialShareLevelUpRewardCredits, 0f, ItemAddedReason.Special);
                    RevivalLog.Info("Level-up share reward: " + template.SocialShareLevelUpRewardCredits + " credits");
                    break;
                }
                default:
                    Fail(conn, op, InvalidRequest);
                    return;
            }
            PlayerState.Save();
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 168, rawType } });
        }

        // ---- plumbing ----

        private static bool TryGet<T>(Dictionary<byte, object> request, byte key, out T value)
        {
            object raw;
            if (request.TryGetValue(key, out raw) && raw is T)
            {
                value = (T)raw;
                return true;
            }
            value = default(T);
            return false;
        }

        private static void Fail(Loopback.VirtualConnection conn, byte op, string message, bool lockpickBroke = false)
        {
            RevivalLog.Info("op " + op + " failed: " + message);
            OperationResponse response = new OperationResponse { OperationCode = op, ReturnCode = 1, DebugMessage = message, Parameters = new Dictionary<byte, object>() };
            if (op == 156)
            {
                response.Parameters[168] = lockpickBroke;
            }
            Loopback.Enqueue(conn, "op " + op + " failure", listener => listener.OnOperationResponse(response));
        }
    }
}
