using System;
using System.Collections.Generic;
using System.IO;
using JunkWars.Client;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Utils.Enums;

namespace GarRevival;

// Social operations without a central server: each player's own server answers them, and what concerns
// another player travels through the session (Session.SendSocial). "Online" means "in my session".
// SocialFriends: friends and message box; SocialParty: squads; SocialGuild: guilds; SocialStore: saved state.
internal static class SocialOps
{
    // The only return code that makes the client report a failed chat line.
    private const short ChatFailed = 32751;
    private const byte UserGroup = 2; // AccountGroupType.User
    private const int MaxChatLength = 500;

    // Message kinds between players; every message starts with the sender's card.
    public const byte Hello = 0;
    public const byte Whisper = 1;
    public const byte PartyChat = 2;
    public const byte GuildChat = 3;
    public const byte FriendInvite = 10;
    public const byte FriendAccept = 11;
    public const byte FriendRemove = 12;
    public const byte PartyInvite = 20;      // leader -> invitee
    public const byte PartyReply = 21;       // invitee -> leader
    public const byte PartyState = 22;       // member -> leader
    public const byte PartyLeave = 23;       // member -> leader
    public const byte PartyMemberAdded = 24; // leader -> members
    public const byte PartyMemberLeft = 25;
    public const byte PartyMemberState = 26;
    public const byte PartyDisband = 27;
    public const byte GuildInvite = 30;
    public const byte GuildJoin = 31;
    public const byte GuildState = 32;
    public const byte GuildGone = 33;
    public const byte GuildRefused = 34;
    public const byte LadderCard = 40;       // my ladder standing (LadderTable)

    // What others need to show me in their lists.
    internal sealed class Card
    {
        public int Level = 1;
        public int Portrait;
        public float ItemLevel;
        public string Guild = string.Empty; // abbreviation
    }

    private static readonly Dictionary<string, Card> Cards = new();
    private static bool _attached;

    public static string Me => PlayerState.Current.fullName;

    public static bool Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        if (op is not (211 or 153 or (>= 182 and <= 191) or (>= 166 and <= 179 and not 169))) return false;
        try
        {
            Attach();
            return Dispatch(conn, op, request ?? new());
        }
        catch (Exception ex)
        {
            RevivalLog.Error($"Social op {op} failed", ex);
            switch (op)
            {
                case 183: // still has to release the client's input blocker
                    GameServer.Event(conn, 169, new() { { 218, new string[0] }, { 240, new bool[0] } });
                    GameServer.Reply(conn, op);
                    break;
                case 182:
                case 179: // startup data: a failure here would log the player out
                    GameServer.Reply(conn, op);
                    break;
                default:
                    GameServer.Fail(conn, op, "That did not work. See revival.log for the reason.");
                    break;
            }
            return true;
        }
    }

    private static bool Dispatch(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        switch (op)
        {
            case 211: return Chat(conn, op, request);

            case 191: SocialParty.Invite(conn, op, Text(request, 238)); break;
            case 190: SocialParty.Respond(conn, op, Flag(request, 218)); break;
            case 189: SocialParty.ChangeState(conn, op, Byte(request, 218, 0)); break;
            case 188: SocialParty.Leave(conn, op); break;
            case 187: SocialParty.Kick(conn, op, Text(request, 217)); break;

            case 186: SocialFriends.Invite(conn, op, Text(request, 238)); break;
            case 185: SocialFriends.Respond(conn, op, Text(request, 237), Flag(request, 218)); break;
            case 184: SocialFriends.Remove(conn, op, Text(request, 217)); break;
            case 183: SocialFriends.Statuses(conn, op); break;
            case 182: SocialFriends.MessageBox(conn, op); break;

            case 179: SocialGuild.Get(conn, op, Flag(request, 218)); break;
            case 178: SocialGuild.Create(conn, op, Text(request, 168), Text(request, 167)); break;
            case 177: SocialGuild.Invite(conn, op, Text(request, 238)); break;
            case 176: SocialGuild.Respond(conn, op, Text(request, 237), Flag(request, 218)); break;
            case 175: SocialGuild.Leave(conn, op); break;
            case 174: SocialGuild.Disband(conn, op); break;
            case 173: SocialGuild.Kick(conn, op, Text(request, 217)); break;
            case 172: SocialGuild.ChangeRole(conn, op, Text(request, 217), Byte(request, 218, 255)); break;
            case 171: SocialGuild.Deposit(conn, op, Number(request, 213), Number(request, 218)); break;
            case 170: SocialGuild.Upgrade(conn, op); break;
            case 166: SocialGuild.SetMessageOfTheDay(conn, op, Text(request, 240)); break;
            case 168: SocialGuild.UnlockBuff(conn, op, Text(request, 218)); break;
            case 167: SocialGuild.ActivateBuff(conn, op, Text(request, 218)); break;
            case 153: SocialGuild.HurryBuff(conn, op, Text(request, 218)); break;
        }
        return true;
    }

    // Hooks the session. Safe to call early: it reads social.dat but not the profile.
    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        SocialStore.Load();
        Session.SocialReceived = OnReceived;
        Session.PlayerAppeared = OnAppeared;
        Session.PlayerGone = OnGone;
        Session.Polled = OnPolled;
        PlayerState.ProfileChanged += AnnounceLadder;
        if (Session.Role != Session.Roles.None)
        {
            foreach (string name in new List<string>(Session.Roster))
            {
                if (name != Me)
                {
                    OnAppeared(name);
                }
            }
        }
    }

    // Private, party and guild lines; general chat stays with GameServer.
    private static bool Chat(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
    {
        var type = request.TryGetValue(239, out var channel) && channel != null ? (ChatType)Convert.ToByte(channel) : ChatType.General;
        if (type == ChatType.General) return false;
        string text = Clip(Text(request, 240), MaxChatLength);
        if (text.Length == 0)
        {
            GameServer.Reply(conn, op);
            return true;
        }
        string myGuild = MyCard().Guild;
        switch (type)
        {
            case ChatType.Private:
                string receiver = Text(request, 238);
                if (receiver != Me && !Session.SendSocial(receiver, Pack(Whisper, w => w.Write(text))))
                {
                    FailChat(conn, op, type, receiver.Length == 0 ? null : receiver, NotHere(receiver));
                    return true;
                }
                // The sender's own copy: "-> [receiver] text" in that conversation.
                ShowChat(type, Me, receiver, text, myGuild, CardOf(receiver).Guild);
                break;
            case ChatType.Party:
                var squad = SocialParty.ChatMembers();
                if (squad == null)
                {
                    FailChat(conn, op, type, null, "You are not in a squad.");
                    return true;
                }
                foreach (string member in squad)
                {
                    Session.SendSocial(member, Pack(PartyChat, w => w.Write(text)));
                }
                ShowChat(type, Me, null, text, myGuild, null);
                break;
            case ChatType.Guild:
                if (SocialStore.Guild == null)
                {
                    FailChat(conn, op, type, null, "You are not in a guild.");
                    return true;
                }
                string guildId = SocialStore.Guild.guildId;
                foreach (string member in SocialGuild.MembersHere())
                {
                    Session.SendSocial(member, Pack(GuildChat, w =>
                    {
                        w.Write(guildId);
                        w.Write(text);
                    }));
                }
                ShowChat(type, Me, null, text, myGuild, null);
                break;
            default:
                FailChat(conn, op, type, null, "Unknown chat channel.");
                return true;
        }
        GameServer.Reply(conn, op);
        return true;
    }

    // Event 211 as ChatManager reads it. For a private line key 238 names the conversation:
    // the receiver on the sender's side, the sender on the receiver's side.
    private static void ShowChat(ChatType type, string sender, string conversation, string text, string senderGuild, string receiverGuild)
    {
        RevivalLog.Info($"chat ({type}) {sender}: {text}");
        var data = new Dictionary<byte, object>
        {
            { 218, (byte)type },
            { 237, sender },
            { 240, text },
            { 168, senderGuild ?? string.Empty },
            { 167, UserGroup },
        };
        if (conversation != null)
        {
            data[238] = conversation;
        }
        if (!string.IsNullOrEmpty(receiverGuild))
        {
            data[166] = receiverGuild;
        }
        Event(211, data);
    }

    private static void FailChat(Loopback.VirtualConnection conn, byte op, ChatType type, string receiver, string message)
    {
        RevivalLog.Info($"op {op} failed: {message}");
        var parameters = new Dictionary<byte, object> { { 239, (byte)type }, { 240, message } };
        if (receiver != null)
        {
            parameters[238] = receiver;
        }
        GameServer.Respond(conn, op, ChatFailed, message, parameters, "failure");
    }

    internal static byte[] Pack(byte kind, Action<BinaryWriter> body = null)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        var card = MyCard();
        w.Write(kind);
        w.Write(card.Level);
        w.Write(card.Portrait);
        w.Write(card.ItemLevel);
        w.Write(card.Guild);
        body?.Invoke(w);
        w.Flush();
        return stream.ToArray();
    }

    private static void OnReceived(string sender, byte[] payload)
    {
        var r = new BinaryReader(new MemoryStream(payload));
        byte kind = r.ReadByte();
        var card = new Card { Level = Math.Max(1, r.ReadInt32()), Portrait = SafePortrait(r.ReadInt32()), ItemLevel = r.ReadSingle(), Guild = Clip(r.ReadString(), 8) };
        string oldGuild = CardOf(sender).Guild;
        Cards[sender] = card;
        if (oldGuild != card.Guild)
        {
            SocialFriends.OnGuildTagChanged(sender, card.Guild);
        }
        SocialGuild.OnCard(sender, card);
        switch (kind)
        {
            case Hello:
                SocialGuild.OnHello(sender, r.ReadString(), r.ReadInt64());
                break;
            case Whisper:
                ShowChat(ChatType.Private, sender, sender, Clip(r.ReadString(), MaxChatLength), card.Guild, null);
                break;
            case PartyChat:
                if (SocialParty.IsActiveMember(sender))
                {
                    ShowChat(ChatType.Party, sender, null, Clip(r.ReadString(), MaxChatLength), card.Guild, null);
                }
                break;
            case GuildChat:
                if (SocialGuild.IsMember(sender, r.ReadString()))
                {
                    ShowChat(ChatType.Guild, sender, null, Clip(r.ReadString(), MaxChatLength), card.Guild, null);
                }
                break;
            case FriendInvite: SocialFriends.OnInvite(sender); break;
            case FriendAccept: SocialFriends.OnAccept(sender); break;
            case FriendRemove: SocialFriends.OnRemove(sender); break;
            case PartyInvite:
            case PartyReply:
            case PartyState:
            case PartyLeave:
            case PartyMemberAdded:
            case PartyMemberLeft:
            case PartyMemberState:
            case PartyDisband:
                SocialParty.OnMessage(sender, kind, r);
                break;
            case GuildInvite:
            case GuildJoin:
            case GuildState:
            case GuildGone:
            case GuildRefused:
                SocialGuild.OnMessage(sender, kind, r);
                break;
            case LadderCard:
                LadderTable.Note(sender, r.ReadDouble(), r.ReadInt32(), r.ReadDouble(), r.ReadString());
                break;
        }
    }

    private static void OnAppeared(string name)
    {
        SendHello(name);
        if (!Instance.Dedicated)
        {
            Session.SendSocial(name, Pack(LadderCard, WriteLadder));
        }
        SocialFriends.OnAppeared(name);
        SocialGuild.OnAppeared(name);
    }

    private static void OnGone(string name)
    {
        Cards.Remove(name);
        LadderTable.Forget(name);
        SocialFriends.OnGone(name);
        SocialParty.OnGone(name);
        SocialGuild.OnGone(name);
    }

    // Once a frame, in or out of a session.
    private static void OnPolled()
    {
        SocialParty.Tick();
        SocialGuild.WatchExperience();
        SocialGuild.TickBuffs();
    }

    // Introduces me and says which guild copy I hold, so members can bring each other up to date.
    private static void SendHello(string name) => Session.SendSocial(name, Pack(Hello, SocialGuild.WriteHello));

    // My guild tag changed: everyone here should see it.
    internal static void AnnounceCard() => Session.BroadcastSocial(Pack(Hello, SocialGuild.WriteHello));

    // After a rated battle or a profile switch. A dedicated server's placeholder profile is not a standing.
    internal static void AnnounceLadder()
    {
        try
        {
            if (_attached && !Instance.Dedicated && Session.Role != Session.Roles.None)
            {
                Session.BroadcastSocial(Pack(LadderCard, WriteLadder));
            }
        }
        catch (Exception ex)
        {
            RevivalLog.Error("Could not announce the ladder standing", ex);
        }
    }

    private static void WriteLadder(BinaryWriter w)
    {
        var player = PlayerState.Current;
        w.Write(player.ladderRating);
        w.Write(player.seasonBattles);
        w.Write(LadderOps.LastChange);
        w.Write(LadderTable.Profile);
    }

    internal static Card MyCard()
    {
        var player = PlayerState.Current;
        return new Card
        {
            Level = player.level,
            Portrait = player.currentPortraitIndex,
            ItemLevel = MyItemLevel(),
            Guild = SocialStore.Guild == null ? string.Empty : SocialStore.Guild.abbreviation ?? string.Empty,
        };
    }

    // The client works out the robot's item level from its parts; the garage has it ready.
    private static float MyItemLevel()
    {
        try
        {
            if (GameManager.Instance.garage != null && GameManager.Instance.garage.currentRobotAssembly != null)
            {
                return GameManager.Instance.garage.currentRobotAssembly.itemLevel;
            }
        }
        catch (Exception)
        {
        }
        return 0f;
    }

    // The squad icons look portraits up without a fallback, so another player's number must exist here.
    internal static int SafePortrait(int portrait) =>
        DataFactory.PortraitIndices.Contains(portrait) ? portrait : PlayerState.Current.currentPortraitIndex;

    // Until their first message arrives: my own portrait, which is sure to exist.
    internal static Card CardOf(string name) =>
        name != null && Cards.TryGetValue(name, out var card) ? card : new Card { Portrait = PlayerState.Current.currentPortraitIndex };

    // Sends an event to my own client.
    internal static void Event(byte code, Dictionary<byte, object> parameters) =>
        GameServer.Event(Loopback.Get(PhotonNetwork.networkingPeer), code, parameters);

    internal static string NotHere(string fullName)
    {
        if (Session.Role == Session.Roles.None) return "You are not in a session. Host or join a game on the Play screen first.";
        return (string.IsNullOrEmpty(fullName) ? "That player" : Session.DisplayName(fullName)) + " is not in your session.";
    }

    internal static string Clip(string text, int max)
    {
        text ??= string.Empty;
        return text.Length <= max ? text : text.Substring(0, max);
    }

    private static string Text(Dictionary<byte, object> request, byte key) =>
        request.TryGetValue(key, out var value) && value is string text ? text : string.Empty;

    private static bool Flag(Dictionary<byte, object> request, byte key) =>
        request.TryGetValue(key, out var value) && value is bool flag && flag;

    private static float Number(Dictionary<byte, object> request, byte key) =>
        request.TryGetValue(key, out var value) && value != null ? Convert.ToSingle(value) : 0f;

    private static byte Byte(Dictionary<byte, object> request, byte key, byte fallback) =>
        request.TryGetValue(key, out var value) && value != null ? Convert.ToByte(value) : fallback;
}
