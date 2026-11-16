using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Arena;
using JunkWars.Client.Arena.Offline;
using JunkWars.Client.Robot;
using JunkWars.Common.Gameplay;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.ValueObjects;

namespace GarRevival;

// What a battle costs the garage: part wear and the consumable boosts used in the fight.
internal static class BattleUpkeep
{
    // Share of a part's battle damage that stays on it. A full repair costs 10% of the part's price
    // and a battle pays ~0.43 credits per damage point, so an even trade costs ~1/3 of the earnings.
    public const float WearShare = 0.1f;

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private static OfflineServerEmulator Rules => BattleManager.arena?.offlineServerEmulator;

    // Host or solo: what is left of each part of one robot, by part id.
    public static Dictionary<int, float> EndDurability(int actorId)
    {
        var left = new Dictionary<int, float>();
        var robot = Rules?.GetRobotById(actorId);
        if (robot == null) return left;
        foreach (var part in robot.GetPartsList())
        {
            if (part != null && part.statistics != null)
            {
                left[part.partID] = part.statistics.Durability.currentValue;
            }
        }
        return left;
    }

    // Wears the saved parts of the robot that fought and tells the client.
    public static void ApplyWear(Dictionary<int, float> endByPartId)
    {
        if (endByPartId == null || endByPartId.Count == 0) return;
        try
        {
            WorkshopOps.ApplyBattleWear(Loopback.Get(PhotonNetwork.networkingPeer), SelectedRobot(PlayerState.Current), endByPartId, WearShare);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Battle wear could not be applied", ex);
        }
    }

    // One entry per boost type slotted on the selected robot, with the amount the player owns.
    public static InventoryItems BattleInventory()
    {
        var carried = new InventoryItems();
        var player = PlayerState.Current;
        var robot = SelectedRobot(player);
        if (robot == null || robot.consumableBoostSlots == null || player.inventory == null) return carried;
        foreach (var type in robot.consumableBoostSlots.Select(s => s.type).Distinct())
        {
            var owned = player.inventory.GetItems<ConsumableBoostVO>().FirstOrDefault(b => b.type == type);
            if (owned != null && owned.quantity > 0)
            {
                carried.AddItem(PackageType.ConsumableBoost, new ConsumableBoostVO { type = type, quantity = owned.quantity });
            }
        }
        return carried;
    }

    // Hook on OfflineArenaCombatant.ExecuteConsumableBoost (empty in the client).
    // The avatar has already checked that the slot is filled, in stock and unused this battle.
    public static void Use(OfflineArenaCombatant combatant, int slotIndex)
    {
        var robot = combatant.theAvatar;
        if (robot == null || !robot.possesedByLocalPlayer) return;
        var slot = robot.robotAssembly.valueObject.consumableBoostSlots.Find(s => s.index == slotIndex);
        if (slot == null) return;
        RevivalLog.Info($"Boost used: slot {slotIndex} ({slot.type})");
        TakeFromStock(slot.type);
        if (SessionBattle.Active && Session.Role == Session.Roles.Client)
        {
            SessionBattle.SendBoost(robot.actorId, slotIndex, (byte)slot.type);
        }
        else
        {
            ApplyOnRules(robot.actorId, slotIndex, (byte)slot.type);
        }
    }

    // Host or solo: the effect in the battle rules, then the sound and robot-side effect on every screen.
    public static void ApplyOnRules(int actorId, int slotIndex, byte type)
    {
        var rules = Rules;
        var robot = rules?.GetRobotById(actorId);
        if (robot == null || !robot.active) return;
        // Energy and speed reuse the strongest pickup; chassis repair is the rules' revive.
        // Head repair only clears the robot's own aiming trouble, which is not in the rules.
        switch ((ConsumableBoostType)type)
        {
            case ConsumableBoostType.EnergyRefill:
                Pickup(rules, actorId, "Energy_Huge");
                break;
            case ConsumableBoostType.SpeedUp:
                Pickup(rules, actorId, "Speed_Huge");
                break;
            case ConsumableBoostType.ChassisRepair:
                typeof(OfflineServerEmulator).GetMethod("TryRessurectChassis", Hidden)?.Invoke(rules, [robot]);
                break;
        }
        RevivalLog.Info($"Boost applied for robot {actorId}: {(ConsumableBoostType)type}");
        Show(actorId, slotIndex);
        SessionBattle.AnnounceBoost(actorId, slotIndex);
    }

    private static void Pickup(OfflineServerEmulator rules, int actorId, string pickable)
    {
        var change = typeof(OfflineServerEmulator).GetMethod("ManagePickableData", Hidden)?.Invoke(rules, [actorId, pickable]);
        if (change != null)
        {
            rules.PublishStatisticsChangedEvent((StatisticChangeData)change);
        }
    }

    // The game's own handler: counts the boost down on the HUD, plays its sound, applies the robot-side part.
    public static void Show(int actorId, int slotIndex)
    {
        if (BattleManager.arena != null && BattleManager.arena.robots.TryGetValue(actorId, out var robot))
        {
            robot.OnConsumableBoostUse(slotIndex);
        }
    }

    private static RobotAssemblyVO SelectedRobot(PlayerVO player) =>
        player.selectedRobotIndex < player.ownedRobotAssemblies.Length ? player.ownedRobotAssemblies[player.selectedRobotIndex] : null;

    private static void TakeFromStock(ConsumableBoostType type)
    {
        var player = PlayerState.Current;
        var owned = player.inventory?.GetItems<ConsumableBoostVO>().FirstOrDefault(b => b.type == type);
        if (owned == null || owned.quantity <= 0) return;
        var used = new InventoryItems();
        used.AddItem(PackageType.ConsumableBoost, new ConsumableBoostVO { type = type, quantity = 1 });
        CosmeticsOps.TakeFromInventory(Loopback.Get(PhotonNetwork.networkingPeer), player, used);
        PlayerState.Save();
    }
}
