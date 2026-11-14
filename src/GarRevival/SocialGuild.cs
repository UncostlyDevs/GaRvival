using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JunkWars.Common.Garage.Enums;
using JunkWars.Common.Guild.Enums;
using JunkWars.Common.Guild.Templates;
using JunkWars.Common.Guild.ValueObjects;
using JunkWars.Common.Packages;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Social;
using JunkWars.Common.Utils;
using JunkWars.Common.Utils.Enums;

namespace GarRevival
{
    // Guilds without a server to keep them: every member holds a copy (in
    // social.dat) and members bring each other up to date whenever they share
    // a session. A copy has two parts that are reconciled differently.
    //
    //   The roster (name, members and ranks, level, message of the day) is
    //   changed by the leader and officers. Each change gets a newer revision
    //   and the newest revision wins.
    //
    //   The tallies (what each member donated and how much experience each
    //   earned for the guild) only ever grow, and each member adds only to
    //   their own, so two copies are merged by taking the larger number per
    //   member. Nothing is lost when members play apart. Vault credits and
    //   guild experience are derived from the tallies and the level.
    //
    // A receiver compares its copy before and after and gives its client the
    // events GuildManager expects for the differences.
    internal static class SocialGuild
    {
        // ---- designed values; the originals were kept on the lost servers ----

        // Member limit at guild level 1..5.
        private static readonly int[] MemberLimit = { 10, 15, 20, 25, 30 };
        // To reach that level: credits taken from the guild vault...
        private static readonly float[] UpgradeCredits = { 0f, 50000f, 150000f, 400000f, 1000000f };
        // ...and guild experience that must have been earned (it is not spent).
        private static readonly int[] UpgradeExperience = { 0, 5000, 15000, 40000, 100000 };
        // Each experience point a member earns in battle is one for the guild.
        private const int GuildExperiencePerPoint = 1;
        private const int MaxMessageOfTheDay = 200;
        private const int MaxLatestDonations = 100; // GuildManager.latestDonationsMaxCount
        private const int MaxRememberedDisbands = 50;

        // The checks the client's create screen makes.
        private static readonly Regex NamePattern = new Regex("^([a-zA-Z]+[a-zA-Z\\s_\\-0-9]*[a-zA-Z0-9]+)$");
        private static readonly Regex TagPattern = new Regex("^([a-zA-Z]+[a-zA-Z_\\-0-9]*[a-zA-Z0-9]+)$");

        // Why a join request or an invitation was turned down.
        private const byte RefusedGone = 1;
        private const byte RefusedInvalid = 2;
        private const byte RefusedFull = 3;
        private const byte RefusedAlreadyInGuild = 4;

        private static int _seenExperience = -1;

        // Replaces DataFactory.GuildUpgrades(): one entry per guild level, keyed by level.
        public static Dictionary<int, GuildUpgradeTemplate> Upgrades()
        {
            Dictionary<int, GuildUpgradeTemplate> upgrades = new Dictionary<int, GuildUpgradeTemplate>();
            for (int level = 1; level <= MemberLimit.Length; level++)
            {
                upgrades[level] = new GuildUpgradeTemplate
                {
                    level = level,
                    maxMembers = MemberLimit[level - 1],
                    price = level == 1 ? new PricePackageItem[0] : new[]
                    {
                        new PricePackageItem { type = PackageType.Credits, quantity = UpgradeCredits[level - 1] },
                        new PricePackageItem { type = PackageType.Experience, quantity = UpgradeExperience[level - 1] },
                    },
                };
            }
            return upgrades;
        }

        private static GuildVO Guild
        {
            get { return SocialStore.Guild; }
        }

        private static string Me
        {
            get { return SocialOps.Me; }
        }

        private static GuildMemberVO Find(GuildVO guild, string name)
        {
            return guild == null ? null : guild.members.FirstOrDefault(m => m.name == name);
        }

        private static bool CanManage(GuildMemberVO member)
        {
            return member != null && (member.role == GuildMemberRole.Leader || member.role == GuildMemberRole.Officer);
        }

        // ---- operations ----

        // 179: asked once, while the garage loads.
        public static void Get(Loopback.VirtualConnection conn, byte op, bool withStatuses)
        {
            PlayerVO player = PlayerState.Current;
            _seenExperience = player.experience;
            if (Guild != null && Find(Guild, Me) == null)
            {
                SocialStore.Guild = null;
            }
            if (Guild == null)
            {
                // A guild id left in the profile is kept: if social.dat was
                // lost, a guild mate's copy brings the guild back.
                GameServer.Reply(conn, op);
                return;
            }
            // My own entry is mine to keep current; others take it from my card.
            GuildMemberVO me = Find(Guild, Me);
            me.level = player.level;
            me.portrait = player.currentPortraitIndex;
            Recount();
            SocialStore.Save();
            if (player.guildId != Guild.guildId)
            {
                player.guildId = Guild.guildId;
                PlayerState.Save();
            }
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObjectCompressed(Guild) } });
            if (withStatuses)
            {
                GameServer.Event(conn, 160, new Dictionary<byte, object> { { 218, OnlineNames() } });
            }
        }

        // 178. The error keys are the ones the client's own create screen uses.
        public static void Create(Loopback.VirtualConnection conn, byte op, string name, string tag)
        {
            name = name.Trim();
            tag = tag.Trim();
            PlayerVO player = PlayerState.Current;
            float cost = CommonConstants.GuildCreationCost;
            string problem =
                Guild != null ? "SM_0102"
                : name.Length < 3 ? "SM_0097"
                : name.Length > 24 ? "SM_0098"
                : !NamePattern.IsMatch(name) ? "SM_0096"
                : tag.Length < 2 ? "SM_0099"
                : tag.Length > 5 ? "SM_0100"
                : !TagPattern.IsMatch(tag) ? "SM_0127"
                : player.credits < cost ? "Not enough credits"
                : null;
            if (problem != null)
            {
                GameServer.Fail(conn, op, problem);
                return;
            }
            GameServer.ChangeCredits(conn, -cost, ItemAddedReason.Buy);
            GuildVO guild = new GuildVO
            {
                guildId = Guid.NewGuid().ToString("N"),
                name = name,
                abbreviation = tag,
                level = 1,
                maxPlayers = MemberLimit[0],
            };
            guild.members.Add(new GuildMemberVO { userId = Me, name = Me, level = player.level, portrait = player.currentPortraitIndex, role = GuildMemberRole.Leader });
            SocialStore.Guild = guild;
            SocialStore.GuildRevision = DateTime.UtcNow.Ticks;
            SocialStore.GuildExperience.Clear();
            SocialStore.PendingGuildJoin = string.Empty;
            SocialStore.Save();
            player.guildId = guild.guildId;
            PlayerState.Save();
            RevivalLog.Info("Guild created: " + name + " [" + tag + "]");
            GameServer.Reply(conn, op, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObjectCompressed(guild) } });
            SocialOps.AnnounceCard();
        }

        // 177
        public static void Invite(Loopback.VirtualConnection conn, byte op, string name)
        {
            string problem =
                Guild == null ? "You are not in a guild."
                : !CanManage(Find(Guild, Me)) ? "Only the guild leader and officers can invite players."
                : name.Length == 0 || name == Me ? "You cannot invite yourself."
                : Find(Guild, name) != null ? Session.DisplayName(name) + " is already in your guild."
                : Guild.members.Count >= Guild.maxPlayers ? "Your guild is full."
                : !Session.InSession(name) ? SocialOps.NotHere(name)
                : SocialOps.CardOf(name).Guild.Length > 0 ? Session.DisplayName(name) + " is already in a guild."
                : null;
            if (problem != null)
            {
                GameServer.Fail(conn, op, problem);
                return;
            }
            if (!SocialStore.SentGuildInvites.Contains(name))
            {
                SocialStore.SentGuildInvites.Add(name);
                SocialStore.Save();
            }
            string id = Guild.guildId;
            string guildName = Guild.name;
            Session.SendSocial(name, SocialOps.Pack(SocialOps.GuildInvite, w =>
            {
                w.Write(id);
                w.Write(guildName);
            }));
            GameServer.Reply(conn, op);
        }

        // 176: my answer to a guild invitation in the message box. Joining is
        // finished when the inviter sends the guild (see OnState).
        public static void Respond(Loopback.VirtualConnection conn, byte op, string inviter, bool accept)
        {
            SocialMessageData invitation = SocialStore.Inbox.FirstOrDefault(m => m.sender == inviter && m.type == EventMessageType.GuildInvitation);
            if (invitation == null)
            {
                SocialFriends.PushInbox(); // the client's list was stale
                GameServer.Reply(conn, op);
                return;
            }
            if (accept)
            {
                // "name;id", as the client's message box splits it.
                string[] parts = (invitation.message ?? string.Empty).Split(';');
                string id = parts.Length > 1 ? parts[1] : string.Empty;
                if (Guild != null)
                {
                    GameServer.Fail(conn, op, "SM_0102");
                    return;
                }
                if (!Session.SendSocial(inviter, SocialOps.Pack(SocialOps.GuildJoin, w => w.Write(id))))
                {
                    GameServer.Fail(conn, op, SocialOps.NotHere(inviter) + " The invitation stays in your message box.");
                    return;
                }
                SocialStore.PendingGuildJoin = id;
            }
            SocialStore.Inbox.Remove(invitation);
            SocialStore.Save();
            SocialFriends.PushInbox();
            GameServer.Reply(conn, op);
        }

        // 175
        public static void Leave(Loopback.VirtualConnection conn, byte op)
        {
            GuildMemberVO me = Find(Guild, Me);
            if (me == null)
            {
                GameServer.Fail(conn, op, "You are not in a guild.");
                return;
            }
            if (me.role == GuildMemberRole.Leader)
            {
                GameServer.Fail(conn, op, "The leader cannot leave. Make an officer the leader first, or disband the guild.");
                return;
            }
            GuildVO before = Clone(Guild);
            Guild.members.Remove(me);
            SocialStore.GuildRevision = NextRevision();
            // Kept for members who are not here now.
            SocialStore.FarewellGuildId = Guild.guildId;
            SocialStore.FarewellRevision = SocialStore.GuildRevision;
            SocialStore.FarewellState = Copy();
            Share(before, Me);
            SocialOps.Event(163, new Dictionary<byte, object>());
            Forget();
            GameServer.Reply(conn, op);
        }

        // 174
        public static void Disband(Loopback.VirtualConnection conn, byte op)
        {
            GuildMemberVO me = Find(Guild, Me);
            if (me == null || me.role != GuildMemberRole.Leader)
            {
                GameServer.Fail(conn, op, "Only the guild leader can disband the guild.");
                return;
            }
            string id = Guild.guildId;
            byte[] gone = SocialOps.Pack(SocialOps.GuildGone, w => w.Write(id));
            foreach (string name in MembersHere())
            {
                Session.SendSocial(name, gone);
            }
            RememberDisbanded(id);
            SocialOps.Event(162, new Dictionary<byte, object>());
            Forget();
            GameServer.Reply(conn, op);
        }

        // 173
        public static void Kick(Loopback.VirtualConnection conn, byte op, string name)
        {
            GuildMemberVO me = Find(Guild, Me);
            GuildMemberVO target = Find(Guild, name);
            bool allowed = me != null && target != null && target != me
                && (me.role == GuildMemberRole.Leader || (me.role == GuildMemberRole.Officer && target.role == GuildMemberRole.Member));
            if (!allowed)
            {
                GameServer.Fail(conn, op, "You are not allowed to remove that member.");
                return;
            }
            ChangeRoster(guild => guild.members.Remove(Find(guild, name)));
            GameServer.Reply(conn, op);
        }

        // 172: the leader promotes and demotes; promoting an officer hands
        // over the leadership. Officers can promote members.
        public static void ChangeRole(Loopback.VirtualConnection conn, byte op, string name, byte role)
        {
            GuildMemberVO me = Find(Guild, Me);
            GuildMemberVO target = Find(Guild, name);
            GuildMemberRole wanted = (GuildMemberRole)role;
            bool allowed = me != null && target != null && target != me && role <= (byte)GuildMemberRole.Member && target.role != wanted
                && (me.role == GuildMemberRole.Leader
                    || (me.role == GuildMemberRole.Officer && target.role == GuildMemberRole.Member && wanted == GuildMemberRole.Officer));
            if (!allowed)
            {
                GameServer.Fail(conn, op, "You are not allowed to change that member's rank.");
                return;
            }
            ChangeRoster(guild =>
            {
                Find(guild, name).role = wanted;
                if (wanted == GuildMemberRole.Leader)
                {
                    Find(guild, Me).role = GuildMemberRole.Officer;
                }
            });
            GameServer.Reply(conn, op);
        }

        // 171: from my own credits and sparks into the guild vault.
        public static void Deposit(Loopback.VirtualConnection conn, byte op, float credits, float premium)
        {
            PlayerVO player = PlayerState.Current;
            string problem =
                Guild == null ? "You are not in a guild."
                : float.IsNaN(credits) || float.IsNaN(premium) || credits < 0f || premium < 0f || credits + premium <= 0f ? "Enter an amount to donate."
                : player.credits < credits || player.premiumCredits < premium ? "Not enough credits"
                : null;
            if (problem != null)
            {
                GameServer.Fail(conn, op, problem);
                return;
            }
            player.credits -= credits;
            player.premiumCredits -= premium;
            PlayerState.Save();
            GameServer.Event(conn, 220, new Dictionary<byte, object>
            {
                { 213, player.credits },
                { 214, -credits },
                { 234, player.premiumCredits },
                { 218, -premium },
                { 168, (byte)ItemAddedReason.Buy },
            });
            AddToTallies(credits, premium, 0);
            GameServer.Reply(conn, op);
        }

        // 170: the next level raises the member limit.
        public static void Upgrade(Loopback.VirtualConnection conn, byte op)
        {
            GuildMemberVO me = Find(Guild, Me);
            int next = Guild == null ? 0 : Guild.level + 1;
            string problem =
                me == null ? "You are not in a guild."
                : me.role != GuildMemberRole.Leader ? "Only the guild leader can expand the guild."
                : next > MemberLimit.Length ? "The guild is already at its highest level."
                : Guild.credits < UpgradeCredits[next - 1] ? "The guild vault does not hold enough credits."
                : Guild.experience < UpgradeExperience[next - 1] ? "The guild has not earned enough experience yet."
                : null;
            if (problem != null)
            {
                GameServer.Fail(conn, op, problem);
                return;
            }
            // The price leaves the vault through Recount: it follows from the level.
            ChangeRoster(guild =>
            {
                guild.level = next;
                guild.maxPlayers = MemberLimit[next - 1];
            });
            GameServer.Reply(conn, op);
        }

        // 166
        public static void SetMessageOfTheDay(Loopback.VirtualConnection conn, byte op, string message)
        {
            if (!CanManage(Find(Guild, Me)))
            {
                GameServer.Fail(conn, op, "Only the guild leader and officers can change the message of the day.");
                return;
            }
            ChangeRoster(guild => guild.messageOfTheDay = SocialOps.Clip(message, MaxMessageOfTheDay));
            GameServer.Reply(conn, op);
        }

        // ---- presence ----

        // The other guild members in my session.
        public static List<string> MembersHere()
        {
            return Guild == null ? new List<string>() : Guild.members.Select(m => m.name).Where(n => n != Me && Session.InSession(n)).ToList();
        }

        public static bool IsMember(string name, string guildId)
        {
            return Guild != null && Guild.guildId == guildId && Find(Guild, name) != null;
        }

        private static string[] OnlineNames()
        {
            return Guild.members.Select(m => m.name).Where(n => n == Me || Session.InSession(n)).ToArray();
        }

        public static void OnAppeared(string name)
        {
            ShowOnline(name, true);
        }

        public static void OnGone(string name)
        {
            ShowOnline(name, false);
        }

        private static void ShowOnline(string name, bool online)
        {
            if (Find(Guild, name) != null)
            {
                SocialOps.Event(159, new Dictionary<byte, object> { { 218, name }, { 240, online } });
            }
        }

        // A member's level and portrait come from the member, with every message.
        public static void OnCard(string sender, SocialOps.Card card)
        {
            GuildMemberVO member = Find(Guild, sender);
            if (member != null && (member.level != card.Level || member.portrait != card.Portrait))
            {
                member.level = card.Level;
                member.portrait = card.Portrait;
                SocialStore.Save();
                SocialOps.Event(158, new Dictionary<byte, object> { { 215, sender }, { 169, card.Level }, { 223, card.Portrait } });
            }
        }

        // ---- keeping the copies in step ----

        // Part of every Hello: which guild I hold a copy of, and its roster revision.
        public static void WriteHello(BinaryWriter w)
        {
            w.Write(Guild != null ? Guild.guildId : PlayerState.Current.guildId ?? string.Empty);
            w.Write(Guild != null ? SocialStore.GuildRevision : 0L);
        }

        // Two members met: each sends the other their copy.
        public static void OnHello(string sender, string guildId, long revision)
        {
            if (guildId.Length == 0)
            {
                return;
            }
            if (SocialStore.DisbandedGuilds.Contains(guildId))
            {
                Session.SendSocial(sender, SocialOps.Pack(SocialOps.GuildGone, w => w.Write(guildId)));
            }
            else if (Guild != null && Guild.guildId == guildId)
            {
                Session.SendSocial(sender, StateMessage(guildId, string.Empty, Copy()));
            }
            else if (SocialStore.FarewellGuildId == guildId && revision < SocialStore.FarewellRevision)
            {
                Session.SendSocial(sender, StateMessage(guildId, Me, SocialStore.FarewellState));
            }
        }

        public static void OnMessage(string sender, byte kind, BinaryReader r)
        {
            string guildId = r.ReadString();
            switch (kind)
            {
                case SocialOps.GuildInvite:
                    OnInvite(sender, guildId, SocialOps.Clip(r.ReadString(), 24));
                    break;
                case SocialOps.GuildJoin:
                    OnJoin(sender, guildId);
                    break;
                case SocialOps.GuildState:
                    string actor = SocialOps.Clip(r.ReadString(), 64);
                    int length = r.ReadInt32();
                    if (length > 0 && length < 4 * 1024 * 1024)
                    {
                        OnState(sender, guildId, actor, r.ReadBytes(length));
                    }
                    break;
                case SocialOps.GuildGone:
                    if (IsMember(sender, guildId))
                    {
                        RememberDisbanded(guildId);
                        SocialOps.Event(162, new Dictionary<byte, object>());
                        Forget();
                    }
                    else if (Guild == null)
                    {
                        DropProfileGuild(guildId);
                    }
                    break;
                case SocialOps.GuildRefused:
                    OnRefused(sender, guildId, r.ReadByte());
                    break;
            }
        }

        private static void OnInvite(string sender, string guildId, string guildName)
        {
            if (Guild != null)
            {
                Refuse(sender, guildId, RefusedAlreadyInGuild);
                return;
            }
            SocialFriends.AddToInbox(sender, EventMessageType.GuildInvitation, guildName.Replace(";", string.Empty) + ";" + guildId.Replace(";", string.Empty));
        }

        // Someone I invited said yes: add them and send everyone the new roster.
        private static void OnJoin(string sender, string guildId)
        {
            if (Guild == null || Guild.guildId != guildId)
            {
                Refuse(sender, guildId, RefusedGone);
            }
            else if (Find(Guild, sender) != null)
            {
                Session.SendSocial(sender, StateMessage(guildId, Me, Copy()));
            }
            else if (!SocialStore.SentGuildInvites.Remove(sender))
            {
                Refuse(sender, guildId, RefusedInvalid);
            }
            else if (Guild.members.Count >= Guild.maxPlayers)
            {
                SocialStore.Save();
                Refuse(sender, guildId, RefusedFull);
            }
            else
            {
                SocialOps.Card card = SocialOps.CardOf(sender);
                ChangeRoster(guild => guild.members.Add(new GuildMemberVO { userId = sender, name = sender, level = card.Level, portrait = card.Portrait, role = GuildMemberRole.Member }));
            }
        }

        private static void Refuse(string name, string guildId, byte reason)
        {
            Session.SendSocial(name, SocialOps.Pack(SocialOps.GuildRefused, w =>
            {
                w.Write(guildId);
                w.Write(reason);
            }));
        }

        private static void OnRefused(string sender, string guildId, byte reason)
        {
            string who = Session.DisplayName(sender);
            if (reason == RefusedAlreadyInGuild)
            {
                // The answer to an invitation of mine.
                if (SocialStore.SentGuildInvites.Remove(sender))
                {
                    SocialStore.Save();
                    GameServer.Notice(who + " is already in a guild.");
                }
                return;
            }
            if (SocialStore.PendingGuildJoin.Length == 0 || SocialStore.PendingGuildJoin != guildId)
            {
                return;
            }
            SocialStore.PendingGuildJoin = string.Empty;
            SocialStore.Save();
            GameServer.Notice(reason == RefusedFull ? "You could not join the guild: it is full."
                : reason == RefusedGone ? "You could not join the guild: it no longer exists."
                : "You could not join the guild: " + who + "'s invitation is no longer valid.");
        }

        // A copy of a guild arrived.
        private static void OnState(string sender, string guildId, string actor, byte[] copy)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(copy));
            long revision = r.ReadInt64();
            GuildVO incoming = SocialStore.GuildFromBytes(r.ReadBytes(r.ReadInt32()));
            Dictionary<string, int> earned = new Dictionary<string, int>();
            for (int i = Math.Min(r.ReadInt32(), 1000); i > 0; i--)
            {
                string name = r.ReadString();
                earned[name] = Math.Max(0, r.ReadInt32());
            }
            if (incoming == null || incoming.guildId != guildId || !Sound(incoming))
            {
                return;
            }
            if (Guild != null && Guild.guildId == guildId)
            {
                bool newer = revision > SocialStore.GuildRevision;
                // From a member I know, or a newer roster from someone on it.
                if (Find(Guild, sender) == null && !(newer && Find(incoming, sender) != null))
                {
                    return;
                }
                if (newer && Find(incoming, Me) == null)
                {
                    // Removed from the guild, now or while I was away.
                    SocialOps.Event(163, new Dictionary<byte, object>());
                    Forget();
                    return;
                }
                GuildVO before = Clone(Guild);
                if (newer)
                {
                    TakeRoster(incoming, revision);
                }
                MergeTallies(incoming, earned);
                Recount();
                SocialStore.Save();
                ShowChanges(before, Guild, actor);
            }
            else if (Guild == null && Find(incoming, Me) != null && Find(incoming, sender) != null && guildId.Length > 0
                && (SocialStore.PendingGuildJoin == guildId || PlayerState.Current.guildId == guildId))
            {
                // I joined (or got my guild back after losing social.dat).
                SocialStore.Guild = incoming;
                SocialStore.GuildRevision = revision;
                SocialStore.GuildExperience.Clear();
                MergeTallies(incoming, earned);
                Recount();
                SocialStore.PendingGuildJoin = string.Empty;
                SocialStore.Save();
                PlayerState.Current.guildId = guildId;
                PlayerState.Save();
                RevivalLog.Info("Guild joined: " + incoming.name);
                SocialOps.Event(166, new Dictionary<byte, object>
                {
                    { 218, DataSerialization.ProtoSerializeObjectCompressed(incoming) },
                    { 168, OnlineNames() },
                });
                SocialOps.AnnounceCard();
            }
            else if (Guild == null && Find(incoming, Me) == null && Find(incoming, sender) != null)
            {
                DropProfileGuild(guildId);
            }
        }

        // The profile still named a guild that is gone or no longer has me
        // (only possible after social.dat was lost).
        private static void DropProfileGuild(string guildId)
        {
            if (guildId.Length > 0 && PlayerState.Current.guildId == guildId)
            {
                PlayerState.Current.guildId = string.Empty;
                PlayerState.Save();
            }
        }

        // The client keys its lists by name and would throw on a repeat.
        private static bool Sound(GuildVO guild)
        {
            if (string.IsNullOrEmpty(guild.name) || guild.name.Length > 24 || string.IsNullOrEmpty(guild.abbreviation) || guild.abbreviation.Length > 5)
            {
                return false;
            }
            if (guild.members.Count == 0 || guild.members.Count > 500 || guild.members.Any(m => string.IsNullOrEmpty(m.name))
                || guild.members.Select(m => m.name).Distinct().Count() != guild.members.Count
                || guild.donations.Any(d => string.IsNullOrEmpty(d.name))
                || guild.donations.Select(d => d.name).Distinct().Count() != guild.donations.Count
                || guild.level < 1 || guild.level > MemberLimit.Length)
            {
                return false;
            }
            // There are no buff templates, so nothing can be unlocked or active.
            guild.unlockedBuffs.Clear();
            guild.activeBuffs.Clear();
            guild.upgradingBuffs.Clear();
            guild.messageOfTheDay = SocialOps.Clip(guild.messageOfTheDay, MaxMessageOfTheDay);
            guild.maxPlayers = MemberLimit[guild.level - 1];
            return true;
        }

        // The roster part of a newer copy replaces mine. What I know better
        // stays: my own entry, and levels, which only rise.
        private static void TakeRoster(GuildVO incoming, long revision)
        {
            PlayerVO player = PlayerState.Current;
            foreach (GuildMemberVO member in incoming.members)
            {
                GuildMemberVO was = Find(Guild, member.name);
                if (member.name == Me)
                {
                    member.level = player.level;
                    member.portrait = player.currentPortraitIndex;
                }
                else if (was != null && was.level > member.level)
                {
                    member.level = was.level;
                    member.portrait = was.portrait;
                }
            }
            Guild.name = incoming.name;
            Guild.abbreviation = incoming.abbreviation;
            Guild.icon = incoming.icon;
            Guild.level = incoming.level;
            Guild.maxPlayers = incoming.maxPlayers;
            Guild.messageOfTheDay = incoming.messageOfTheDay;
            Guild.members = incoming.members;
            SocialStore.GuildRevision = revision;
        }

        // Per member, the larger number is the more recent one.
        private static void MergeTallies(GuildVO incoming, Dictionary<string, int> earned)
        {
            foreach (GuildDonatorVO theirs in incoming.donations)
            {
                GuildDonatorVO mine = Guild.donations.FirstOrDefault(d => d.name == theirs.name);
                if (mine == null)
                {
                    Guild.donations.Add(theirs);
                }
                else if (mine != theirs)
                {
                    mine.credits = Math.Max(mine.credits, theirs.credits);
                    mine.premiumCredits = Math.Max(mine.premiumCredits, theirs.premiumCredits);
                }
            }
            foreach (GuildDonationVO donation in incoming.latestDonations)
            {
                if (!Guild.latestDonations.Any(d => Same(d, donation)))
                {
                    Guild.latestDonations.Add(donation);
                }
            }
            TrimLatestDonations();
            foreach (KeyValuePair<string, int> entry in earned)
            {
                int mine;
                SocialStore.GuildExperience.TryGetValue(entry.Key, out mine);
                SocialStore.GuildExperience[entry.Key] = Math.Max(mine, entry.Value);
            }
        }

        // Newest first, as the client keeps them.
        private static void TrimLatestDonations()
        {
            Guild.latestDonations.Sort((a, b) => b.date.CompareTo(a.date));
            if (Guild.latestDonations.Count > MaxLatestDonations)
            {
                Guild.latestDonations.RemoveRange(MaxLatestDonations, Guild.latestDonations.Count - MaxLatestDonations);
            }
        }

        // Vault and experience follow from the tallies and the level.
        private static void Recount()
        {
            double spent = 0;
            for (int level = 2; level <= Guild.level && level <= UpgradeCredits.Length; level++)
            {
                spent += UpgradeCredits[level - 1];
            }
            Guild.credits = Math.Max(0, Guild.donations.Sum(d => d.credits) - spent);
            Guild.premiumCredits = Guild.donations.Sum(d => d.premiumCredits);
            Guild.experience = (int)Math.Min(int.MaxValue, SocialStore.GuildExperience.Values.Sum(v => (long)v));
        }

        // A change to the roster by me: new revision, events for my client,
        // and the new copy to the members who are here.
        private static void ChangeRoster(Action<GuildVO> change)
        {
            GuildVO before = Clone(Guild);
            change(Guild);
            SocialStore.GuildRevision = NextRevision();
            Recount();
            SocialStore.Save();
            ShowChanges(before, Guild, Me);
            Share(before, Me);
        }

        private static long NextRevision()
        {
            return Math.Max(SocialStore.GuildRevision + 1, DateTime.UtcNow.Ticks);
        }

        // To everyone here who is a member now or was one a moment ago (a
        // removed member learns of it from the copy they are missing from).
        private static void Share(GuildVO before, string actor)
        {
            byte[] message = StateMessage(Guild.guildId, actor, Copy());
            foreach (string name in new List<string>(Session.Roster))
            {
                if (name != Me && (Find(Guild, name) != null || Find(before, name) != null))
                {
                    Session.SendSocial(name, message);
                }
            }
        }

        // My copy as it travels and as it is kept for a farewell.
        private static byte[] Copy()
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter w = new BinaryWriter(stream);
            byte[] guild = SocialStore.GuildToBytes(Guild);
            w.Write(SocialStore.GuildRevision);
            w.Write(guild.Length);
            w.Write(guild);
            w.Write(SocialStore.GuildExperience.Count);
            foreach (KeyValuePair<string, int> entry in SocialStore.GuildExperience)
            {
                w.Write(entry.Key);
                w.Write(entry.Value);
            }
            w.Flush();
            return stream.ToArray();
        }

        private static byte[] StateMessage(string guildId, string actor, byte[] copy)
        {
            return SocialOps.Pack(SocialOps.GuildState, w =>
            {
                w.Write(guildId);
                w.Write(actor);
                w.Write(copy.Length);
                w.Write(copy);
            });
        }

        private static GuildVO Clone(GuildVO guild)
        {
            return SocialStore.GuildFromBytes(SocialStore.GuildToBytes(guild));
        }

        // I am no longer in a guild.
        private static void Forget()
        {
            SocialStore.Guild = null;
            SocialStore.GuildRevision = 0;
            SocialStore.GuildExperience.Clear();
            SocialStore.Save();
            PlayerState.Current.guildId = string.Empty;
            PlayerState.Save();
            SocialOps.AnnounceCard();
        }

        // Members who meet me later with a copy of a disbanded guild are told.
        private static void RememberDisbanded(string guildId)
        {
            if (!SocialStore.DisbandedGuilds.Contains(guildId))
            {
                SocialStore.DisbandedGuilds.Add(guildId);
                if (SocialStore.DisbandedGuilds.Count > MaxRememberedDisbands)
                {
                    SocialStore.DisbandedGuilds.RemoveAt(0);
                }
            }
        }

        // The events GuildManager expects for the difference between two copies.
        private static void ShowChanges(GuildVO before, GuildVO now, string actor)
        {
            foreach (GuildMemberVO member in now.members)
            {
                GuildMemberVO was = Find(before, member.name);
                if (was == null)
                {
                    SocialOps.Event(165, new Dictionary<byte, object> { { 218, DataSerialization.ProtoSerializeObjectCompressed(member) } });
                    if (!Session.InSession(member.name))
                    {
                        // The client assumes a new member is online.
                        SocialOps.Event(159, new Dictionary<byte, object> { { 218, member.name }, { 240, false } });
                    }
                }
                else if (was.level != member.level || was.portrait != member.portrait)
                {
                    SocialOps.Event(158, new Dictionary<byte, object> { { 215, member.name }, { 169, member.level }, { 223, member.portrait } });
                }
            }
            foreach (GuildMemberVO was in before.members)
            {
                if (Find(now, was.name) == null)
                {
                    SocialOps.Event(164, new Dictionary<byte, object> { { 215, was.name } });
                }
            }
            GuildMemberVO[] moved = now.members.Where(m => Find(before, m.name) != null && Find(before, m.name).role != m.role).ToArray();
            if (moved.Length > 0)
            {
                SocialOps.Event(161, new Dictionary<byte, object>
                {
                    { 237, moved.Select(m => m.name).ToArray() },
                    { 240, moved.Select(m => (byte)m.role).ToArray() },
                });
            }
            if ((before.messageOfTheDay ?? string.Empty) != (now.messageOfTheDay ?? string.Empty))
            {
                // The chat line names who changed it; a catch-up copy does not say, so name the leader.
                GuildMemberVO leader = now.members.FirstOrDefault(m => m.role == GuildMemberRole.Leader) ?? now.members[0];
                SocialOps.Event(150, new Dictionary<byte, object> { { 217, Find(now, actor) != null ? actor : leader.name }, { 240, now.messageOfTheDay ?? string.Empty } });
            }
            // Each new donation is its own event; the client adds it to its lists.
            GuildDonationVO[] donations = now.latestDonations.Where(d => !before.latestDonations.Any(o => Same(o, d))).Reverse().ToArray();
            foreach (GuildDonationVO donation in donations)
            {
                SocialOps.Event(157, new Dictionary<byte, object>
                {
                    { 213, now.credits },
                    { 234, now.premiumCredits },
                    { 218, DataSerialization.ProtoSerializeObject(donation) },
                });
            }
            if (donations.Length == 0 && (before.credits != now.credits || before.premiumCredits != now.premiumCredits))
            {
                SocialOps.Event(157, new Dictionary<byte, object> { { 213, now.credits }, { 234, now.premiumCredits } });
            }
            if (before.experience != now.experience)
            {
                SocialOps.Event(156, new Dictionary<byte, object> { { 170, now.experience } });
            }
            if (before.level != now.level || before.maxPlayers != now.maxPlayers)
            {
                SocialOps.Event(155, new Dictionary<byte, object> { { 169, now.level }, { 218, now.maxPlayers } });
            }
        }

        private static bool Same(GuildDonationVO a, GuildDonationVO b)
        {
            return a.name == b.name && a.date.Ticks == b.date.Ticks && a.credits == b.credits && a.premiumCredits == b.premiumCredits;
        }

        // ---- the vault and guild experience ----

        // Once a frame (Session.Polled): experience I earned since the last
        // look also counts for my guild. Armed by op 179, when the profile is loaded.
        public static void WatchExperience()
        {
            if (_seenExperience < 0)
            {
                return;
            }
            int now = PlayerState.Current.experience;
            int gained = now - _seenExperience;
            _seenExperience = now;
            if (gained > 0 && Guild != null)
            {
                AddToTallies(0, 0, gained * GuildExperiencePerPoint);
            }
        }

        // What I gave or earned goes on my own tally: no new roster revision,
        // so it merges with whatever the others did meanwhile.
        private static void AddToTallies(double credits, double premium, int experience)
        {
            GuildVO before = Clone(Guild);
            if (credits > 0 || premium > 0)
            {
                GuildDonatorVO total = Guild.donations.FirstOrDefault(d => d.name == Me);
                if (total == null)
                {
                    total = new GuildDonatorVO { name = Me };
                    Guild.donations.Add(total);
                }
                total.credits += credits;
                total.premiumCredits += premium;
                Guild.latestDonations.Add(new GuildDonationVO { name = Me, date = DateTime.UtcNow, credits = credits, premiumCredits = premium });
                TrimLatestDonations();
            }
            if (experience > 0)
            {
                int mine;
                SocialStore.GuildExperience.TryGetValue(Me, out mine);
                SocialStore.GuildExperience[Me] = mine + experience;
            }
            Recount();
            SocialStore.Save();
            ShowChanges(before, Guild, Me);
            Share(before, Me);
        }
    }
}
