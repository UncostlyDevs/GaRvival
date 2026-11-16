using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Client;
using JunkWars.Client.Arena;
using JunkWars.Client.Robot;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using JunkWars.Common.Utils;
using UnityEngine;
using AvatarBarrel = JunkWars.Client.Robot.WeaponBarrel;
using RulesBarrel = JunkWars.Client.Arena.Offline.WeaponBarrel;

namespace GarRevival;

// Ammo stock, loads and battle use. The client's garage does the bookkeeping (nodes per barrel,
// loading, buying what is missing); the server checks it, sells rounds and takes away what a
// battle used. The client learns about battle use from the profile reload on garage entry.
internal static class AmmoOps
{
    // What the local robot went into the current battle with, by barrel part id.
    private static readonly Dictionary<int, AmmoDistributionNode> StartLoad = new();

    // Off: every battle starts fully loaded and uses nothing up.
    public static bool Economy => ServerRules.Active != null ? ServerRules.Active.Ammo : Settings.Ammo;

    // op 200. A failure here would log the player out, so it always succeeds.
    public static void SetDistribution(Loopback.VirtualConnection conn, Dictionary<byte, object> request)
    {
        bool resend = false;
        try
        {
            var sent = DataSerialization.ProtoDeserializeObject<AmmoDistributionVO>((byte[])request[218]);
            var asked = sent?.nodes ?? [];
            var player = PlayerState.Current;
            var nodes = Normalize(player, asked, out bool stockMoved);
            Distribution(player).nodes = nodes;
            PlayerState.Save();
            resend = stockMoved || !Same(asked, nodes);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("op 200: ammo distribution not applied", ex);
        }
        GameServer.Reply(conn, 200);
        if (resend)
        {
            Send(conn, PlayerState.Current);
        }
    }

    private sealed class Line
    {
        public string Type;
        public int Tier;
        public int Quantity;
    }

    // op 201
    public static void Buy(Loopback.VirtualConnection conn, Dictionary<byte, object> request)
    {
        try
        {
            var order = DataSerialization.ProtoDeserializeObjectCompressed<AmmoBuyData[]>((byte[])request[218]) ?? [];
            bool auto = request.ContainsKey(234) && Convert.ToInt32(request[234]) == 1;
            var lines = order.Where(o => o != null && o.tierData != null && AmmoDesign.IsKnown(o.indent))
                .SelectMany(o => o.tierData.Where(t => t.Key >= 0 && t.Key <= Catalog.MaxTier && t.Value > 0)
                    .Select(t => new Line { Type = o.indent, Tier = t.Key, Quantity = t.Value }))
                .ToList();
            var player = PlayerState.Current;
            // Same sum and rounding as the supply screen's total.
            if (!auto && Mathf.Ceil(lines.Sum(l => l.Quantity * Catalog.BuyPrice(l.Type, l.Tier))) > player.credits)
            {
                GameServer.Fail(conn, 201, "Not enough credits");
                return;
            }
            float cost = Mathf.Ceil(Purchase(player, lines));
            GameServer.ChangeCredits(conn, -cost, ItemAddedReason.Buy);
            PlayerState.Save();
            RevivalLog.Info($"Ammo bought{(auto ? " (auto resupply)" : string.Empty)} for {cost}: " +
                string.Join(", ", lines.Select(l => $"{l.Quantity} {l.Type} t{l.Tier}").ToArray()));
            Dictionary<byte, object> reply = new();
            if (player.ownedAmmo.Count > 0)
            {
                reply[218] = DataSerialization.ProtoSerializeObject(player.ownedAmmo);
            }
            if (auto && cost > 0f)
            {
                reply[213] = cost; // the client unboxes a float
            }
            GameServer.Reply(conn, 201, reply);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("op 201: ammo purchase failed", ex);
            GameServer.Fail(conn, 201, "The server could not do that (see revival.log)");
        }
    }

    // Adds only what the credits cover (auto resupply takes what it can afford).
    // Returns the credits spent before rounding.
    private static float Purchase(PlayerVO player, List<Line> lines)
    {
        float budget = Mathf.Floor(player.credits);
        float spent = 0f;
        foreach (var line in lines)
        {
            float price = Catalog.BuyPrice(line.Type, line.Tier);
            line.Quantity = price <= 0f ? line.Quantity : Math.Min(line.Quantity, (int)((budget - spent) / price));
            if (line.Quantity > 0)
            {
                AddStock(player, line.Type, line.Tier, line.Quantity);
                spent += line.Quantity * price;
            }
        }
        lines.RemoveAll(l => l.Quantity <= 0);
        return spent;
    }

    // Brings the distribution in line with the robots (a barrel swapped inside a module gets no
    // node from the client) and tells the client if that changed anything. Call after robot/module events.
    public static void Resync(Loopback.VirtualConnection conn, bool force = false)
    {
        try
        {
            var player = PlayerState.Current;
            bool changed = Rebuild(player);
            if (changed)
            {
                PlayerState.Save();
            }
            // Outside the garage the client cannot buy; the garage reload carries the state.
            if ((changed || force) && conn != null && GameManager.Instance != null && GameManager.Instance.currentGameState == GameState.Garage
                && PhotonNetwork.connectionStateDetailed == PeerState.JoinedLobby)
            {
                Send(conn, player);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Ammo resync failed", ex);
        }
    }

    // A bought barrel comes with one load of what it fires first.
    public static void OnPartsBought(Loopback.VirtualConnection conn, IEnumerable<PartVO> parts)
    {
        try
        {
            if (Grant(PlayerState.Current, parts, 1))
            {
                Resync(conn, true);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Ammo for a bought barrel not granted", ex);
        }
    }

    // On every profile load: profiles without ammo get starter loads for their ammo barrels,
    // and nodes follow the catalog.
    public static bool PrepareProfile(PlayerVO player)
    {
        try
        {
            bool granted = player.ownedAmmo.Count == 0 && Grant(player, player.ownedParts, AmmoDesign.StarterLoads);
            return Rebuild(player) | granted;
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Ammo profile check failed", ex);
            return false;
        }
    }

    private static bool Rebuild(PlayerVO player)
    {
        var distribution = Distribution(player);
        var nodes = Normalize(player, distribution.nodes, out bool stockMoved);
        bool changed = stockMoved || !Same(distribution.nodes, nodes);
        distribution.nodes = nodes;
        return changed;
    }

    private static bool Grant(PlayerVO player, IEnumerable<PartVO> parts, int loads)
    {
        bool any = false;
        foreach (var part in parts.Where(IsBarrel))
        {
            var barrel = BarrelOf(part);
            if (UsesAmmo(barrel))
            {
                AddStock(player, barrel.usableAmmoTypes[0], part.currentTier, loads * (int)barrel.MaxAmmo);
                any = true;
            }
        }
        return any;
    }

    private static void Send(Loopback.VirtualConnection conn, PlayerVO player)
    {
        Dictionary<byte, object> data = new() { { 231, DataSerialization.ProtoSerializeObject(Distribution(player)) } };
        if (player.ownedAmmo.Count > 0)
        {
            data[215] = DataSerialization.ProtoSerializeObject(player.ownedAmmo);
        }
        GameServer.Event(conn, 192, data);
    }

    // Server copy of Player.RecalculateAmmoSupplyVO and RedistributeAmmoSupplyVO: one node per
    // mounted barrel, capacity and type from the catalog, current <= desired <= max, and per
    // ammo type and tier no more loaded than owned.
    private static List<AmmoDistributionNode> Normalize(PlayerVO player, List<AmmoDistributionNode> proposed, out bool stockMoved)
    {
        var known = ById(Distribution(player).nodes);
        var asked = ById(proposed);
        stockMoved = false;
        List<AmmoDistributionNode> nodes = [];
        foreach (var part in MountedBarrels(player))
        {
            known.TryGetValue(part.partID, out var previous);
            var from = asked.TryGetValue(part.partID, out var sent) ? sent : previous;
            var node = Fix(part, from, previous);
            // An upgrade turns the loaded rounds into rounds of the new tier.
            if (previous != null && previous.tier != node.tier && previous.desiredAmmoType == node.desiredAmmoType && node.desiredAmmoType.Length > 0)
            {
                int moved = Math.Min(previous.currentAmmount, Stock(player, node.desiredAmmoType, previous.tier));
                AddStock(player, node.desiredAmmoType, previous.tier, -moved);
                AddStock(player, node.desiredAmmoType, node.tier, moved);
                stockMoved |= moved > 0;
            }
            nodes.Add(node);
        }
        foreach (var group in nodes.Where(n => n.desiredAmmoType.Length > 0).GroupBy(n => n.desiredAmmoType + "|" + n.tier))
        {
            var first = group.First();
            int left = Stock(player, first.desiredAmmoType, first.tier);
            foreach (var node in group)
            {
                node.currentAmmount = Math.Min(node.currentAmmount, left);
                left -= node.currentAmmount;
            }
            foreach (var node in group)
            {
                int load = Math.Min(node.desiredAmmount - node.currentAmmount, left);
                node.currentAmmount += load;
                left -= load;
            }
        }
        return nodes;
    }

    private static AmmoDistributionNode Fix(PartVO part, AmmoDistributionNode from, AmmoDistributionNode previous)
    {
        var barrel = BarrelOf(part);
        var node = new AmmoDistributionNode { moudleID = part.partID, tier = part.currentTier, desiredAmmoType = string.Empty };
        if (!UsesAmmo(barrel)) return node;
        node.maxAmmount = (int)barrel.MaxAmmo;
        node.desiredAmmoType = from != null && barrel.usableAmmoTypes.Contains(from.desiredAmmoType) ? from.desiredAmmoType : barrel.usableAmmoTypes[0];
        // New to the server, or made while the barrel had no ammo: load it full.
        bool firstSeen = from == null || previous == null || previous.maxAmmount <= 0 || string.IsNullOrEmpty(previous.desiredAmmoType);
        node.desiredAmmount = firstSeen ? node.maxAmmount : Mathf.Clamp(from.desiredAmmount, 0, node.maxAmmount);
        node.currentAmmount = from == null ? 0 : Mathf.Clamp(from.currentAmmount, 0, node.desiredAmmount);
        return node;
    }

    private static bool Same(List<AmmoDistributionNode> a, List<AmmoDistributionNode> b) =>
        a.Count == b.Count && b.All(n => a.Any(m => m.moudleID == n.moudleID && m.tier == n.tier && m.currentAmmount == n.currentAmmount
            && m.desiredAmmount == n.desiredAmmount && m.maxAmmount == n.maxAmmount && m.desiredAmmoType == n.desiredAmmoType));

    // Barrels in the weapon modules mounted on any robot, as the client counts them.
    private static IEnumerable<PartVO> MountedBarrels(PlayerVO player)
    {
        var modules = player.ownedModuleAssemblies.GroupBy(m => m.moduleID).ToDictionary(g => g.Key, g => g.First());
        var parts = player.ownedParts.GroupBy(p => p.partID).ToDictionary(g => g.Key, g => g.First());
        return player.ownedRobotAssemblies
            .SelectMany(r => r.dockingPointToModuleLinks ?? [])
            .Select(l => modules.ContainsKey(l.childModuleID) ? modules[l.childModuleID] : null)
            .Where(m => m != null && m.type == LogicalType.Weapon && m.dockingPointToPartLinks != null)
            .SelectMany(m => m.dockingPointToPartLinks)
            .Select(l => parts.ContainsKey(l.partID) ? parts[l.partID] : null)
            .Where(p => p != null && IsBarrel(p))
            .Distinct();
    }

    // The local robot's barrels load what the saved distribution has loaded. The avatar reads
    // maxAmmount and the battle rules currentAmmount, so both carry the load; nodes are rewritten
    // in place because the battle uses the first node of a barrel.
    public static void LoadActor(ActorVO actor)
    {
        try
        {
            var saved = ById(Distribution(PlayerState.Current).nodes);
            foreach (var part in actor.parts.Where(IsBarrel))
            {
                var node = NodeOf(actor, part.partID);
                saved.TryGetValue(part.partID, out var own);
                node.desiredAmmoType = own == null ? string.Empty : own.desiredAmmoType ?? string.Empty;
                node.currentAmmount = own == null ? 0 : own.currentAmmount;
            }
            NormalizeActor(actor, false);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not load ammo for the battle", ex);
        }
    }

    // Every barrel of a robot entering a battle gets exactly one node with the catalog's capacity;
    // full when the economy is off or for bots. The host also runs this on robots players send.
    public static void NormalizeActor(ActorVO actor, bool full)
    {
        try
        {
            foreach (var part in (actor.parts ?? []).Where(IsBarrel))
            {
                var node = NodeOf(actor, part.partID);
                var barrel = BarrelOf(part);
                node.tier = part.currentTier;
                if (!UsesAmmo(barrel))
                {
                    node.desiredAmmoType = string.Empty;
                    node.currentAmmount = node.desiredAmmount = node.maxAmmount = 0;
                    continue;
                }
                if (!barrel.usableAmmoTypes.Contains(node.desiredAmmoType))
                {
                    node.desiredAmmoType = barrel.usableAmmoTypes[0];
                }
                int max = (int)barrel.MaxAmmo;
                int loaded = full || !Economy ? max : Mathf.Clamp(node.currentAmmount, 0, max);
                node.currentAmmount = node.desiredAmmount = node.maxAmmount = loaded;
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not check a robot's ammo", ex);
        }
    }

    // Battle start: the local robot's nodes as the battle got them (the host's roster in a hosted battle).
    public static void RecordStart(ActorVO actor)
    {
        StartLoad.Clear();
        if (actor?.ammoDistribution?.nodes == null) return;
        foreach (var node in actor.ammoDistribution.nodes.Where(n => !string.IsNullOrEmpty(n.desiredAmmoType) && n.currentAmmount > 0))
        {
            if (!StartLoad.ContainsKey(node.moudleID))
            {
                StartLoad[node.moudleID] = node.ShallowCopy();
            }
        }
    }

    // Host or solo: rounds left in each ammo barrel of one robot.
    public static Dictionary<int, int> EndAmmo(int actorId)
    {
        try
        {
            return RulesBarrels(actorId).Where(b => !string.IsNullOrEmpty(b.Value.CurrentAmmoType))
                .ToDictionary(b => b.Key, b => (int)b.Value.CurrentAmmo);
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not read the ammo left", ex);
            return new();
        }
    }

    // Takes the rounds the battle used from the stock and the loads.
    // Pickups never add stock: what is left counts up to what was loaded.
    public static void ApplyBattleUse(Dictionary<int, int> remaining)
    {
        try
        {
            var player = PlayerState.Current;
            var nodes = ById(Distribution(player).nodes);
            int total = 0;
            foreach (var start in StartLoad.Values)
            {
                if (!remaining.TryGetValue(start.moudleID, out int left)) continue;
                int used = start.currentAmmount - Mathf.Clamp(left, 0, start.currentAmmount);
                if (used <= 0) continue;
                AddStock(player, start.desiredAmmoType, start.tier, -used);
                if (nodes.TryGetValue(start.moudleID, out var node) && node.desiredAmmoType == start.desiredAmmoType)
                {
                    node.currentAmmount = Math.Max(0, node.currentAmmount - used);
                }
                total += used;
            }
            StartLoad.Clear();
            if (total > 0)
            {
                RevivalLog.Info($"Battle used {total} rounds of ammo");
                PlayerState.Save();
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Ammo use could not be applied", ex);
        }
    }

    // Replaces RobotAvatar.AddFullAmmo, which gives energy barrels -1 rounds (the bot AI then
    // always looks for ammo) and ignores the load. The test ground fills both sides.
    public static void OnAddFullAmmo(RobotAvatar avatar)
    {
        bool testGround = BattleManager.arena is TestGround;
        var rules = RulesBarrels(avatar.actorId);
        foreach (var barrel in AvatarBarrels(avatar))
        {
            if (barrel.AmmoPerShot <= 0f || barrel.MaxAmmo <= 0f)
            {
                barrel.CurrentAmmo = 0f;
                continue;
            }
            rules.TryGetValue(barrel.partVO.partID, out var rule);
            if (testGround && rule != null)
            {
                rule.CurrentAmmo = rule.DesiredAmmo = rule.MaxAmmo;
            }
            float loaded = rule != null ? rule.CurrentAmmo : testGround ? barrel.MaxAmmo : barrel.CurrentAmmo;
            barrel.CurrentAmmo = Mathf.Clamp(loaded, 0f, barrel.MaxAmmo);
        }
    }

    private static IEnumerable<AvatarBarrel> AvatarBarrels(RobotAvatar avatar) =>
        (avatar.weapons ?? []).Where(w => w != null && w.weaponBarrels != null)
            .SelectMany(w => w.weaponBarrels).Where(b => b != null && b.partVO != null);

    private static Dictionary<int, RulesBarrel> RulesBarrels(int actorId)
    {
        Dictionary<int, RulesBarrel> barrels = new();
        var robot = BattleManager.arena?.offlineServerEmulator?.GetRobotById(actorId);
        if (robot?.weapons == null) return barrels;
        foreach (var barrel in robot.weapons.Where(w => w != null && w.weaponBarrels != null).SelectMany(w => w.weaponBarrels))
        {
            if (barrel != null && barrel.partVO != null && !barrels.ContainsKey(barrel.partVO.partID))
            {
                barrels[barrel.partVO.partID] = barrel;
            }
        }
        return barrels;
    }

    private static bool IsBarrel(PartVO part) => part != null && part.type == LogicalType.WeaponMuzzle;

    // The catalog's numbers, so a robot sent by another player cannot claim more.
    private static WeaponBarrelVO BarrelOf(PartVO part)
    {
        if (Catalog.Parts != null && Catalog.Parts.TryGetValue(part.indent ?? string.Empty, out var template))
        {
            return template.partTierableStatistics[Mathf.Clamp(part.currentTier, 0, template.partTierableStatistics.Length - 1)].partSpesificData as WeaponBarrelVO;
        }
        return part.partSpesificData as WeaponBarrelVO;
    }

    private static bool UsesAmmo(WeaponBarrelVO barrel) =>
        barrel != null && barrel.AmmoPerShot > 0f && barrel.MaxAmmo > 0f && barrel.usableAmmoTypes != null && barrel.usableAmmoTypes.Length > 0;

    private static AmmoDistributionVO Distribution(PlayerVO player)
    {
        player.ammoDistribution ??= new();
        player.ammoDistribution.nodes ??= [];
        return player.ammoDistribution;
    }

    private static Dictionary<int, AmmoDistributionNode> ById(IEnumerable<AmmoDistributionNode> nodes) =>
        nodes.Where(n => n != null).GroupBy(n => n.moudleID).ToDictionary(g => g.Key, g => g.First());

    private static AmmoDistributionNode NodeOf(ActorVO actor, int partId)
    {
        actor.ammoDistribution ??= new();
        actor.ammoDistribution.nodes ??= [];
        var node = actor.ammoDistribution.nodes.FirstOrDefault(n => n != null && n.moudleID == partId);
        if (node == null)
        {
            node = new AmmoDistributionNode { moudleID = partId, desiredAmmoType = string.Empty };
            actor.ammoDistribution.nodes.Add(node);
        }
        return node;
    }

    private static int Stock(PlayerVO player, string type, int tier)
    {
        var owned = player.ownedAmmo.FirstOrDefault(a => a != null && a.indent == type);
        return owned != null && owned.quantity != null && tier >= 0 && tier < owned.quantity.Length ? owned.quantity[tier] : 0;
    }

    private static void AddStock(PlayerVO player, string type, int tier, int delta)
    {
        if (delta == 0 || tier < 0 || tier > Catalog.MaxTier) return;
        var owned = player.ownedAmmo.FirstOrDefault(a => a != null && a.indent == type);
        if (owned == null)
        {
            if (delta < 0) return;
            owned = new AmmonitionVO { indent = type, quantity = new int[Catalog.MaxTier + 1] };
            player.ownedAmmo.Add(owned);
        }
        if (owned.quantity == null || owned.quantity.Length <= Catalog.MaxTier)
        {
            var quantity = new int[Catalog.MaxTier + 1];
            if (owned.quantity != null)
            {
                Array.Copy(owned.quantity, quantity, owned.quantity.Length);
            }
            owned.quantity = quantity;
        }
        owned.quantity[tier] = Math.Max(0, owned.quantity[tier] + delta);
    }
}
