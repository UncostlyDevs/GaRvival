using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JunkWars.Client;
using JunkWars.Common.Utils.Party;

namespace GarRevival
{
    // Squads ("parties") among the players of a session. The leader's server
    // owns the roster and tells the members what changed; members send their
    // own changes to the leader. Every server keeps a mirror so it can give
    // its client the events PartyManager expects:
    //   182 the whole party (also sent to an invited player, who is a member
    //       in state Invited until they answer), 173 the invitation pop-up,
    //   174 my own state changed, 177 someone else's, 176 their level/portrait,
    //   179 a player was invited, 178 a player left or declined,
    //   175 I am out (left, kicked, disbanded, declined).
    // A party lives only as long as its members stay in the session, and it
    // ends when the leader leaves it.
    internal static class SocialParty
    {
        // The squad screen has three seats.
        private const int MaxSize = 3;

        private const byte Invited = (byte)PartyMemberState.Invited;
        private const byte NotReady = (byte)PartyMemberState.NotReady;
        private const byte Ready = (byte)PartyMemberState.Ready;

        private sealed class Member
        {
            public string Name;
            public byte State;
            public byte Role;
            public int Level = 1;
            public int Portrait;
            public float ItemLevel;
        }

        private static string _id; // null when not in a party
        private static string _leader;
        private static bool _awayFromGarage;
        private static readonly List<Member> Members = new List<Member>();

        private static bool Leading
        {
            get { return _id != null && _leader == SocialOps.Me; }
        }

        private static Member Find(string name)
        {
            return Members.FirstOrDefault(m => m.Name == name);
        }

        private static Member Mine
        {
            get { return Find(SocialOps.Me); }
        }

        // ---- operations ----

        // 191
        public static void Invite(Loopback.VirtualConnection conn, byte op, string name)
        {
            string problem =
                name.Length == 0 || name == SocialOps.Me ? "You cannot invite yourself."
                : _id != null && !Leading ? "Only the squad leader can invite players."
                : _id != null && Find(name) != null ? Session.DisplayName(name) + " is already in your squad."
                : _id != null && Members.Count >= MaxSize ? "Your squad is full."
                : !Session.InSession(name) ? SocialOps.NotHere(name)
                : null;
            if (problem != null)
            {
                GameServer.Fail(conn, op, problem);
                return;
            }
            Member invited = FromCard(name, Invited, PartyMemberRole.Member);
            if (_id == null)
            {
                _id = Guid.NewGuid().ToString("N");
                _leader = SocialOps.Me;
                Members.Add(FromCard(SocialOps.Me, NotReady, PartyMemberRole.Leader));
                Members.Add(invited);
                ShowParty();
            }
            else
            {
                Members.Add(invited);
                ShowJoined(invited);
                ToOthers(name, SocialOps.Pack(SocialOps.PartyMemberAdded, w =>
                {
                    w.Write(_id);
                    Write(w, invited);
                }));
            }
            Session.SendSocial(name, SocialOps.Pack(SocialOps.PartyInvite, w =>
            {
                w.Write(_id);
                w.Write(Members.Count);
                foreach (Member member in Members)
                {
                    Write(w, member);
                }
            }));
            RevivalLog.Info("Party: invited " + name);
            GameServer.Reply(conn, op);
        }

        // 190: my answer to the invitation pop-up.
        public static void Respond(Loopback.VirtualConnection conn, byte op, bool accept)
        {
            Member me = _id == null ? null : Mine;
            if (me != null && me.State == Invited)
            {
                string leader = _leader;
                string id = _id;
                if (accept)
                {
                    me.State = NotReady;
                    SocialOps.Event(174, new Dictionary<byte, object> { { 232, NotReady } });
                }
                else
                {
                    ShowOut();
                }
                Session.SendSocial(leader, SocialOps.Pack(SocialOps.PartyReply, w =>
                {
                    w.Write(id);
                    w.Write(accept);
                }));
            }
            // Otherwise the party was gone before the answer; nothing to do.
            GameServer.Reply(conn, op);
        }

        // 189: ready / not ready.
        public static void ChangeState(Loopback.VirtualConnection conn, byte op, byte state)
        {
            Member me = _id == null ? null : Mine;
            if (me == null || me.State == Invited)
            {
                GameServer.Fail(conn, op, "You are not in a squad.");
                return;
            }
            if (state != NotReady && state != Ready)
            {
                GameServer.Fail(conn, op, "Unknown squad state.");
                return;
            }
            SetMyState(me, state);
            GameServer.Reply(conn, op);
        }

        private static void SetMyState(Member me, byte state)
        {
            me.State = state;
            Refresh(me);
            SocialOps.Event(174, new Dictionary<byte, object> { { 232, state } });
            if (Leading)
            {
                ToOthers(null, StateMessage(me));
            }
            else
            {
                string id = _id;
                Session.SendSocial(_leader, SocialOps.Pack(SocialOps.PartyState, w =>
                {
                    w.Write(id);
                    w.Write(state);
                }));
            }
        }

        // Once a frame. The original server cleared the ready flags when a
        // battle ended; here every member clears their own on returning to
        // the garage, so "ready" always means ready for the next match.
        public static void Tick()
        {
            if (_id == null)
            {
                _awayFromGarage = false;
                return;
            }
            if (GameManager.Instance.currentGameState != GameState.Garage)
            {
                _awayFromGarage = true;
            }
            else if (_awayFromGarage)
            {
                _awayFromGarage = false;
                Member me = Mine;
                if (me != null && me.State == Ready)
                {
                    SetMyState(me, NotReady);
                }
            }
        }

        // 188: a member leaves; the leader leaving disbands the party.
        public static void Leave(Loopback.VirtualConnection conn, byte op)
        {
            if (_id != null)
            {
                string id = _id;
                if (Leading)
                {
                    ToOthers(null, SocialOps.Pack(SocialOps.PartyDisband, w => w.Write(id)));
                }
                else
                {
                    Session.SendSocial(_leader, SocialOps.Pack(SocialOps.PartyLeave, w => w.Write(id)));
                }
                ShowOut();
            }
            GameServer.Reply(conn, op);
        }

        // 187
        public static void Kick(Loopback.VirtualConnection conn, byte op, string name)
        {
            if (!Leading || name == SocialOps.Me || Find(name) == null)
            {
                GameServer.Fail(conn, op, "Only the squad leader can remove a member.");
                return;
            }
            string id = _id;
            Session.SendSocial(name, SocialOps.Pack(SocialOps.PartyDisband, w => w.Write(id)));
            RemoveMember(name);
            GameServer.Reply(conn, op);
        }

        // ---- from other players ----

        public static void OnMessage(string sender, byte kind, BinaryReader r)
        {
            string id = r.ReadString();
            if (kind == SocialOps.PartyInvite)
            {
                OnInvite(sender, id, r);
                return;
            }
            if (_id == null || id != _id)
            {
                return;
            }
            if (Leading)
            {
                Member member = Find(sender);
                if (member == null)
                {
                    return;
                }
                switch (kind)
                {
                    case SocialOps.PartyReply:
                        if (member.State != Invited)
                        {
                            break;
                        }
                        if (r.ReadBoolean())
                        {
                            Refresh(member);
                            SetState(member, NotReady);
                            ToOthers(sender, StateMessage(member));
                        }
                        else
                        {
                            RemoveMember(sender);
                        }
                        break;
                    case SocialOps.PartyState:
                        byte state = r.ReadByte();
                        if (member.State != Invited && (state == NotReady || state == Ready))
                        {
                            Refresh(member);
                            SetState(member, state);
                            ToOthers(sender, StateMessage(member));
                        }
                        break;
                    case SocialOps.PartyLeave:
                        RemoveMember(sender);
                        break;
                }
            }
            else if (sender == _leader)
            {
                switch (kind)
                {
                    case SocialOps.PartyMemberAdded:
                        Member added = Read(r);
                        if (Find(added.Name) == null)
                        {
                            added.State = Invited;
                            added.Role = (byte)PartyMemberRole.Member;
                            Members.Add(added);
                            ShowJoined(added);
                        }
                        break;
                    case SocialOps.PartyMemberLeft:
                        string left = r.ReadString();
                        if (left == SocialOps.Me)
                        {
                            ShowOut();
                        }
                        else if (Find(left) != null)
                        {
                            ShowLeft(left);
                        }
                        break;
                    case SocialOps.PartyMemberState:
                        Member update = Read(r);
                        Member known = Find(update.Name);
                        if (known != null && known.Name != SocialOps.Me)
                        {
                            known.Level = update.Level;
                            known.Portrait = update.Portrait;
                            known.ItemLevel = update.ItemLevel;
                            SetState(known, update.State);
                        }
                        break;
                    case SocialOps.PartyDisband:
                        ShowOut();
                        break;
                }
            }
        }

        private static void OnInvite(string sender, string id, BinaryReader r)
        {
            int count = r.ReadInt32();
            List<Member> members = new List<Member>();
            for (int i = 0; i < count && i < MaxSize; i++)
            {
                members.Add(Read(r));
            }
            bool sound = count <= MaxSize
                && members.Select(m => m.Name).Distinct().Count() == members.Count
                && members.All(m => m.State <= Ready && (m.Role == (byte)PartyMemberRole.Leader) == (m.Name == sender))
                && members.Any(m => m.Name == sender)
                && members.Any(m => m.Name == SocialOps.Me && m.State == Invited);
            if (_id != null || !sound)
            {
                // Busy (or nonsense): to the leader this is a "declined".
                Session.SendSocial(sender, SocialOps.Pack(SocialOps.PartyReply, w =>
                {
                    w.Write(id);
                    w.Write(false);
                }));
                return;
            }
            _id = id;
            _leader = sender;
            Members.AddRange(members);
            ShowParty();
            SocialOps.Event(173, new Dictionary<byte, object> { { 237, sender } });
            RevivalLog.Info("Party: invited by " + sender);
        }

        public static void OnGone(string name)
        {
            if (_id == null)
            {
                return;
            }
            if (name == _leader)
            {
                ShowOut();
            }
            else if (Leading && Find(name) != null)
            {
                RemoveMember(name);
            }
        }

        // ---- for chat ----

        // The members a party chat line goes to, or null when I am not in a party.
        public static List<string> ChatMembers()
        {
            Member me = _id == null ? null : Mine;
            if (me == null || me.State == Invited)
            {
                return null;
            }
            return Members.Where(m => m != me && m.State != Invited).Select(m => m.Name).ToList();
        }

        public static bool IsActiveMember(string name)
        {
            Member me = _id == null ? null : Mine;
            Member other = _id == null ? null : Find(name);
            return me != null && me.State != Invited && other != null && other.State != Invited;
        }

        // ---- roster changes and the events that show them ----

        // Leader only: a member left, declined, was removed or dropped out of
        // the session. A party of one is no party.
        private static void RemoveMember(string name)
        {
            ShowLeft(name);
            string id = _id;
            ToOthers(null, SocialOps.Pack(SocialOps.PartyMemberLeft, w =>
            {
                w.Write(id);
                w.Write(name);
            }));
            if (Members.Count <= 1)
            {
                ShowOut();
            }
        }

        private static void SetState(Member member, byte state)
        {
            // Data first: the client's "joined" notice reads the member afterwards.
            SocialOps.Event(176, new Dictionary<byte, object> { { 215, member.Name }, { 169, member.Level }, { 223, member.Portrait }, { 218, member.ItemLevel } });
            if (member.State != state)
            {
                member.State = state;
                SocialOps.Event(177, new Dictionary<byte, object> { { 215, member.Name }, { 232, state } });
            }
        }

        private static void ShowParty()
        {
            SocialOps.Event(182, new Dictionary<byte, object>
            {
                { 255, _id },
                { 215, Members.Select(m => m.Name).ToArray() },
                { 232, Members.Select(m => m.State).ToArray() },
                { 231, Members.Select(m => m.Role).ToArray() },
                { 169, Members.Select(m => m.Level).ToArray() },
                { 223, Members.Select(m => m.Portrait).ToArray() },
                { 234, Members.Select(m => m.ItemLevel).ToArray() },
            });
        }

        private static void ShowJoined(Member member)
        {
            SocialOps.Event(179, new Dictionary<byte, object> { { 215, member.Name }, { 169, member.Level }, { 223, member.Portrait }, { 218, member.ItemLevel } });
        }

        private static void ShowLeft(string name)
        {
            Members.Remove(Find(name));
            SocialOps.Event(178, new Dictionary<byte, object> { { 215, name } });
        }

        // Event 175 ends the party on my client.
        private static void ShowOut()
        {
            _id = null;
            _leader = null;
            Members.Clear();
            SocialOps.Event(175, new Dictionary<byte, object>());
        }

        // ---- wire ----

        // To every member but me and, optionally, one more.
        private static void ToOthers(string except, byte[] message)
        {
            foreach (Member member in Members)
            {
                if (member.Name != SocialOps.Me && member.Name != except)
                {
                    Session.SendSocial(member.Name, message);
                }
            }
        }

        private static byte[] StateMessage(Member member)
        {
            string id = _id;
            return SocialOps.Pack(SocialOps.PartyMemberState, w =>
            {
                w.Write(id);
                Write(w, member);
            });
        }

        private static Member FromCard(string name, byte state, PartyMemberRole role)
        {
            SocialOps.Card card = name == SocialOps.Me ? SocialOps.MyCard() : SocialOps.CardOf(name);
            return new Member { Name = name, State = state, Role = (byte)role, Level = card.Level, Portrait = card.Portrait, ItemLevel = card.ItemLevel };
        }

        // Brings a member's level, portrait and robot up to date from their latest card.
        private static void Refresh(Member member)
        {
            Member fresh = FromCard(member.Name, member.State, (PartyMemberRole)member.Role);
            member.Level = fresh.Level;
            member.Portrait = fresh.Portrait;
            member.ItemLevel = fresh.ItemLevel;
        }

        private static void Write(BinaryWriter w, Member member)
        {
            w.Write(member.Name);
            w.Write(member.State);
            w.Write(member.Role);
            w.Write(member.Level);
            w.Write(member.Portrait);
            w.Write(member.ItemLevel);
        }

        private static Member Read(BinaryReader r)
        {
            return new Member
            {
                Name = SocialOps.Clip(r.ReadString(), 64),
                State = r.ReadByte(),
                Role = r.ReadByte(),
                Level = r.ReadInt32(),
                Portrait = SocialOps.SafePortrait(r.ReadInt32()),
                ItemLevel = r.ReadSingle(),
            };
        }
    }
}
