using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Steamworks;

namespace GarRevival
{
    // A multiplayer session: one player hosts, others join, over Steam or a
    // direct connection. Each player's garage, profile and unlocks stay on
    // their own PC (their built-in server keeps answering those operations);
    // the session carries what players share. For now that is the player
    // list and garage chat.
    internal static class Session
    {
        public enum Roles { None, Host, Client }

        public const byte ProtocolVersion = 3;
        private const byte MsgHello = 1;    // client -> host: version, full name
        private const byte MsgWelcome = 2;  // host -> client: host name, server rules, roster
        private const byte MsgRejected = 3; // host -> client: reason
        private const byte MsgChat = 4;     // either way: sender, text, guild
        private const byte MsgJoined = 5;   // host -> clients: full name
        private const byte MsgLeft = 6;     // host -> clients: full name
        private const byte MsgBattle = 7;   // either way: a SessionBattle message
        private const byte MsgSocial = 8;   // to the host: target name ("" = everyone), payload; from the host: sender name, payload

        // Friends, parties, guilds and whispers ride on the session but live
        // outside this file; they attach here so the session does not depend
        // on them.
#pragma warning disable 0649 // assigned by the social code, which is compiled separately
        public static Action<string, byte[]> SocialReceived; // sender's full name, payload
        public static Action<string> PlayerAppeared;         // another player is now in my session (each one already there when I join, too)
        public static Action<string> PlayerGone;             // another player is no longer in my session (everyone, when I leave)
        public static Action Polled;                         // once per Poll, in or out of a session
#pragma warning restore 0649

        private sealed class Member
        {
            public Link Link;
            public string Name; // null until the Hello arrives
        }

        private static readonly List<Member> Members = new List<Member>(); // host only: the remote players
        private static readonly List<string> Names = new List<string>();   // everyone in the session, self included
        private static TcpHostListener _listener;
        private static Link _host; // client only

        public static Roles Role = Roles.None;

        // Client: the host has let us in.
        public static bool Welcomed;

        // Why the last attempt to join ended before the host let us in; null while it is running.
        public static string LastJoinError;

        // The port a LAN game is hosted on, or 0.
        public static int TcpPort;

        // Milliseconds on the host's clock; the same value on every member
        // (give or take the connection delay), for timestamping movement.
        private static int _clockOffset;

        public static int ClockMs
        {
            get { return Environment.TickCount + _clockOffset; }
        }

        // The other players in the session (host only).
        public static List<string> RemoteNames
        {
            get { return Members.Where(m => m.Name != null).Select(m => m.Name).ToList(); }
        }

        public static void SendBattleToHost(byte[] payload)
        {
            if (_host != null)
            {
                _host.Send(Pack(MsgBattle, w => w.Write(payload)));
            }
        }

        public static void SendBattleTo(string name, byte[] payload)
        {
            Member member = Members.FirstOrDefault(m => m.Name == name);
            if (member != null)
            {
                member.Link.Send(Pack(MsgBattle, w => w.Write(payload)));
            }
        }

        public static void BroadcastBattle(byte[] payload, string exceptName)
        {
            byte[] message = Pack(MsgBattle, w => w.Write(payload));
            foreach (Member member in Members)
            {
                if (member.Name != null && member.Name != exceptName)
                {
                    member.Link.Send(message);
                }
            }
        }
        public static string Status = "Not in a session";

        public static string LocalName
        {
            get { return PlayerState.Current.fullName; }
        }

        public static string LocalDisplayName
        {
            get { return PlayerState.Current.playerName; }
        }

        public static IList<string> Roster
        {
            get { return Names; }
        }

        // Players in the session; a dedicated server does not count itself.
        public static int PlayerCount
        {
            get { return Math.Max(0, Names.Count - (Instance.Dedicated && Role == Roles.Host ? 1 : 0)); }
        }

        // ---- starting and leaving ----

        public static void HostTcp(int port)
        {
            Leave(null);
            // If the port is taken (a second copy of the game on this PC), try the next few.
            Exception problem = null;
            for (int candidate = port; candidate < port + 10 && _listener == null; candidate++)
            {
                try
                {
                    _listener = new TcpHostListener(candidate);
                    TcpPort = candidate;
                }
                catch (Exception ex)
                {
                    problem = ex;
                }
            }
            if (_listener == null)
            {
                SetStatus("Could not host on port " + port + ": " + problem.Message);
                return;
            }
            BecomeHost("Hosting a LAN game on port " + TcpPort);
        }

        public static void HostSteam()
        {
            Leave(null);
            SetStatus(SteamNet.Host() ? "Creating Steam lobby..." : "Steam is not available");
        }

        public static void OnSteamHostResult(bool ok, string error)
        {
            if (ok)
            {
                BecomeHost("Hosting on Steam");
            }
            else
            {
                SetStatus(error);
            }
        }

        private static void BecomeHost(string status)
        {
            Role = Roles.Host;
            _clockOffset = 0;
            Names.Clear();
            Names.Add(LocalName);
            SetStatus(status);
            GameServer.Notice("You are hosting. Other players can join now.");
            ServerRules.Enter(ServerRules.Hosted);
        }

        public static void JoinTcp(string host, int port)
        {
            Leave(null);
            BeginJoin(new TcpLink(host, port), "Connecting to " + host + ":" + port + "...");
        }

        public static void JoinSteam(CSteamID lobby)
        {
            Leave(null);
            if (SteamNet.Join(lobby))
            {
                SetStatus("Joining Steam lobby...");
                return;
            }
            SetStatus("Steam is not available");
            LastJoinError = Status;
        }

        public static void OnSteamJoinResult(Link host, string error)
        {
            if (host == null)
            {
                SetStatus(error);
                LastJoinError = error;
                return;
            }
            BeginJoin(host, "Connecting to " + host.Label + "...");
        }

        private static void BeginJoin(Link host, string status)
        {
            Role = Roles.Client;
            _host = host;
            Welcomed = false;
            SetStatus(status);
            _host.Send(Pack(MsgHello, w =>
            {
                w.Write(ProtocolVersion);
                w.Write(LocalName);
            }));
        }

        public static void Leave(string reason)
        {
            if (Role == Roles.None && _listener == null && SteamNet.Lobby == CSteamID.Nil)
            {
                return;
            }
            if (Role == Roles.Client && !Welcomed && reason != null)
            {
                LastJoinError = reason;
            }
            Welcomed = false;
            foreach (Member member in Members)
            {
                member.Link.Close();
            }
            Members.Clear();
            string[] others = Names.Count == 0 ? new string[0] : Names.Where(n => n != LocalName).ToArray();
            Names.Clear();
            if (_host != null)
            {
                _host.Close();
                _host = null;
            }
            if (_listener != null)
            {
                _listener.Stop();
                _listener = null;
            }
            TcpPort = 0;
            SteamNet.Leave();
            SessionBattle.OnSessionEnded();
            bool wasIn = Role != Roles.None;
            Role = Roles.None;
            SetStatus(reason ?? "Not in a session");
            if (wasIn)
            {
                GameServer.Notice(reason ?? "You left the session.");
                ServerRules.Exit();
            }
            foreach (string other in others)
            {
                Raise(PlayerGone, other);
            }
        }

        private static void SetStatus(string status)
        {
            Status = status;
            RevivalLog.Info("Session: " + status);
        }

        // ---- running ----

        public static void AddIncoming(Link link)
        {
            Members.Add(new Member { Link = link });
        }

        public static void Poll()
        {
            SteamNet.Poll();
            if (Role == Roles.Host)
            {
                if (_listener != null)
                {
                    for (TcpLink link = _listener.Accept(); link != null; link = _listener.Accept())
                    {
                        AddIncoming(link);
                    }
                }
                foreach (Member member in Members.ToArray())
                {
                    for (byte[] message = member.Link.Receive(); message != null; message = member.Link.Receive())
                    {
                        Guard(member.Link, () => HandleAsHost(member, message));
                    }
                    if (member.Link.Closed)
                    {
                        Members.Remove(member);
                        if (member.Name != null)
                        {
                            MemberLeft(member.Name);
                            Broadcast(Pack(MsgLeft, w => w.Write(member.Name)), null);
                        }
                    }
                }
            }
            else if (Role == Roles.Client && _host != null)
            {
                for (byte[] message = _host.Receive(); message != null && _host != null; message = _host == null ? null : _host.Receive())
                {
                    Guard(_host, () => HandleAsClient(message));
                }
                if (_host != null && _host.Closed)
                {
                    Leave("Lost the connection to the host.");
                }
            }
            if (Polled != null)
            {
                try
                {
                    Polled();
                }
                catch (Exception ex)
                {
                    RevivalLog.Error("Social upkeep failed", ex);
                    Polled = null;
                }
            }
        }

        private static void Guard(Link link, Action handle)
        {
            try
            {
                handle();
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Bad session message from " + link.Label, ex);
                link.Close();
            }
        }

        private static void HandleAsHost(Member member, byte[] message)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(message));
            byte type = r.ReadByte();
            if (member.Name == null)
            {
                if (type != MsgHello)
                {
                    member.Link.Close();
                    return;
                }
                byte version = r.ReadByte();
                string name = r.ReadString();
                string problem = version != ProtocolVersion ? "This server runs a different GAR Revival version."
                    : name.Length == 0 || name.Length > 64 ? "Invalid player name."
                    : Names.Contains(name) ? "A player called " + name + " is already here."
                    : ServerRules.Active != null && PlayerCount >= ServerRules.Active.MaxPlayers ? "The server is full."
                    : null;
                if (problem != null)
                {
                    member.Link.Send(Pack(MsgRejected, w => w.Write(problem)));
                    RevivalLog.Info("Session: rejected " + member.Link.Label + ": " + problem);
                    return;
                }
                member.Name = name;
                member.Link.Send(Pack(MsgWelcome, w =>
                {
                    w.Write(LocalDisplayName);
                    w.Write(ServerRules.Pack(ServerRules.Active ?? ServerRules.Hosted));
                    w.Write(Environment.TickCount);
                    w.Write(Names.Count);
                    foreach (string existing in Names)
                    {
                        w.Write(existing);
                    }
                }));
                Broadcast(Pack(MsgJoined, w => w.Write(name)), member);
                MemberJoined(name);
                return;
            }
            if (type == MsgChat)
            {
                r.ReadString(); // the sender field is not trusted; the host knows who this is
                string text = r.ReadString();
                string guild = r.ReadString();
                if (text.Length > 0 && text.Length <= 500)
                {
                    RelayChat(member.Name, text, guild);
                }
            }
            else if (type == MsgBattle)
            {
                SessionBattle.FromClient(member.Name, r.ReadBytes(message.Length - 1));
            }
            else if (type == MsgSocial)
            {
                string target = r.ReadString();
                int length = r.ReadInt32();
                if (length >= 0 && length <= message.Length)
                {
                    RouteSocial(member, target, r.ReadBytes(length));
                }
            }
        }

        private static void HandleAsClient(byte[] message)
        {
            BinaryReader r = new BinaryReader(new MemoryStream(message));
            switch (r.ReadByte())
            {
                case MsgWelcome:
                    string hostName = r.ReadString();
                    string rules = r.ReadString();
                    _clockOffset = r.ReadInt32() - Environment.TickCount;
                    Names.Clear();
                    for (int i = r.ReadInt32(); i > 0; i--)
                    {
                        Names.Add(r.ReadString());
                    }
                    Names.Add(LocalName);
                    Welcomed = true;
                    SetStatus("In " + hostName + "'s session");
                    GameServer.Notice("You joined " + hostName + "'s session (" + Names.Count + " players).");
                    ServerRules.Enter(ServerRules.Unpack(rules));
                    foreach (string other in Names.Where(n => n != LocalName).ToArray())
                    {
                        Raise(PlayerAppeared, other);
                    }
                    break;
                case MsgRejected:
                    Leave("The host refused the connection: " + r.ReadString());
                    break;
                case MsgChat:
                    string sender = r.ReadString();
                    string text = r.ReadString();
                    GameServer.DeliverChat(sender, text, r.ReadString());
                    break;
                case MsgJoined:
                    MemberJoined(r.ReadString());
                    break;
                case MsgLeft:
                    MemberLeft(r.ReadString());
                    break;
                case MsgBattle:
                    SessionBattle.FromHost(r.ReadBytes(message.Length - 1));
                    break;
                case MsgSocial:
                    string from = r.ReadString();
                    int length = r.ReadInt32();
                    if (length >= 0 && length <= message.Length)
                    {
                        Raise(SocialReceived, from, r.ReadBytes(length));
                    }
                    break;
            }
        }

        private static void MemberJoined(string name)
        {
            Names.Add(name);
            RevivalLog.Info("Session: " + name + " joined (" + Names.Count + " players)");
            GameServer.Notice(DisplayName(name) + " joined.");
            Raise(PlayerAppeared, name);
        }

        private static void MemberLeft(string name)
        {
            Names.Remove(name);
            RevivalLog.Info("Session: " + name + " left (" + Names.Count + " players)");
            GameServer.Notice(DisplayName(name) + " left.");
            Raise(PlayerGone, name);
        }

        // "name.tag" -> "name"
        public static string DisplayName(string fullName)
        {
            return fullName.Length > 5 ? fullName.Substring(0, fullName.Length - 5) : fullName;
        }

        // ---- chat ----

        // The local player typed a message in the garage chat.
        public static void SendChat(string text, string guild)
        {
            guild = guild ?? string.Empty;
            if (Role == Roles.Client && _host != null)
            {
                // The host sends it back to everyone, us included.
                _host.Send(Pack(MsgChat, w =>
                {
                    w.Write(LocalName);
                    w.Write(text);
                    w.Write(guild);
                }));
            }
            else if (Role == Roles.Host)
            {
                RelayChat(LocalName, text, guild);
            }
            else
            {
                GameServer.DeliverChat(LocalName, text, guild);
            }
        }

        private static void RelayChat(string sender, string text, string guild)
        {
            Broadcast(Pack(MsgChat, w =>
            {
                w.Write(sender);
                w.Write(text);
                w.Write(guild);
            }), null);
            GameServer.DeliverChat(sender, text, guild);
            DedicatedServer.OnChat(sender, text);
        }

        // ---- social messages ----

        public static bool InSession(string fullName)
        {
            return Role != Roles.None && Names.Contains(fullName);
        }

        // Sends a payload to one other player in the session; false when
        // that player is not here.
        public static bool SendSocial(string target, byte[] payload)
        {
            if (Role == Roles.Host)
            {
                Member member = Members.FirstOrDefault(m => m.Name == target);
                if (member == null)
                {
                    return false;
                }
                member.Link.Send(PackSocial(LocalName, payload));
                return true;
            }
            if (Role == Roles.Client && _host != null && target != LocalName && Names.Contains(target))
            {
                _host.Send(PackSocial(target, payload));
                return true;
            }
            return false;
        }

        // Sends a payload to every other player in the session.
        public static void BroadcastSocial(byte[] payload)
        {
            if (Role == Roles.Host)
            {
                Broadcast(PackSocial(LocalName, payload), null);
            }
            else if (Role == Roles.Client && _host != null)
            {
                _host.Send(PackSocial(string.Empty, payload));
            }
        }

        private static byte[] PackSocial(string name, byte[] payload)
        {
            return Pack(MsgSocial, w =>
            {
                w.Write(name);
                w.Write(payload.Length);
                w.Write(payload);
            });
        }

        // The host passes a client's social message on to its target, or to
        // everyone, stamped with the sender's real name.
        private static void RouteSocial(Member from, string target, byte[] payload)
        {
            if (target.Length == 0)
            {
                Broadcast(PackSocial(from.Name, payload), from);
                Raise(SocialReceived, from.Name, payload);
            }
            else if (target == LocalName)
            {
                Raise(SocialReceived, from.Name, payload);
            }
            else
            {
                Member member = Members.FirstOrDefault(m => m.Name == target);
                if (member != null && member != from)
                {
                    member.Link.Send(PackSocial(from.Name, payload));
                }
            }
        }

        // A failing social handler must not take the connection down with it.
        private static void Raise(Action<string, byte[]> handler, string name, byte[] payload)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(name, payload);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Social message from " + name + " failed", ex);
            }
        }

        private static void Raise(Action<string> handler, string name)
        {
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(name);
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Social update for " + name + " failed", ex);
            }
        }

        private static void Broadcast(byte[] message, Member except)
        {
            foreach (Member member in Members)
            {
                if (member != except && member.Name != null)
                {
                    member.Link.Send(message);
                }
            }
        }

        private static byte[] Pack(byte type, Action<BinaryWriter> write)
        {
            MemoryStream stream = new MemoryStream();
            BinaryWriter writer = new BinaryWriter(stream);
            writer.Write(type);
            write(writer);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
