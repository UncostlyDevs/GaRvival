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

namespace GarRevival
{
    // What a battle takes from and gives to the garage: parts come back worn
    // and need repairs, and the consumable boosts slotted on the robot can be
    // used once each during the fight.
    internal static class BattleUpkeep
    {
        // Share of the damage a part took in a battle that stays on it
        // afterwards. The original value is lost. A full repair costs 10% of a
        // part's price, and a battle pays about 0.43 credits per point of
        // damage dealt. At this value a starter robot's repairs come to about
        // a third of the earnings when it deals as much damage as it takes
        // (measured: 588 damage taken cost 135 credits to repair at 0.15).
        public const float WearShare = 0.1f;

        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

        private static OfflineServerEmulator Rules
        {
            get { return BattleManager.arena == null ? null : BattleManager.arena.offlineServerEmulator; }
        }

        // ---- wear ----

        // Host or solo: what is left of each part of one robot, by part id,
        // from the battle rules.
        public static Dictionary<int, float> EndDurability(int actorId)
        {
            Dictionary<int, float> left = new Dictionary<int, float>();
            OfflineServerEmulator rules = Rules;
            Robot robot = rules == null ? null : rules.GetRobotById(actorId);
            if (robot == null)
            {
                return left;
            }
            foreach (PartVO part in robot.GetPartsList())
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
            if (endByPartId == null || endByPartId.Count == 0)
            {
                return;
            }
            try
            {
                PlayerVO player = PlayerState.Current;
                RobotAssemblyVO robot = player.selectedRobotIndex < player.ownedRobotAssemblies.Length ? player.ownedRobotAssemblies[player.selectedRobotIndex] : null;
                WorkshopOps.ApplyBattleWear(Loopback.Get(PhotonNetwork.networkingPeer), robot, endByPartId, WearShare);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Battle wear could not be applied", ex);
            }
        }

        // ---- consumable boosts ----

        // The boosts the selected robot carries into a battle: one entry per
        // type slotted on it, with the amount the player owns.
        public static InventoryItems BattleInventory()
        {
            InventoryItems carried = new InventoryItems();
            PlayerVO player = PlayerState.Current;
            RobotAssemblyVO robot = player.selectedRobotIndex < player.ownedRobotAssemblies.Length ? player.ownedRobotAssemblies[player.selectedRobotIndex] : null;
            if (robot == null || robot.consumableBoostSlots == null || player.inventory == null)
            {
                return carried;
            }
            foreach (ConsumableBoostType type in robot.consumableBoostSlots.Select(s => s.type).Distinct())
            {
                ConsumableBoostVO owned = player.inventory.GetItems<ConsumableBoostVO>().FirstOrDefault(b => b.type == type);
                if (owned != null && owned.quantity > 0)
                {
                    carried.AddItem(PackageType.ConsumableBoost, new ConsumableBoostVO { type = type, quantity = owned.quantity });
                }
            }
            return carried;
        }

        // Hook on OfflineArenaCombatant.ExecuteConsumableBoost, which the
        // offline arena never implemented. The avatar has already checked that
        // the slot is filled, in stock and not used yet this battle.
        public static void Use(OfflineArenaCombatant combatant, int slotIndex)
        {
            RobotAvatar robot = combatant.theAvatar;
            if (robot == null || !robot.possesedByLocalPlayer)
            {
                return;
            }
            ConsumableBoostSlotVO slot = robot.robotAssembly.valueObject.consumableBoostSlots.Find(s => s.index == slotIndex);
            if (slot == null)
            {
                return;
            }
            RevivalLog.Info("Boost used: slot " + slotIndex + " (" + slot.type + ")");
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

        // Host or solo: the effect in the battle rules, then the sound and
        // the robot-side effect on every screen.
        public static void ApplyOnRules(int actorId, int slotIndex, byte type)
        {
            OfflineServerEmulator rules = Rules;
            Robot robot = rules == null ? null : rules.GetRobotById(actorId);
            if (robot == null || !robot.active)
            {
                return;
            }
            // Energy and speed reuse the strongest pickup of that kind; the
            // chassis repair is the rules' own revive. A head repair only
            // clears the robot's own aiming trouble, which is not in the rules.
            switch ((ConsumableBoostType)type)
            {
                case ConsumableBoostType.EnergyRefill:
                    Pickup(rules, actorId, "Energy_Huge");
                    break;
                case ConsumableBoostType.SpeedUp:
                    Pickup(rules, actorId, "Speed_Huge");
                    break;
                case ConsumableBoostType.ChassisRepair:
                    MethodInfo revive = typeof(OfflineServerEmulator).GetMethod("TryRessurectChassis", Hidden);
                    if (revive != null)
                    {
                        revive.Invoke(rules, new object[] { robot });
                    }
                    break;
            }
            RevivalLog.Info("Boost applied for robot " + actorId + ": " + (ConsumableBoostType)type);
            Show(actorId, slotIndex);
            SessionBattle.AnnounceBoost(actorId, slotIndex);
        }

        private static void Pickup(OfflineServerEmulator rules, int actorId, string pickable)
        {
            MethodInfo manage = typeof(OfflineServerEmulator).GetMethod("ManagePickableData", Hidden);
            object change = manage == null ? null : manage.Invoke(rules, new object[] { actorId, pickable });
            if (change != null)
            {
                rules.PublishStatisticsChangedEvent((StatisticChangeData)change);
            }
        }

        // The game's own handler: counts the boost down on the robot's HUD,
        // plays its sound and applies the robot-side part (chassis, head).
        public static void Show(int actorId, int slotIndex)
        {
            RobotAvatar robot;
            if (BattleManager.arena != null && BattleManager.arena.robots.TryGetValue(actorId, out robot))
            {
                robot.OnConsumableBoostUse(slotIndex);
            }
        }

        private static void TakeFromStock(ConsumableBoostType type)
        {
            PlayerVO player = PlayerState.Current;
            ConsumableBoostVO owned = player.inventory == null ? null : player.inventory.GetItems<ConsumableBoostVO>().FirstOrDefault(b => b.type == type);
            if (owned == null || owned.quantity <= 0)
            {
                return;
            }
            InventoryItems used = new InventoryItems();
            used.AddItem(PackageType.ConsumableBoost, new ConsumableBoostVO { type = type, quantity = 1 });
            CosmeticsOps.TakeFromInventory(Loopback.Get(PhotonNetwork.networkingPeer), player, used);
            PlayerState.Save();
        }
    }
}
