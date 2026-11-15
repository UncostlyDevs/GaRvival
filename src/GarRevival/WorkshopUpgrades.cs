using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival
{
    // Part upgrades and the tech tree, as UpgradeManager on the client reads
    // them. An upgrade in progress is one PartUpgradeTimeSpan: it starts at
    // startTime and passes through the tiers startTier+1 .. tier, each taking
    // its category's tierUpgardeDuration. Rushing moves the whole schedule
    // earlier through hurryOffset. The part's tier only changes when a step's
    // time is up; the client learns of it through event 216 (the part) and
    // event 140 (the remaining schedule).
    internal static partial class WorkshopOps
    {
        // ---- 162: start an upgrade ----

        private static void UpgradeItem(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int id;
            int itemType;
            int tier;
            if (!TryInt(request, 233, out id) || !TryInt(request, 169, out tier))
            {
                GameServer.Fail(conn, op, InvalidData);
                return;
            }
            TryInt(request, 234, out itemType);
            PlayerVO player = PlayerState.Current;
            PartVO part = UpgradeTarget(id, itemType);
            if (part == null)
            {
                GameServer.Fail(conn, op, PartNotOwned);
                return;
            }
            PartTemplate template = TemplateOf(part);
            PartsUpgardeCategoryData data = WorkshopData.CategoryOf(part.type);
            PriceTemplate price;
            if (template == null || data == null || !Catalog.Prices.TryGetValue(part.indent, out price))
            {
                GameServer.Fail(conn, op, InvalidPart);
                return;
            }
            if (part.expirationDate != DateTime.MinValue)
            {
                GameServer.Fail(conn, op, RentedCannotBeUpgraded);
                return;
            }
            if (player.partInUpgrade.Any(s => s.partID == part.partID))
            {
                GameServer.Fail(conn, op, "Label_ActionLockedUpgradeInProgress");
                return;
            }
            if (tier <= part.currentTier || tier > Math.Min(template.maxTier, data.maxTechTier))
            {
                GameServer.Fail(conn, op, "This part cannot be upgraded to tier " + tier + ".");
                return;
            }
            if (tier > TechLevel(player, data.category))
            {
                GameServer.Fail(conn, op, "Upgrade_Details_ActionHint_Technology_Unlock");
                return;
            }

            // The same sum the client shows: the upgrade price of every tier
            // the part passes through (UpgradeManager.GetTotalUpgadePrice).
            float credits = 0f;
            float sparks = 0f;
            Dictionary<UpgradeComponentType, int> components = new Dictionary<UpgradeComponentType, int>();
            for (int from = part.currentTier; from < tier; from++)
            {
                foreach (PricePackageItem item in WorkshopData.PriceAt(price.data.upgrade, from))
                {
                    if (item.type == PackageType.Credits)
                    {
                        credits += item.quantity;
                    }
                    else if (item.type == PackageType.PremiumCredits)
                    {
                        sparks += item.quantity;
                    }
                    else if (item.type == PackageType.UpgradeComponent && Enum.IsDefined(typeof(UpgradeComponentType), item.indent ?? string.Empty))
                    {
                        UpgradeComponentType component = (UpgradeComponentType)Enum.Parse(typeof(UpgradeComponentType), item.indent);
                        int have;
                        components.TryGetValue(component, out have);
                        components[component] = have + (int)Math.Ceiling(item.quantity);
                    }
                }
            }
            foreach (KeyValuePair<UpgradeComponentType, int> need in components)
            {
                UpgradeComponentType type = need.Key;
                UpgradeComponentVO owned = player.inventory.FindItem<UpgradeComponentVO>(c => c.type == type);
                if (owned == null || owned.quantity < need.Value)
                {
                    GameServer.Fail(conn, op, NotEnoughComponents);
                    return;
                }
            }
            string refusal = Pay(conn, credits, sparks, Flag(request, 218));
            if (refusal != null)
            {
                GameServer.Fail(conn, op, refusal);
                return;
            }
            if (components.Count > 0)
            {
                InventoryItems spent = new InventoryItems();
                foreach (KeyValuePair<UpgradeComponentType, int> need in components)
                {
                    spent.AddItem(PackageType.UpgradeComponent, new UpgradeComponentVO { type = need.Key, quantity = need.Value });
                }
                player.inventory.SubtractInventory(spent);
                GameServer.Event(conn, 218, new Dictionary<byte, object> { { 235, DataSerialization.ProtoSerializeObject(spent) } });
            }

            long now = DateTime.UtcNow.Ticks;
            player.partInUpgrade.Add(new PartUpgradeTimeSpan
            {
                partID = part.partID,
                startTime = new DateTime(now, DateTimeKind.Utc),
                startTier = part.currentTier,
                tier = tier,
                hurryOffset = 0L,
            });
            RevivalLog.Info("Upgrade started: part " + part.partID + " (" + part.indent + ") tier " + part.currentTier + " -> " + tier + " for " + credits + " credits, " + sparks + " sparks");
            // Also sends the new schedule (event 140).
            FinishUpgrades(conn, now, true);
            PlayerState.Save();
            GameServer.Reply(conn, op);
        }

        // The upgrade screen always sends a part id; for a module it is the
        // module's case that carries the tier.
        private static PartVO UpgradeTarget(int id, int itemType)
        {
            if (itemType == (int)ItemType.Module)
            {
                ModuleAssemblyVO module = PlayerState.FindModule(id);
                return module == null ? null : PlayerState.FindPart(module.moduleCaseID);
            }
            return PlayerState.FindPart(id);
        }

        // ---- 158: finish an upgrade now, for sparks ----

        private static void HurryUpgrade(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int id;
            int itemType;
            int toTier;
            if (!TryInt(request, 233, out id) || !TryInt(request, 169, out toTier))
            {
                GameServer.Fail(conn, op, InvalidData);
                return;
            }
            TryInt(request, 234, out itemType);
            PlayerVO player = PlayerState.Current;
            PartVO part = UpgradeTarget(id, itemType);
            PartUpgradeTimeSpan span = part == null ? null : player.partInUpgrade.FirstOrDefault(s => s.partID == part.partID);
            PartsUpgardeCategoryData data = part == null ? null : WorkshopData.CategoryOf(part.type);
            if (span == null || data == null || toTier <= part.currentTier || toTier > span.tier)
            {
                // Usually the upgrade finished while the confirmation was open.
                GameServer.Fail(conn, op, InvalidRequest);
                return;
            }

            // UpgradeManager.GetTotalHurryUpgradePrice: the step in progress
            // costs the share of its time that is left, later steps cost full.
            long now = DateTime.UtcNow.Ticks;
            long end = span.startTime.Ticks - span.hurryOffset;
            float sparks = 0f;
            bool inProgress = true;
            for (int tier = span.startTier + 1; tier <= toTier; tier++)
            {
                long duration = WorkshopData.UpgradeTicks(data, tier);
                end += duration;
                if (tier <= part.currentTier)
                {
                    continue;
                }
                PricePackageItem step = data.tierHurryPrice[tier];
                float quantity = step.quantity;
                if (inProgress)
                {
                    quantity = duration <= 0L ? 0f : (float)Math.Ceiling(quantity * ((double)Math.Abs(end - now) / duration));
                    inProgress = false;
                }
                if (quantity > 0f && step.type == PackageType.PremiumCredits)
                {
                    sparks += (float)Math.Ceiling(quantity);
                }
            }
            string refusal = Pay(conn, 0f, sparks, false);
            if (refusal != null)
            {
                GameServer.Fail(conn, op, refusal);
                return;
            }
            // 'end' is now when the requested tier would have been reached.
            if (end > now)
            {
                span.hurryOffset += end - now;
            }
            RevivalLog.Info("Upgrade rushed: part " + part.partID + " to tier " + toTier + " for " + sparks + " sparks");
            FinishUpgrades(conn, now, true);
            PlayerState.Save();
            GameServer.Reply(conn, op);
        }

        // ---- 159: unlock a tech tier early, for sparks ----

        private static void UnlockTechTier(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            int categoryCode;
            int tier;
            PartsUpgardeCategoryData data;
            if (!TryInt(request, 218, out categoryCode) || !TryInt(request, 169, out tier) || !WorkshopData.UpgradeTable.TryGetValue((TechCategory)categoryCode, out data))
            {
                GameServer.Fail(conn, op, InvalidData);
                return;
            }
            PlayerVO player = PlayerState.Current;
            TechCategoryLink link = player.techLevels.First(l => l.category == data.category);
            if (tier <= link.currentTier)
            {
                GameServer.Fail(conn, op, "Upgrade_TechTree_Unlocked");
                return;
            }
            if (tier > data.maxTechTier)
            {
                GameServer.Fail(conn, op, "No part can be upgraded beyond tier " + data.maxTechTier + " yet.");
                return;
            }
            // Every tier on the way is paid for; the screen normally only
            // offers the next one.
            float sparks = 0f;
            for (int t = link.currentTier + 1; t <= tier; t++)
            {
                sparks += data.tierForceUnlockPrice[t].quantity;
            }
            string refusal = Pay(conn, 0f, sparks, false);
            if (refusal != null)
            {
                GameServer.Fail(conn, op, refusal);
                return;
            }
            link.currentTier = tier;
            RevivalLog.Info("Tech " + data.category + " unlocked to tier " + tier + " for " + sparks + " sparks");
            AnnounceTechLevels(conn, new[] { data.category }, new[] { tier });
            PlayerState.Save();
            GameServer.Reply(conn, op);
        }

        private static int TechLevel(PlayerVO player, TechCategory category)
        {
            TechCategoryLink link = player.techLevels.FirstOrDefault(l => l.category == category);
            return link == null ? 0 : link.currentTier;
        }

        private static void AnnounceTechLevels(Loopback.VirtualConnection conn, TechCategory[] categories, int[] levels)
        {
            GameServer.Event(conn, 144, new Dictionary<byte, object> { { 218, categories }, { 169, levels } });
        }

        // Tech tiers also unlock by themselves as the player levels up.
        private static bool SyncTechLevels(Loopback.VirtualConnection conn)
        {
            PlayerVO player = PlayerState.Current;
            List<TechCategory> categories = new List<TechCategory>();
            List<int> levels = new List<int>();
            foreach (TechCategoryLink link in player.techLevels)
            {
                PartsUpgardeCategoryData data;
                if (!WorkshopData.UpgradeTable.TryGetValue(link.category, out data))
                {
                    continue;
                }
                int level = Math.Min(data.maxTechTier, Math.Max(link.currentTier, WorkshopData.TierUnlockedAtLevel(data, player.level)));
                if (level != link.currentTier)
                {
                    link.currentTier = level;
                    categories.Add(link.category);
                    levels.Add(level);
                }
            }
            if (categories.Count == 0)
            {
                return false;
            }
            RevivalLog.Info("Tech levels now " + string.Join(", ", categories.Select((c, i) => c + " " + levels[i]).ToArray()) + " (player level " + player.level + ")");
            if (conn != null)
            {
                AnnounceTechLevels(conn, categories.ToArray(), levels.ToArray());
            }
            return true;
        }

        // ---- upgrade timers ----

        // Raises every part whose current step is over and drops schedules
        // that are complete or whose part is gone (sold). 'announce' sends the
        // schedule even when nothing came due, after a request changed it.
        private static bool FinishUpgrades(Loopback.VirtualConnection conn, long now, bool announce)
        {
            PlayerVO player = PlayerState.Current;
            List<PartVO> raised = new List<PartVO>();
            bool changed = false;
            foreach (PartUpgradeTimeSpan span in player.partInUpgrade.ToArray())
            {
                PartVO part = PlayerState.FindPart(span.partID);
                PartTemplate template = part == null ? null : TemplateOf(part);
                PartsUpgardeCategoryData data = part == null ? null : WorkshopData.CategoryOf(part.type);
                if (template == null || data == null)
                {
                    player.partInUpgrade.Remove(span);
                    changed = true;
                    continue;
                }
                int reached = part.currentTier;
                long end = span.startTime.Ticks - span.hurryOffset;
                for (int tier = span.startTier + 1; tier <= span.tier; tier++)
                {
                    end += WorkshopData.UpgradeTicks(data, tier);
                    if (end <= now && tier > reached)
                    {
                        reached = tier;
                    }
                }
                if (reached > part.currentTier)
                {
                    SetTier(part, template, reached);
                    raised.Add(part);
                    changed = true;
                    RevivalLog.Info("Upgrade step finished: part " + part.partID + " (" + part.indent + ") is now tier " + part.currentTier);
                }
                // Also covers a part that cannot go any higher.
                if (part.currentTier >= Math.Min(span.tier, template.maxTier))
                {
                    player.partInUpgrade.Remove(span);
                    changed = true;
                }
            }
            if (conn != null && (changed || announce))
            {
                if (raised.Count > 0)
                {
                    GameServer.Event(conn, 216, new Dictionary<byte, object> { { 231, DataSerialization.ProtoSerializeObject(raised) } });
                    AmmoOps.Resync(conn);
                }
                // The client reads this one with protobuf directly, as an array.
                GameServer.Event(conn, 140, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObject(player.partInUpgrade.ToArray()) } });
            }
            return changed;
        }
    }
}
