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

namespace GarRevival;

// Loot, daily rewards and store packs all become package items applied to the profile and
// announced with events 217 (items added), 220 (currency), 141 (colours), 218 (items removed).
internal static partial class CosmeticsOps
{
    private static readonly System.Random Rng = new();

    internal sealed class Grant
    {
        public float Credits;
        public float Sparks;
        public readonly List<PartVO> Parts = [];
        public readonly List<ModuleAssemblyVO> Modules = [];
        public readonly InventoryItems Inventory = new();
        public readonly List<int> Colors = [];
    }

    // Applies the items to the profile. Nothing is sent yet.
    internal static Grant Give(PlayerVO player, IEnumerable<PackageItem> items)
    {
        var grant = new Grant();
        foreach (var item in items)
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
                    if (!Catalog.Parts.TryGetValue(indent, out var template))
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
                    if (!Catalog.Modules.TryGetValue(indent, out var template))
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
                    var color = ColorByIndent(indent);
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
                        RevivalLog.Info($"Reward skipped: cannot give {item.type} '{indent}'");
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
        player.inventory ??= new();
        player.inventory.MergeInventory(grant.Inventory); // copies, so the grant stays as sent
        return grant;
    }

    // The items event carries the reason the client routes on (crate window, end-of-battle rewards);
    // itemsEventAlways sends it even when empty, for screens that wait for it.
    internal static void Announce(Loopback.VirtualConnection conn, Grant grant, ItemAddedReason reason, bool itemsEventAlways)
    {
        bool hasItems = grant.Parts.Count > 0 || grant.Modules.Count > 0 || !grant.Inventory.IsEmpty;
        if (hasItems || itemsEventAlways)
        {
            Dictionary<byte, object> data = new() { { 168, (byte)reason } };
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
            GameServer.Event(conn, 141, new() { { 235, DataSerialization.ProtoSerializeObject(grant.Colors) } });
        }
    }

    // Event 220 after the balances changed. The client takes the new credits only when the
    // credit delta is non-zero, and likewise sparks.
    internal static void AnnounceCurrency(Loopback.VirtualConnection conn, float creditsDelta, float sparksDelta, ItemAddedReason reason)
    {
        if (creditsDelta == 0f && sparksDelta == 0f) return;
        var player = PlayerState.Current;
        GameServer.Event(conn, 220, new()
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
        GameServer.Event(conn, 218, new() { { 235, DataSerialization.ProtoSerializeObject(used) } });
    }

    internal static int Owned<T>(PlayerVO player, Predicate<T> match) where T : InventoryItem =>
        player.inventory?.FindItem(match)?.quantity ?? 0;

    internal static string Describe(IEnumerable<PackageItem> items)
    {
        string[] words = items.Select(i => $"{i.quantityInt} {i.type}" + (string.IsNullOrEmpty(i.indent) ? string.Empty : " " + i.indent)).ToArray();
        return words.Length == 0 ? "nothing" : string.Join(", ", words);
    }

    internal static LootContext ContextFor(PlayerVO player, float performance, bool openedWithKey)
    {
        int rank = 1 + (Math.Max(1, player.level) - 1) / CosmeticsDesign.LevelsPerRank;
        return new LootContext(Math.Min(CommonConstants.MaxRobotRank, rank), performance, openedWithKey);
    }

    // Every random item rolls on its own chance; every group on its chance, then one weighted pick
    // per slot. An item with no sub-type named (say "any crate") gets a random one.
    internal static List<PackageItem> Roll(LootTemplate template, LootContext context, PlayerVO player)
    {
        List<PackageItem> loot = [];
        if (template.randomItems != null)
        {
            foreach (var item in template.randomItems)
            {
                if (Happens(item.probability, context))
                {
                    AddLoot(loot, item.itemType, item.specificIndent, Between(item.minCount, item.maxCount, context), true);
                }
            }
        }
        if (template.groups != null)
        {
            foreach (var group in template.groups)
            {
                if (group.possibleItems == null || !Happens(group.probability, context)) continue;
                var pool = group.possibleItems.Where(i => CanReceive(player, i.itemType, i.specificIndent)).ToList();
                int slots = Between(group.minSlotsCount, group.maxSlotsCount, context);
                for (int slot = 0; slot < slots && pool.Count > 0; slot++)
                {
                    var pick = PickWeighted(pool, context);
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

    private static bool HasRandomSubtype(PackageType type, string indent) =>
        string.IsNullOrEmpty(indent) && InventoryItemFactory.GetItemTypeSubtypes(type).Any();

    // Colours are only worth dropping once; unknown ids never drop.
    private static bool CanReceive(PlayerVO player, PackageType type, string indent) => type switch
    {
        PackageType.TintColor => ColorByIndent(indent) is { } color && !player.ownedTintColors.Contains(color.Index),
        PackageType.Part => indent != null && Catalog.Parts.ContainsKey(indent),
        PackageType.Module => indent != null && Catalog.Modules.ContainsKey(indent),
        _ => true,
    };

    private static void AddLoot(List<PackageItem> loot, PackageType type, string indent, int count, bool allowSameSubtype)
    {
        if (count <= 0) return;
        indent ??= string.Empty;
        if (HasRandomSubtype(type, indent))
        {
            var subtypes = InventoryItemFactory.GetItemTypeSubtypes(type).ToList();
            if (!allowSameSubtype)
            {
                subtypes.RemoveAll(s => loot.Any(l => l.type == type && l.indent == s));
            }
            if (subtypes.Count == 0) return;
            indent = subtypes[Rng.Next(subtypes.Count)];
        }
        var same = loot.Find(l => l.type == type && l.indent == indent);
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
            if (roll < 0) return pool[i];
        }
        return pool[pool.Count - 1];
    }

    private static bool Happens(LootNumericProperty probability, LootContext context)
    {
        float chance = (probability?.GetValue(context) ?? 1f) * context.battlePerfCoeff;
        return Rng.NextDouble() < chance;
    }

    private static int Between(LootNumericProperty min, LootNumericProperty max, LootContext context)
    {
        int low = min == null ? 1 : min.GetIntValue(context);
        int high = Math.Max(low, max == null ? low : max.GetIntValue(context));
        return Rng.Next(low, high + 1);
    }
}
