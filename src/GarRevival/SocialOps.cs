using System;
using System.Collections.Generic;
using System.IO;
using ExitGames.Client.Photon;
using JunkWars.Client;
using JunkWars.Common.Player.ValueObjects;
using JunkWars.Common.Utils.Enums;

namespace GarRevival
{
    // Friends, whispers, parties and guilds without a central server. Each
    // player's own built-in server answers their social operations; what
    // concerns another player travels to them through the session (see
    // Session.SendSocial) and their server turns it into the events their
    // client expects. Other players exist only while they are in my session,
    // so "online" means "in my session".
    //
    //   SocialFriends  friend list, invitations, the message box
    //   SocialParty    squads: the leader's server keeps the roster
    //   SocialGuild    guilds: every member keeps a copy, the newest wins
    //   SocialStore    what is remembered between launches
    internal static class SocialOps
    {
        // The only return code that makes the client report a failed chat line.
        private const short ChatFailed = 32751;
        private const byte UserGroup = 2; // AccountGroupType.User
        private const int MaxChatLength = 500;

        // ---- message kinds between players; every message starts with the sender's card ----
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

        // What others need to show me in their lists.
        internal sealed class Card
        {
            public int Level = 1;
            public int Portrait;
            public float ItemLevel;
            public string Guild = string.Empty; // abbreviation
        }

        private static readonly Dictionary<string, Card> Cards = new Dictionary<string, Card>();
        private static bool _attached;

        public static string Me
        {
            get { return PlayerState.Current.fullName; }
        }

        // ---- operations from my own client ----

        public static bool Handle(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            switch (op)
            {
                case 211:
                case 191: case 190: case 189: case 188: case 187:
                case 186: case 185: case 184: case 183: case 182:
                case 179: case 178: case 177: case 176: case 175: case 174: case 173: case 172: case 171: case 170:
                case 168: case 167: case 166: case 153:
                    break;
                default:
                    return false;
            }
            try
            {
                Attach();
                return Dispatch(conn, op, request ?? new Dictionary<byte, object>());
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Social op " + op + " failed", ex);
                switch (op)
                {
                    case 183: // still has to release the client's input blocker
                        GameServer.Event(conn, 169, new Dictionary<byte, object> { { 218, new string[0] }, { 240, new bool[0] } });
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
                case 189: SocialParty.ChangeState(conn, op, request.ContainsKey(218) && request[218] != null ? Convert.ToByte(request[218]) : (byte)0); break;
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
                case 172: SocialGuild.ChangeRole(conn, op, Text(request, 217), request.ContainsKey(218) && request[218] != null ? Convert.ToByte(request[218]) : (byte)255); break;
                case 171: SocialGuild.Deposit(conn, op, Number(request, 213), Number(request, 218)); break;
                case 170: SocialGuild.Upgrade(conn, op); break;
                case 166: SocialGuild.SetMessageOfTheDay(conn, op, Text(request, 240)); break;
                case 168: SocialGuild.UnlockBuff(conn, op, Text(request, 218)); break;
                case 167: SocialGuild.ActivateBuff(conn, op, Text(request, 218)); break;
                case 153: SocialGuild.HurryBuff(conn, op, Text(request, 218)); break;
            }
            return true;
        }

        // Hooks the session. Safe to call early: it reads social.dat but not
        // the profile.
        public static void Attach()
        {
            if (_attached)
            {
                return;
            }
            _attached = true;
            SocialStore.Load();
            Session.SocialReceived = OnReceived;
            Session.PlayerAppeared = OnAppeared;
            Session.PlayerGone = OnGone;
            Session.Polled = OnPolled;
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

        // ---- chat: private, party and guild lines (general chat stays with GameServer) ----

        private static bool Chat(Loopback.VirtualConnection conn, byte op, Dictionary<byte, object> request)
        {
            ChatType type = request.ContainsKey(239) && request[239] != null ? (ChatType)Convert.ToByte(request[239]) : ChatType.General;
            if (type == ChatType.General)
            {
                return false;
            }
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
                    List<string> squad = SocialParty.ChatMembers();
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

        // Event 211 as the client's ChatManager reads it. For a private line
        // key 238 names the conversation it belongs to: the receiver on the
        // sender's side, the sender on the receiver's side.
        private static void ShowChat(ChatType type, string sender, string conversation, string text, string senderGuild, string receiverGuild)
        {
            RevivalLog.Info("chat (" + type + ") " + sender + ": " + text);
            Dictionary<byte, object> data = new Dictionary<byte, object>
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
            RevivalLog.Info("op " + op + " failed: " + message);
            Dictionary<byte, object> parameters = new Dictionary<byte, object> { { 239, (byte)type }, { 240, message } };
            if (receiver != null)
            {
                parameters[238] = receiver;
            }
            OperationResponse response = new OperationResponse { OperationCode = op, ReturnCode = ChatFailed, DebugMessage = message, Parameters = parameters };
            Loopback.Enqueue(conn, "op " + op + " failure", listener => listener.OnOperationResponse(response));
        }

        // ---- messages from other players ----

        internal static byte[] Pack(byte kind, Action<BinaryWriter> body = null)
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter w = new BinaryWriter(stream);
            Card card = MyCard();
            w.Write(kind);
            w.Write(card.Level);
            w.Write(card.Portrait);
            w.Write(card.ItemLevel);
            w.Write(card.Guild);
            if (body != null)
            {
                body(w);
            }
            w.Flush();
            return stream.ToArray();
        }

        private static void OnReceived(string sender, byte[] payload)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(payload));
            byte kind = r.ReadByte();
            Card card = new Card { Level = Math.Max(1, r.ReadInt32()), Portrait = SafePortrait(r.ReadInt32()), ItemLevel = r.ReadSingle(), Guild = Clip(r.ReadString(), 8) };
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
            }
        }

        private static void OnAppeared(string name)
        {
            SendHello(name);
            SocialFriends.OnAppeared(name);
            SocialGuild.OnAppeared(name);
        }

        private static void OnGone(string name)
        {
            Cards.Remove(name);
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

        // Introduces me (my card rides on every message) and says which guild
        // copy I hold, so members can bring each other up to date.
        private static void SendHello(string name)
        {
            Session.SendSocial(name, Pack(Hello, SocialGuild.WriteHello));
        }

        // My guild tag changed: everyone here should see it.
        internal static void AnnounceCard()
        {
            Session.BroadcastSocial(Pack(Hello, SocialGuild.WriteHello));
        }

        // ---- shared helpers ----

        internal static Card MyCard()
        {
            PlayerVO player = PlayerState.Current;
            return new Card
            {
                Level = player.level,
                Portrait = player.currentPortraitIndex,
                ItemLevel = MyItemLevel(),
                Guild = SocialStore.Guild == null ? string.Empty : SocialStore.Guild.abbreviation ?? string.Empty,
            };
        }

        // The robot's item level is worked out by the client from its parts;
        // the garage has it ready.
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

        // The squad icons look portraits up without a fallback, so a number
        // from another player must be one this client has.
        internal static int SafePortrait(int portrait)
        {
            return DataFactory.PortraitIndices.Contains(portrait) ? portrait : PlayerState.Current.currentPortraitIndex;
        }

        internal static Card CardOf(string name)
        {
            // Until their first message arrives: my own portrait, which is sure to exist.
            Card card;
            return name != null && Cards.TryGetValue(name, out card) ? card : new Card { Portrait = PlayerState.Current.currentPortraitIndex };
        }

        // Sends an event to my own client.
        internal static void Event(byte code, Dictionary<byte, object> parameters)
        {
            GameServer.Event(Loopback.Get(PhotonNetwork.networkingPeer), code, parameters);
        }

        internal static string NotHere(string fullName)
        {
            if (Session.Role == Session.Roles.None)
            {
                return "You are not in a session. Host or join a game on the Play screen first.";
            }
            return (string.IsNullOrEmpty(fullName) ? "That player" : Session.DisplayName(fullName)) + " is not in your session.";
        }

        internal static string Clip(string text, int max)
        {
            text = text ?? string.Empty;
            return text.Length <= max ? text : text.Substring(0, max);
        }

        private static string Text(Dictionary<byte, object> request, byte key)
        {
            object value;
            return request.TryGetValue(key, out value) && value is string ? (string)value : string.Empty;
        }

        private static bool Flag(Dictionary<byte, object> request, byte key)
        {
            object value;
            return request.TryGetValue(key, out value) && value is bool && (bool)value;
        }

        private static float Number(Dictionary<byte, object> request, byte key)
        {
            object value;
            return request.TryGetValue(key, out value) && value != null ? Convert.ToSingle(value) : 0f;
        }
    }
}
