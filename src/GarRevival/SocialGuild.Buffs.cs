using System;
using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using JunkWars.Common.Guild.Enums;
using JunkWars.Common.Guild.Templates;
using JunkWars.Common.Guild.ValueObjects;
using JunkWars.Common.Packages;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Utils;
using UnityEngine;
using ClientGuild = JunkWars.Client.Guild;
using DataManager = JunkWars.Client.DataManager;
using GuildManager = JunkWars.Client.GuildManager;

namespace GarRevival;

// Guild benefits: six structures of five levels. An officer unlocks a level (it builds for a while, or is hurried
// with sparks) and activates it for a day. Kept in GuildVO.unlockedBuffs, upgradingBuffs (build start per unlocked
// benefit) and activeBuffs (expiry); copies merge by union, earliest start, latest expiry. Spending is a tally.
internal static partial class SocialGuild
{
    private const int BuffLevels = 5;
    // Designed effect per level, as the client applies it (amount = x):
    //   Damage x (1 + x/100); Defence taken / (1 + x/100); Accuracy + x/100; Speed + x km/h;
    //   Debuffs (field repair, slow, energy drain) times / (1 + x/100); Pickables amounts and durations x (1 + x/100).
    private static readonly Dictionary<GuildBuffCategory, float[]> BuffEffect = new()
    {
        { GuildBuffCategory.Damage, [2f, 4f, 6f, 8f, 10f] },
        { GuildBuffCategory.Defence, [2f, 4f, 6f, 8f, 10f] },
        { GuildBuffCategory.Accuracy, [1f, 2f, 3f, 4f, 5f] },
        { GuildBuffCategory.Speed, [1f, 2f, 3f, 4f, 5f] },
        { GuildBuffCategory.Debuffs, [5f, 10f, 15f, 20f, 25f] },
        { GuildBuffCategory.Pickables, [5f, 10f, 15f, 20f, 25f] },
    };
    // Per level: building time, vault credits to unlock (the guild level's experience is required, not spent),
    // sparks to finish building at once, and vault credits per activation.
    private static readonly int[] BuffUnlockMinutes = [30, 120, 360, 720, 1440];
    private static readonly float[] BuffUnlockCredits = [10000f, 25000f, 60000f, 150000f, 400000f];
    private static readonly float[] BuffHurrySparks = [10f, 25f, 50f, 100f, 200f];
    private static readonly float[] BuffActivateCredits = [2000f, 5000f, 10000f, 20000f, 40000f];
    private const int BuffActiveMinutes = 1440;

    private static Dictionary<string, GuildBuffTemplate> _buffTemplates;
    private static Loopback.Delivery _pendingBuffSync;
    private static float _nextBuffTick;

    // Op 169. The garage panel has one slot per structure and level; the client's texts are keyed GB_<Category>_<level>.
    public static Dictionary<string, GuildBuffTemplate> BuffTemplates() =>
        _buffTemplates ??= BuffEffect
            .SelectMany(e => Enumerable.Range(1, BuffLevels).Select(level => NewBuffTemplate(e.Key, level, e.Value[level - 1])))
            .ToDictionary(t => t.indent);

    private static GuildBuffTemplate NewBuffTemplate(GuildBuffCategory category, int level, float amount)
    {
        string indent = "GB_" + category + "_" + level;
        return new GuildBuffTemplate
        {
            indent = indent,
            uiData = new UIData { name = indent + "_Name", description = indent + "_Description" },
            level = level,
            category = category,
            // GuildBuffType lists the same effects in the same order.
            data = [new GuildBuffData { type = (GuildBuffType)category, amount = amount }],
            duration = BuffActiveMinutes,
            unlockTime = BuffUnlockMinutes[level - 1],
            unlockPrice = [Price(PackageType.Credits, BuffUnlockCredits[level - 1]), Price(PackageType.Experience, UpgradeExperience[level - 1])],
            buyPrice = [Price(PackageType.Credits, BuffActivateCredits[level - 1])],
            hurryPrice = [Price(PackageType.PremiumCredits, BuffHurrySparks[level - 1])],
        };
    }

    private static PricePackageItem Price(PackageType type, float quantity) => new() { type = type, quantity = quantity };

    private static double Cost(PricePackageItem[] price, PackageType type) => price.Where(p => p.type == type).Sum(p => (double)p.quantity);

    private static GuildBuffTemplate FindBuff(string indent) =>
        indent != null && BuffTemplates().TryGetValue(indent, out var template) ? template : null;

    private static GuildBuffUpgradeVO Ledger(GuildVO guild, string indent) => guild.upgradingBuffs.FirstOrDefault(u => u.indent == indent);

    private static bool Built(GuildBuffUpgradeVO entry, long now)
    {
        var template = FindBuff(entry.indent);
        return template == null || entry.startTime.Ticks + template.unlockTime * TimeSpan.TicksPerMinute <= now;
    }

    private static bool IsBuilt(string indent)
    {
        var entry = Ledger(Guild, indent);
        return entry != null && Built(entry, DateTime.UtcNow.Ticks);
    }

    private static GuildBuffVO ActiveBuff(GuildBuffTemplate template, long expires) => new()
    {
        indent = template.indent,
        expiration = new DateTime(expires, DateTimeKind.Utc),
        duration = template.duration,
        level = template.level,
        category = template.category,
        data = (GuildBuffData[])template.data.Clone(),
    };

    // 168
    public static void UnlockBuff(Loopback.VirtualConnection conn, byte op, string indent)
    {
        var template = FindBuff(indent);
        string problem = BuffProblem(template)
            ?? (Guild.unlockedBuffs.Contains(indent) ? "This structure is already unlocked."
            : Guild.level < template.level ? $"Expand the guild to level {template.level} first."
            : template.level > 1 && !IsBuilt(Previous(template)) ? "Unlock the previous level of this structure first."
            : Guild.credits < Cost(template.unlockPrice, PackageType.Credits) ? "The guild vault does not hold enough credits."
            : Guild.experience < Cost(template.unlockPrice, PackageType.Experience) ? "The guild has not earned enough influence yet."
            : null);
        ChangeBuffs(conn, op, problem, () =>
        {
            Guild.unlockedBuffs.Add(indent);
            Guild.upgradingBuffs.Add(new GuildBuffUpgradeVO { indent = indent, startTime = DateTime.UtcNow });
        });
    }

    // 153: finishes building at once.
    public static void HurryBuff(Loopback.VirtualConnection conn, byte op, string indent)
    {
        var template = FindBuff(indent);
        var entry = Guild == null ? null : Ledger(Guild, indent);
        double sparks = template == null ? 0 : Cost(template.hurryPrice, PackageType.PremiumCredits);
        string problem = BuffProblem(template)
            ?? (entry == null ? "Unlock this structure first."
            : Built(entry, DateTime.UtcNow.Ticks) ? "This structure is not being upgraded."
            : Guild.premiumCredits < sparks ? "The guild vault does not hold enough sparks."
            : null);
        ChangeBuffs(conn, op, problem, () =>
        {
            entry.startTime = DateTime.UtcNow.AddMinutes(-template.unlockTime);
            Spend(0, sparks);
        });
    }

    // 167
    public static void ActivateBuff(Loopback.VirtualConnection conn, byte op, string indent)
    {
        var template = FindBuff(indent);
        double credits = template == null ? 0 : Cost(template.buyPrice, PackageType.Credits);
        if (Guild != null)
        {
            PruneExpiredBuffs();
        }
        string problem = BuffProblem(template)
            ?? (!IsBuilt(indent) ? "Unlock this structure first."
            : Guild.activeBuffs.Any(b => b.indent == indent) ? "This benefit is already active."
            : Guild.activeBuffs.Any(b => b.category == template.category) ? "Another benefit of this structure is active."
            : Guild.credits < credits ? "The guild vault does not hold enough credits."
            : null);
        ChangeBuffs(conn, op, problem, () =>
        {
            Guild.activeBuffs.Add(ActiveBuff(template, DateTime.UtcNow.AddMinutes(template.duration).Ticks));
            Spend(credits, 0);
        });
    }

    private static string BuffProblem(GuildBuffTemplate template) =>
        Guild == null ? "You are not in a guild."
        : !CanManage(Find(Guild, Me)) ? "Only the guild leader and officers can manage guild benefits."
        : template == null ? "Unknown guild benefit."
        : null;

    private static string Previous(GuildBuffTemplate template) => "GB_" + template.category + "_" + (template.level - 1);

    // A benefit change by me: no new roster revision, events for my client, and the new copy to the members here.
    private static void ChangeBuffs(Loopback.VirtualConnection conn, byte op, string problem, Action change)
    {
        if (problem != null)
        {
            GameServer.Fail(conn, op, problem);
            return;
        }
        var before = Clone(Guild);
        change();
        Recount();
        SocialStore.Save();
        ShowChanges(before, Guild, Me);
        SyncBuffs();
        Share(before, Me);
        GameServer.Reply(conn, op);
    }

    // Like a donation, a spend goes on my own tally and merges with the others'.
    private static void Spend(double credits, double sparks)
    {
        var mine = SocialStore.GuildBuffSpending.FirstOrDefault(s => s.name == Me);
        if (mine == null)
        {
            mine = new GuildDonatorVO { name = Me };
            SocialStore.GuildBuffSpending.Add(mine);
        }
        mine.credits += credits;
        mine.premiumCredits += sparks;
    }

    // What the vault paid for benefits: unlocks follow from the set (two officers unlocking the same one apart
    // pay once), the rest from the tallies.
    private static double BuffCredits() =>
        Guild.unlockedBuffs.Select(FindBuff).Where(t => t != null).Sum(t => Cost(t.unlockPrice, PackageType.Credits))
            + SocialStore.GuildBuffSpending.Sum(s => s.credits);

    private static double BuffSparks() => SocialStore.GuildBuffSpending.Sum(s => s.premiumCredits);

    // Known benefits only, one entry each (the client keys them by indent and throws on a repeat), a build entry
    // per unlocked benefit, no times in the future, and one active benefit per structure (the highest level),
    // since battles add up everything listed.
    private static void SanitizeBuffs(GuildVO guild)
    {
        long now = DateTime.UtcNow.Ticks;
        var ledger = guild.upgradingBuffs.Where(u => u != null && FindBuff(u.indent) != null).ToList();
        var unlocked = guild.unlockedBuffs.Concat(ledger.Select(u => u.indent)).Where(i => FindBuff(i) != null).Distinct().ToList();
        guild.unlockedBuffs = unlocked;
        guild.upgradingBuffs = unlocked.Select(indent => new GuildBuffUpgradeVO
        {
            indent = indent,
            // No entry: built.
            startTime = new DateTime(Math.Min(now, ledger.Where(u => u.indent == indent).Select(u => u.startTime.Ticks).DefaultIfEmpty(0L).Min()), DateTimeKind.Utc),
        }).ToList();
        guild.activeBuffs = guild.activeBuffs
            .Where(b => b != null && unlocked.Contains(b.indent) && b.expiration.Ticks > now)
            .Select(b => ActiveBuff(FindBuff(b.indent), Math.Min(b.expiration.Ticks, now + FindBuff(b.indent).duration * TimeSpan.TicksPerMinute)))
            .GroupBy(b => b.category)
            .Select(g => g.OrderByDescending(b => b.level).ThenByDescending(b => b.expiration.Ticks).First())
            .ToList();
    }

    private static void MergeBuffs(GuildVO incoming, List<GuildDonatorVO> spending)
    {
        Guild.unlockedBuffs.AddRange(incoming.unlockedBuffs);
        Guild.upgradingBuffs.AddRange(incoming.upgradingBuffs);
        Guild.activeBuffs.AddRange(incoming.activeBuffs);
        SanitizeBuffs(Guild);
        MergeSpending(spending);
    }

    private static void MergeSpending(List<GuildDonatorVO> spending)
    {
        foreach (var theirs in spending)
        {
            var mine = SocialStore.GuildBuffSpending.FirstOrDefault(s => s.name == theirs.name);
            if (mine == null)
            {
                SocialStore.GuildBuffSpending.Add(theirs);
                continue;
            }
            mine.credits = Math.Max(mine.credits, theirs.credits);
            mine.premiumCredits = Math.Max(mine.premiumCredits, theirs.premiumCredits);
        }
    }

    private static bool PruneExpiredBuffs()
    {
        long now = DateTime.UtcNow.Ticks;
        return Guild.activeBuffs.RemoveAll(b => b.expiration.Ticks <= now) > 0;
    }

    // The guild as the client should hold it now: building = not yet built, active = not yet expired.
    private static GuildVO ForClient(GuildVO guild)
    {
        var view = Clone(guild);
        SanitizeBuffs(view);
        long now = DateTime.UtcNow.Ticks;
        view.upgradingBuffs.RemoveAll(u => Built(u, now));
        return view;
    }

    // The client's own guild, once it shows my guild and knows the templates.
    private static ClientGuild ClientView()
    {
        var manager = GuildManager.Instance;
        var client = manager != null && manager.Initialized ? manager.GetGuild() : null;
        bool ready = client != null && Guild != null && client.guildId == Guild.guildId
            && client.unlockedBuffs != null && client.activeBuffs != null && client.upgradingBuffs != null
            && DataManager.Instance != null && DataManager.Instance.guildBuffsTemplateList != null;
        return ready ? client : null;
    }

    // Brings the client's benefit lists in line with my copy. The difference is worked out again on delivery,
    // against what the client holds by then, so no event repeats what it has (its lists throw on a repeat).
    private static void SyncBuffs()
    {
        try
        {
            var conn = Loopback.Get(PhotonNetwork.networkingPeer);
            bool queued = _pendingBuffSync != null && conn.Incoming.Contains(_pendingBuffSync);
            if (!queued && BuffEvents().Count > 0)
            {
                _pendingBuffSync = Loopback.Enqueue(conn, "guild benefits", DeliverBuffEvents);
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Guild benefits sync failed", ex);
        }
    }

    private static void DeliverBuffEvents(IPhotonPeerListener listener)
    {
        foreach (var data in BuffEvents())
        {
            try
            {
                RevivalLog.Info($"Guild benefits: event {data.Code} {Describe(data[218])}");
                listener.OnEvent(data);
            }
            catch (Exception ex)
            {
                RevivalLog.Error($"Game threw on guild benefits event {data.Code}", ex);
                // A difference the client cannot take would come back every tick.
                _nextBuffTick = Time.realtimeSinceStartup + 60f;
            }
        }
    }

    private static string Describe(object value) => value is string[] names ? string.Join(",", names) : value as string ?? "(benefit)";

    // 153 for what ended, 152 for unlocks and finished builds (true starts a build, false ends it; an already built
    // benefit needs both), 151 for what became active. A changed expiry is not resent: 153 would announce an expiry.
    private static List<EventData> BuffEvents()
    {
        var events = new List<EventData>();
        var client = ClientView();
        if (client == null) return events;
        var view = ForClient(Guild);
        var ended = client.activeBuffs.Keys.Where(k => !view.activeBuffs.Any(b => b.indent == k)).ToArray();
        if (ended.Length > 0)
        {
            events.Add(BuffEvent(153, new() { { 218, ended } }));
        }
        foreach (string indent in view.unlockedBuffs)
        {
            var building = Ledger(view, indent);
            bool shownBuilding = client.upgradingBuffs.TryGetValue(indent, out var shown) && shown != null;
            bool moved = building != null && shownBuilding && Math.Abs(building.startTime.Ticks - shown.startTime.Ticks) >= TimeSpan.TicksPerSecond;
            var entry = Ledger(Guild, indent);
            long start = entry != null ? entry.startTime.Ticks : 0L;
            if (shownBuilding && (building == null || moved))
            {
                events.Add(Unlocked(indent, false, start));
            }
            bool startBuilding = building != null ? !shownBuilding || moved : !shownBuilding && !client.unlockedBuffs.Contains(indent);
            if (startBuilding)
            {
                events.Add(Unlocked(indent, true, start));
                if (building == null)
                {
                    events.Add(Unlocked(indent, false, start));
                }
            }
        }
        foreach (var buff in view.activeBuffs.Where(b => !client.activeBuffs.ContainsKey(b.indent)))
        {
            events.Add(BuffEvent(151, new() { { 218, DataSerialization.ProtoSerializeObject(buff) } }));
        }
        return events;
    }

    private static EventData Unlocked(string indent, bool building, long startTicks) =>
        BuffEvent(152, new() { { 218, indent }, { 168, building }, { 167, startTicks } });

    private static EventData BuffEvent(byte code, Dictionary<byte, object> parameters) => new() { Code = code, Parameters = parameters };

    // Once a second from Session.Polled: expired benefits leave the copy, and the client hears of expiries
    // and finished builds, in or out of a session.
    public static void TickBuffs()
    {
        if (Time.realtimeSinceStartup < _nextBuffTick) return;
        _nextBuffTick = Time.realtimeSinceStartup + 1f;
        try
        {
            if (Guild == null || ClientView() == null) return;
            if (PruneExpiredBuffs())
            {
                SocialStore.Save();
            }
            SyncBuffs();
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Guild benefits upkeep failed", ex);
        }
    }

    // What my robot takes into battle: the benefits active in my copy.
    public static List<string> BattleBuffs() =>
        Guild == null ? [] : ActorBuffs(ForClient(Guild).activeBuffs.Select(b => b.indent));

    // A robot's list as the host accepts it: known benefits, the highest level per structure,
    // none when the server rules turn them off.
    public static List<string> ActorBuffs(IEnumerable<string> indents)
    {
        if (indents == null || (ServerRules.Active != null && !ServerRules.Active.GuildBuffs)) return [];
        return indents.Select(FindBuff).Where(t => t != null)
            .GroupBy(t => t.category)
            .Select(g => g.OrderByDescending(t => t.level).First().indent)
            .ToList();
    }
}
