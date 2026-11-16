using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival;

// Repairs, inventory slots, renting, the spark exchange, timed boosts and consumables.
internal static partial class WorkshopOps
{
    // 205: repair robots, modules and/or parts. The price is the catalog's repair price for the
    // part's tier times the damaged share (DataManager.GetInventoryPartsPriceData), less the repairs boost.
    private static void RepairItems(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        var player = PlayerState.Current;
        var moduleIds = Ids(request, 232);
        foreach (int robotId in Ids(request, 236))
        {
            var robot = player.ownedRobotAssemblies.FirstOrDefault(r => r.assemblyID == robotId);
            if (robot != null)
            {
                moduleIds.AddRange(ModuleIdsOf(robot));
            }
        }
        var partIds = Ids(request, 231);
        foreach (int moduleId in moduleIds)
        {
            var module = PlayerState.FindModule(moduleId);
            if (module != null)
            {
                partIds.AddRange(PlayerState.PartIdsOf(module));
            }
        }
        var damaged = partIds.Distinct().Select(PlayerState.FindPart).Where(IsDamaged).ToList();
        float cost = RepairCost(damaged);
        if (player.credits < cost)
        {
            GameServer.Fail(conn, op, NotEnoughCreditsToRepair);
            return;
        }
        if (damaged.Count > 0)
        {
            Restore(conn, damaged);
            GameServer.ChangeCredits(conn, -cost, ItemAddedReason.Buy);
            PlayerState.Save();
        }
        RevivalLog.Info($"Repaired {damaged.Count} part(s) for {cost} credits");
        GameServer.Reply(conn, op);
    }

    private static List<int> ModuleIdsOf(RobotAssemblyVO robot)
    {
        List<int> ids = [robot.rootPartID];
        ids.AddRange((robot.dockingPointToModuleLinks ?? []).Select(l => l.childModuleID));
        return ids;
    }

    private static bool IsDamaged(PartVO part) =>
        part != null && part.statistics != null && part.statistics.Durability.maxValue > 0f
        && part.statistics.Durability.currentValue < part.statistics.Durability.maxValue;

    private static float RepairCost(List<PartVO> damaged)
    {
        float cost = 0f;
        foreach (var part in damaged)
        {
            if (Catalog.Prices.TryGetValue(part.indent, out var price))
            {
                var durability = part.statistics.Durability;
                cost += WorkshopData.PriceAt(price.data.repair, part.currentTier).GetTotalCredits() * (durability.maxValue - durability.currentValue) / durability.maxValue;
            }
        }
        float boost = Math.Min(1f, CommonConstants.BONUS_REPAIRS_VALUE * BoostScale(CommonConstants.BONUS_REPAIRS));
        return (float)Math.Round(cost * (1f - boost));
    }

    // Event 207 replaces the statistics of the parts it lists.
    private static void Restore(Loopback.VirtualConnection conn, List<PartVO> parts)
    {
        foreach (var part in parts)
        {
            part.statistics.Durability.currentValue = part.statistics.Durability.maxValue;
        }
        AnnounceStatistics(conn, parts);
    }

    private static void AnnounceStatistics(Loopback.VirtualConnection conn, List<PartVO> parts) =>
        GameServer.Event(conn, 207, new() { { 215, DataSerialization.ProtoSerializeObjectCompressed(parts.ToArray()) } });

    // After a battle: endByPartId is what the battle rules left of each part. A share of the damage
    // taken stays on the saved part; robots set to auto repair are repaired if the credits are there.
    public static void ApplyBattleWear(Loopback.VirtualConnection conn, RobotAssemblyVO robot, Dictionary<int, float> endByPartId, float wearShare)
    {
        List<PartVO> worn = [];
        foreach (var end in endByPartId)
        {
            var part = PlayerState.FindPart(end.Key);
            if (part == null || part.statistics == null || part.statistics.Durability.maxValue <= 0f) continue;
            var durability = part.statistics.Durability;
            float taken = Math.Max(0f, durability.currentValue - Math.Max(0f, end.Value));
            if (taken > 0f)
            {
                durability.currentValue = Math.Max(0f, durability.currentValue - taken * wearShare);
                worn.Add(part);
            }
        }
        RevivalLog.Info($"Battle wear: {worn.Count} of {endByPartId.Count} part(s) worn");
        if (worn.Count == 0) return;
        var damaged = worn.Where(IsDamaged).ToList();
        float cost = RepairCost(damaged);
        if (robot != null && robot.autoRepair && damaged.Count > 0 && PlayerState.Current.credits >= cost)
        {
            foreach (var part in damaged)
            {
                part.statistics.Durability.currentValue = part.statistics.Durability.maxValue;
            }
            GameServer.ChangeCredits(conn, -cost, ItemAddedReason.Buy);
            RevivalLog.Info($"Auto repair after battle: {damaged.Count} part(s) for {cost} credits");
        }
        AnnounceStatistics(conn, worn);
        PlayerState.Save();
    }

    // Same as the client's BuyInventorySlotsAction.CalculateNextSlotsPrice.
    private static float SlotsPrice(int owned, int count)
    {
        float perFive = owned < 25 ? 10f : ((int)Math.Round((owned - 25) / 5.0) + 1) * 85f;
        return (float)Math.Round((double)count * (double)(perFive / 5f));
    }

    // 196: inventory slots
    private static void BuyInventorySlots(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        if (!TryInt(request, 218, out int count) || count <= 0 || count > 100)
        {
            GameServer.Fail(conn, op, InvalidData);
            return;
        }
        var player = PlayerState.Current;
        float sparks = SlotsPrice(player.inventorySlots, count);
        string refusal = Pay(conn, 0f, sparks, false);
        if (refusal != null)
        {
            GameServer.Fail(conn, op, refusal);
            return;
        }
        player.inventorySlots += count;
        RevivalLog.Info($"Bought {count} inventory slots for {sparks} sparks; now {player.inventorySlots}");
        GameServer.Event(conn, 190, new() { { 218, player.inventorySlots } });
        PlayerState.Save();
        GameServer.Reply(conn, op);
    }

    // 180: rent. A rented item is an ordinary part or module whose parts carry an expiration date;
    // the client shows the time left and refuses to sell or upgrade it.
    private static void RentItem(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        string id = Text(request, 233);
        TryInt(request, 234, out int itemType);
        var player = PlayerState.Current;
        ModuleTemplate moduleTemplate = null;
        PartTemplate partTemplate = null;
        float credits;
        if (id != null && itemType == (int)ItemType.Module && Catalog.Modules.TryGetValue(id, out moduleTemplate))
        {
            credits = WorkshopData.RentCredits(moduleTemplate.moduleCaseIndent)
                + (moduleTemplate.dockingPointToPartLinks ?? []).Sum(l => WorkshopData.RentCredits(l.partIndent));
        }
        else if (id != null && itemType == (int)ItemType.Part && Catalog.Parts.TryGetValue(id, out partTemplate))
        {
            credits = WorkshopData.RentCredits(id);
        }
        else
        {
            GameServer.Fail(conn, op, itemType == (int)ItemType.Module ? "SM_0043" : "SM_0044");
            return;
        }
        string refusal = credits > 0f ? Pay(conn, credits, 0f, Flag(request, 218)) : InvalidPart;
        if (refusal != null)
        {
            GameServer.Fail(conn, op, refusal);
            return;
        }
        List<PartVO> parts = [];
        List<ModuleAssemblyVO> modules = [];
        if (moduleTemplate != null)
        {
            modules.Add(PlayerState.CreateModuleFromTemplate(player, moduleTemplate, parts));
        }
        else
        {
            parts.Add(PlayerState.CreatePart(player, partTemplate));
        }
        var expires = DateTime.UtcNow.AddDays(WorkshopData.RentDays);
        foreach (var part in parts)
        {
            part.expirationDate = expires;
        }
        RevivalLog.Info($"Rented {(ItemType)itemType} {id} for {credits} credits until {expires:u}");
        GameServer.AddItems(conn, parts, modules);
        PlayerState.Save();
        GameServer.Reply(conn, op);
    }

    // Rented parts go back once their time is up, only while the profile is prepared for sending:
    // the client does not expect parts to vanish from a robot the garage is showing.
    // A module goes with its rented case; anything attached below it comes loose.
    private static bool ReturnExpiredRentals(PlayerVO player, long now)
    {
        var gone = new HashSet<int>(player.ownedParts.Where(p => p.expirationDate != DateTime.MinValue && p.expirationDate.Ticks <= now).Select(p => p.partID));
        if (gone.Count == 0) return false;
        var detached = new HashSet<int>();
        foreach (var module in player.ownedModuleAssemblies.ToArray())
        {
            if (gone.Contains(module.moduleCaseID))
            {
                detached.Add(module.moduleID);
                player.ownedModuleAssemblies.Remove(module);
            }
            else if (module.dockingPointToPartLinks != null)
            {
                module.dockingPointToPartLinks.RemoveAll(l => gone.Contains(l.partID));
            }
        }
        foreach (var robot in player.ownedRobotAssemblies)
        {
            var links = robot.dockingPointToModuleLinks ?? [];
            if (detached.Contains(robot.rootPartID))
            {
                robot.rootPartID = -1;
                links.Clear();
                continue;
            }
            var loose = new HashSet<int>(detached);
            bool again = true;
            while (again)
            {
                again = false;
                foreach (var link in links.ToArray())
                {
                    if (loose.Contains(link.childModuleID) || loose.Contains(link.parentModuleID))
                    {
                        links.Remove(link);
                        loose.Add(link.childModuleID);
                        again = true;
                    }
                }
            }
        }
        player.ownedParts.RemoveAll(p => gone.Contains(p.partID));
        RevivalLog.Info($"Returned {gone.Count} rented part(s) whose time was up");
        return true;
    }

    // 160: sparks to credits
    private static void ConvertCurrency(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        float sparks = request.TryGetValue(213, out var raw) && raw is float asked ? (float)Math.Floor(asked) : 0f;
        var player = PlayerState.Current;
        if (!(sparks >= 1f) || sparks > WorkshopData.MaxSparksPerExchange || sparks > player.premiumCredits)
        {
            // The client shows its own text for any failure here.
            GameServer.Fail(conn, op, InvalidSparkAmount);
            return;
        }
        ChangeBalance(conn, sparks * CommonConstants.CurrencyConvertionRate, -sparks);
        RevivalLog.Info($"Exchanged {sparks} sparks for {sparks * CommonConstants.CurrencyConvertionRate} credits");
        PlayerState.Save();
        GameServer.Reply(conn, op);
    }

    // 157: timed boosts
    private static void ActivateTimedBuff(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        string indent = Text(request, 233);
        if (indent == null || !TryInt(request, 169, out int plan) || !WorkshopData.TimedBuffs().TryGetValue(indent, out var template)
            || plan < 0 || plan >= template.price.Length || plan >= template.duration.Length)
        {
            GameServer.Fail(conn, op, InvalidRequest);
            return;
        }
        var player = PlayerState.Current;
        long now = DateTime.UtcNow.Ticks;
        var active = ActiveBoosts(now);
        // Boosts with the same effect do not stack; the same boost can be bought again
        // to extend it (DataManager.CanAddTimedBuff).
        if (active.Any(b => b.indent != indent && WorkshopData.TimedBuffs()[b.indent].keywords.Intersect(template.keywords).Any()))
        {
            GameServer.Fail(conn, op, BoostAlreadyActive);
            return;
        }
        string refusal = Pay(conn, 0f, template.price[plan].items.GetTotalPremiumCredits(), false);
        if (refusal != null)
        {
            GameServer.Fail(conn, op, refusal);
            return;
        }
        long length = template.duration[plan] * TimeSpan.TicksPerMinute;
        var boost = active.FirstOrDefault(b => b.indent == indent);
        bool extend = boost != null;
        if (extend)
        {
            boost.endTime = new DateTime(boost.endTime.Ticks + length, DateTimeKind.Utc);
        }
        else
        {
            // Not listed as active means any entry left over has run out.
            player.timedBuffs.RemoveAll(b => b.indent == indent);
            boost = new TimedBuffVO { indent = indent, startTime = new DateTime(now, DateTimeKind.Utc), endTime = new DateTime(now + length, DateTimeKind.Utc) };
            player.timedBuffs.Add(boost);
        }
        RevivalLog.Info($"Timed boost {indent}{(extend ? " extended" : " activated")} until {boost.endTime:u}");
        GameServer.Event(conn, 143, new()
        {
            { 235, new[] { indent } },
            { 166, extend },
            { 168, boost.startTime.Ticks },
            { 167, boost.endTime.Ticks },
        });
        PlayerState.Save();
        GameServer.Reply(conn, op);
    }

    // The client counts an expired boost down to zero but drops it only when told. Entries the
    // table does not know (the client would trip over them) are cleared only before the profile is sent.
    private static bool ExpireBoosts(Loopback.VirtualConnection conn, long now)
    {
        var player = PlayerState.Current;
        var over = player.timedBuffs
            .Where(b => WorkshopData.TimedBuffs().ContainsKey(b.indent) ? b.endTime.Ticks <= now : conn == null).ToList();
        if (over.Count == 0) return false;
        player.timedBuffs.RemoveAll(over.Contains);
        RevivalLog.Info("Timed boost(s) ended: " + string.Join(", ", over.Select(b => b.indent).ToArray()));
        if (conn != null)
        {
            GameServer.Event(conn, 143, new() { { 218, over.Select(b => b.indent).Distinct().ToArray() }, { 166, false } });
        }
        return true;
    }

    // 163: consumables in a robot's three battle slots
    private static void SelectConsumableBoost(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        RobotAssemblyVO robot = null;
        if (TryInt(request, 168, out int robotId) && TryInt(request, 167, out int slot) && TryInt(request, 166, out int type))
        {
            robot = PlayerState.Current.ownedRobotAssemblies.FirstOrDefault(r => r.assemblyID == robotId);
            // -1 empties the slot.
            bool known = type >= 0 && type <= byte.MaxValue && Enum.IsDefined(typeof(ConsumableBoostType), (byte)type);
            if (robot != null && slot >= 0 && slot < CommonConstants.ConsumableBoostSlotsCount && (known || type == -1))
            {
                robot.consumableBoostSlots ??= [];
                // A consumable sits in one slot only.
                robot.consumableBoostSlots.RemoveAll(s => s.index == slot || (known && (int)s.type == type));
                if (known)
                {
                    robot.consumableBoostSlots.Add(new ConsumableBoostSlotVO { index = (byte)slot, type = (ConsumableBoostType)type });
                }
                PlayerState.Save();
                RevivalLog.Info($"Robot {robotId} consumable slots: " + string.Join(", ", robot.consumableBoostSlots.Select(s => s.index + "=" + s.type).ToArray()));
            }
        }
        ReplyBoostSlots(conn, op, robot);
    }

    // The client reads both keys without checking the return code and applies them to the robot
    // named, so even a refused request is answered with a robot's slots as they stand.
    private static void ReplyBoostSlots(Loopback.VirtualConnection conn, byte op, RobotAssemblyVO robot)
    {
        var player = PlayerState.Current;
        if (robot == null)
        {
            RevivalLog.Info($"op {op}: request not applied");
            robot = player.selectedRobotIndex < player.ownedRobotAssemblies.Length ? player.ownedRobotAssemblies[player.selectedRobotIndex] : player.ownedRobotAssemblies.FirstOrDefault();
        }
        if (robot == null) return;
        GameServer.Reply(conn, op, new()
        {
            { 168, robot.assemblyID },
            { 167, DataSerialization.ProtoSerializeObject(robot.consumableBoostSlots ?? []) },
        });
    }

    // 218 with type Package: buying consumables
    private static PackagePriceTemplate.PricePackage FindPackage(Dictionary<byte, object> request)
    {
        string id = Text(request, 233);
        if (id == null || !TryInt(request, 234, out int itemType) || itemType != (int)ItemType.Package) return null;
        return WorkshopData.ConsumablePackages().FirstOrDefault(p => p.indent == id);
    }

    private static void BuyPackage(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        var package = FindPackage(request);
        string refusal = Pay(conn, package.normalBuy.GetTotalCredits(), package.normalBuy.GetTotalPremiumCredits(), Flag(request, 218));
        if (refusal != null)
        {
            GameServer.Fail(conn, op, refusal);
            return;
        }
        var bought = new InventoryItems();
        foreach (var item in package.itemPackage.items.Where(i => i.type == PackageType.ConsumableBoost))
        {
            bought.AddItem(PackageType.ConsumableBoost, new ConsumableBoostVO
            {
                type = (ConsumableBoostType)Enum.Parse(typeof(ConsumableBoostType), item.indent),
                quantity = item.quantityInt,
            });
        }
        PlayerState.Current.inventory.MergeInventory(bought);
        RevivalLog.Info("Bought package " + package.indent);
        GameServer.Event(conn, 217, new()
        {
            { 168, (byte)ItemAddedReason.Buy },
            { 235, DataSerialization.ProtoSerializeObject(bought) },
        });
        PlayerState.Save();
        GameServer.Reply(conn, op);
    }
}
