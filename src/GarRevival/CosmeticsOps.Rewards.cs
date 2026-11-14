using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Inventory;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;

namespace GarRevival
{
    // Handing things to the player: crate and battle loot, daily rewards and
    // store packs all end up as a list of package items that is applied to the
    // profile and announced with the events the client listens for
    // (217 items added, 220 currency, 141 colours, 218 items removed).
    internal static partial class CosmeticsOps
    {
        private static readonly System.Random Rng = new System.Random();

        internal sealed class Grant
        {
            public float Credits;
            public float Sparks;
            public readonly List<PartVO> Parts = new List<PartVO>();
            public readonly List<ModuleAssemblyVO> Modules = new List<ModuleAssemblyVO>();
            public readonly InventoryItems Inventory = new InventoryItems();
            public readonly List<int> Colors = new List<int>();
        }

        // Applies the items to the profile. Nothing is sent yet.
        internal static Grant Give(PlayerVO player, IEnumerable<PackageItem> items)
        {
            Grant grant = new Grant();
            foreach (PackageItem item in items)
            {
                int count = Math.Max(1, item.quantityInt);
                string indent = item.indent ?? string.Empty;
                switch (item.type)
                {
                    case PackageType.Credits:
                        grant.Credits += item.quantity;
                        break;
                    case PackageType.PremiumCredits:
                        grant.Sparks += item.quantity;
                        break;
                    case PackageType.Part:
                    {
                        PartTemplate template;
                        if (!Catalog.Parts.TryGetValue(indent, out template))
                        {
                            RevivalLog.Info("Reward skipped: unknown part " + indent);
                            break;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            grant.Parts.Add(PlayerState.CreatePart(player, template));
                        }
                        break;
                    }
                    case PackageType.Module:
                    {
                        ModuleTemplate template;
                        if (!Catalog.Modules.TryGetValue(indent, out template))
                        {
                            RevivalLog.Info("Reward skipped: unknown module " + indent);
                            break;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            // The module's own parts travel with it, as when buying one.
                            grant.Modules.Add(PlayerState.CreateModuleFromTemplate(player, template, grant.Parts));
                        }
                        break;
                    }
                    case PackageType.TintColor:
                    {
                        CosmeticsDesign.ColorDef color = ColorByIndent(indent);
                        if (color != null && !player.ownedTintColors.Contains(color.Index))
                        {
                            player.ownedTintColors.Add(color.Index);
                            grant.Colors.Add(color.Index);
                        }
                        break;
                    }
                    default:
                    {
                        InventoryItem vo = null;
                        try
                        {
                            vo = InventoryItemFactory.CreateItem(item.type, indent);
                        }
                        catch (ArgumentException)
                        {
                            // not a sub-type of this item type
                        }
                        if (vo == null)
                        {
                            RevivalLog.Info("Reward skipped: cannot give " + item.type + " '" + indent + "'");
                            break;
                        }
                        vo.quantity = count;
                        grant.Inventory.AddItem(item.type, vo);
                        break;
                    }
                }
            }
            player.credits += grant.Credits;
            player.premiumCredits += grant.Sparks;
            if (player.inventory == null)
            {
                player.inventory = new InventoryItems();
            }
            player.inventory.MergeInventory(grant.Inventory); // copies, so the grant stays as sent
            return grant;
        }

        // Tells the client. The items event carries the reason the client
        // routes on (crate window, end-of-battle rewards); itemsEventAlways
        // sends it even when empty, for screens that wait for it.
        internal static void Announce(Loopback.VirtualConnection conn, Grant grant, ItemAddedReason reason, bool itemsEventAlways)
        {
            bool hasItems = grant.Parts.Count > 0 || grant.Modules.Count > 0 || !grant.Inventory.IsEmpty;
            if (hasItems || itemsEventAlways)
            {
                Dictionary<byte, object> data = new Dictionary<byte, object> { { 168, (byte)reason } };
                if (grant.Parts.Count > 0)
                {
                    data[231] = DataSerialization.ProtoSerializeObject(grant.Parts);
                }
                if (grant.Modules.Count > 0)
                {
                    data[232] = DataSerialization.ProtoSerializeObject(grant.Modules);
                }
                if (!grant.Inventory.IsEmpty)
                {
                    data[235] = DataSerialization.ProtoSerializeObject(grant.Inventory);
                }
                GameServer.Event(conn, 217, data);
            }
            AnnounceCurrency(conn, grant.Credits, grant.Sparks, reason);
            if (grant.Colors.Count > 0)
            {
                GameServer.Event(conn, 141, new Dictionary<byte, object> { { 235, DataSerialization.ProtoSerializeObject(grant.Colors) } });
            }
        }

        // Event 220 after the balances have changed. The client takes the new
        // credits only when the credit delta is non-zero, and likewise sparks.
        internal static void AnnounceCurrency(Loopback.VirtualConnection conn, float creditsDelta, float sparksDelta, ItemAddedReason reason)
        {
            if (creditsDelta == 0f && sparksDelta == 0f)
            {
                return;
            }
            PlayerVO player = PlayerState.Current;
            GameServer.Event(conn, 220, new Dictionary<byte, object>
            {
                { 213, player.credits },
                { 214, creditsDelta },
                { 234, player.premiumCredits },
                { 218, sparksDelta },
                { 168, (byte)reason },
            });
        }

        // Removes consumables from the profile and tells the client (event 218).
        internal static void TakeFromInventory(Loopback.VirtualConnection conn, PlayerVO player, InventoryItems used)
        {
            player.inventory.SubtractInventory(used);
            GameServer.Event(conn, 218, new Dictionary<byte, object> { { 235, DataSerialization.ProtoSerializeObject(used) } });
        }

        internal static int Owned<T>(PlayerVO player, Predicate<T> match) where T : InventoryItem
        {
            T item = player.inventory == null ? null : player.inventory.FindItem(match);
            return item == null ? 0 : item.quantity;
        }

        internal static string Describe(IEnumerable<PackageItem> items)
        {
            string[] words = items.Select(i => i.quantityInt + " " + i.type + (string.IsNullOrEmpty(i.indent) ? string.Empty : " " + i.indent)).ToArray();
            return words.Length == 0 ? "nothing" : string.Join(", ", words);
        }

        // ---- loot tables ----

        internal static LootContext ContextFor(PlayerVO player, float performance, bool openedWithKey)
        {
            int rank = 1 + (Math.Max(1, player.level) - 1) / CosmeticsDesign.LevelsPerRank;
            return new LootContext(Math.Min(CommonConstants.MaxRobotRank, rank), performance, openedWithKey);
        }

        // Rolls a loot table: every random item on its own chance; every group
        // on its chance, then one weighted pick per slot. An item with no
        // sub-type named (say "any crate") gets a random one.
        internal static List<PackageItem> Roll(LootTemplate template, LootContext context, PlayerVO player)
        {
            List<PackageItem> loot = new List<PackageItem>();
            if (template.randomItems != null)
            {
                foreach (LootTemplate.RandomLootItem item in template.randomItems)
                {
                    if (Happens(item.probability, context))
                    {
                        AddLoot(loot, item.itemType, item.specificIndent, Between(item.minCount, item.maxCount, context), true);
                    }
                }
            }
            if (template.groups != null)
            {
                foreach (LootTemplate.LootGroup group in template.groups)
                {
                    if (group.possibleItems == null || !Happens(group.probability, context))
                    {
                        continue;
                    }
                    List<LootTemplate.LootGroupItem> pool = group.possibleItems.Where(i => CanReceive(player, i.itemType, i.specificIndent)).ToList();
                    int slots = Between(group.minSlotsCount, group.maxSlotsCount, context);
                    for (int slot = 0; slot < slots && pool.Count > 0; slot++)
                    {
                        LootTemplate.LootGroupItem pick = PickWeighted(pool, context);
                        if (!HasRandomSubtype(pick.itemType, pick.specificIndent))
                        {
                            pool.Remove(pick); // a named item drops once per group
                        }
                        AddLoot(loot, pick.itemType, pick.specificIndent, Between(pick.minCount, pick.maxCount, context), group.allowSameRandomItemSubtype);
                    }
                }
            }
            return loot;
        }

        private static bool HasRandomSubtype(PackageType type, string indent)
        {
            return string.IsNullOrEmpty(indent) && InventoryItemFactory.GetItemTypeSubtypes(type).Any();
        }

        // Colours are only worth dropping once; unknown ids never drop.
        private static bool CanReceive(PlayerVO player, PackageType type, string indent)
        {
            switch (type)
            {
                case PackageType.TintColor:
                    CosmeticsDesign.ColorDef color = ColorByIndent(indent);
                    return color != null && !player.ownedTintColors.Contains(color.Index);
                case PackageType.Part:
                    return indent != null && Catalog.Parts.ContainsKey(indent);
                case PackageType.Module:
                    return indent != null && Catalog.Modules.ContainsKey(indent);
                default:
                    return true;
            }
        }

        private static void AddLoot(List<PackageItem> loot, PackageType type, string indent, int count, bool allowSameSubtype)
        {
            if (count <= 0)
            {
                return;
            }
            indent = indent ?? string.Empty;
            if (HasRandomSubtype(type, indent))
            {
                List<string> subtypes = InventoryItemFactory.GetItemTypeSubtypes(type).ToList();
                if (!allowSameSubtype)
                {
                    subtypes.RemoveAll(s => loot.Any(l => l.type == type && l.indent == s));
                }
                if (subtypes.Count == 0)
                {
                    return;
                }
                indent = subtypes[Rng.Next(subtypes.Count)];
            }
            PackageItem same = loot.Find(l => l.type == type && l.indent == indent);
            if (same != null)
            {
                same.quantity += count;
            }
            else
            {
                loot.Add(new PackageItem { type = type, indent = indent, quantity = count });
            }
        }

        private static LootTemplate.LootGroupItem PickWeighted(List<LootTemplate.LootGroupItem> pool, LootContext context)
        {
            int[] weights = pool.Select(i => Math.Max(1, i.probabilityWeightInt == null ? 1 : i.probabilityWeightInt.GetIntValue(context))).ToArray();
            int roll = Rng.Next(weights.Sum());
            for (int i = 0; i < pool.Count; i++)
            {
                roll -= weights[i];
                if (roll < 0)
                {
                    return pool[i];
                }
            }
            return pool[pool.Count - 1];
        }

        private static bool Happens(LootNumericProperty probability, LootContext context)
        {
            float chance = (probability == null ? 1f : probability.GetValue(context)) * context.battlePerfCoeff;
            return Rng.NextDouble() < chance;
        }

        private static int Between(LootNumericProperty min, LootNumericProperty max, LootContext context)
        {
            int low = min == null ? 1 : min.GetIntValue(context);
            int high = Math.Max(low, max == null ? low : max.GetIntValue(context));
            return Rng.Next(low, high + 1);
        }
    }
}
