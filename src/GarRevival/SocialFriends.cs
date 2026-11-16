using System;
using System.Collections.Generic;
using System.Linq;
using JunkWars.Common.Social;
using JunkWars.Common.Utils;
using JunkWars.Common.Utils.Enums;

namespace GarRevival;

// Friend list (PlayerVO.friendList); a friend is online while in my session. Invitations go through the session
// and wait in the receiver's message box (EventMessagesManager) until answered, across restarts.
internal static class SocialFriends
{
    private static List<string> Friends => PlayerState.Current.friendList;

    // 186: the answer carries the name back; the client shows "invitation sent".
    public static void Invite(Loopback.VirtualConnection conn, byte op, string name)
    {
        if (name.Length == 0 || name == SocialOps.Me)
        {
            GameServer.Fail(conn, op, "You cannot invite yourself.");
            return;
        }
        if (Friends.Contains(name))
        {
            GameServer.Fail(conn, op, Session.DisplayName(name) + " is already your friend.");
            return;
        }
        if (FindInvitation(name) != null)
        {
            Accept(name); // they asked first: inviting them back is a yes
        }
        else if (!Session.SendSocial(name, SocialOps.Pack(SocialOps.FriendInvite)))
        {
            GameServer.Fail(conn, op, SocialOps.NotHere(name));
            return;
        }
        else if (!SocialStore.SentFriendInvites.Contains(name))
        {
            SocialStore.SentFriendInvites.Add(name);
            SocialStore.Save();
        }
        GameServer.Reply(conn, op, new() { { 238, name } });
    }

    // 185: answer to an invitation in the message box.
    public static void Respond(Loopback.VirtualConnection conn, byte op, string name, bool accept)
    {
        if (FindInvitation(name) == null)
        {
            PushInbox(); // the client's list was stale
        }
        else if (accept)
        {
            Accept(name);
        }
        else
        {
            SocialStore.Inbox.Remove(FindInvitation(name));
            SocialStore.Save();
            PushInbox();
        }
        GameServer.Reply(conn, op);
    }

    // 184
    public static void Remove(Loopback.VirtualConnection conn, byte op, string name)
    {
        if (Forget(name))
        {
            SocialStore.Outbox.RemoveAll(n => n.Target == name);
            Tell(name, SocialOps.FriendRemove);
        }
        GameServer.Reply(conn, op);
    }

    // 183: the answer is event 169, which also releases the client's input blocker.
    public static void Statuses(Loopback.VirtualConnection conn, byte op)
    {
        var names = Friends.ToArray();
        GameServer.Event(conn, 169, new()
        {
            { 218, names },
            { 240, names.Select(n => Session.InSession(n)).ToArray() },
            { 255, names.Select(n => SocialOps.CardOf(n).Guild).ToArray() },
        });
        GameServer.Reply(conn, op);
    }

    // 182: friend and guild invitations waiting for an answer.
    public static void MessageBox(Loopback.VirtualConnection conn, byte op) =>
        GameServer.Reply(conn, op, new() { { 218, DataSerialization.ProtoSerializeObject(SocialStore.Inbox) } });

    public static void OnInvite(string sender)
    {
        if (Friends.Contains(sender))
        {
            // They lost me from their list; give them the yes again.
            Session.SendSocial(sender, SocialOps.Pack(SocialOps.FriendAccept));
            return;
        }
        AddToInbox(sender, EventMessageType.FriendInvitation, string.Empty);
    }

    public static void OnAccept(string sender)
    {
        // Only someone I invited can put themselves on my list.
        if (SocialStore.SentFriendInvites.Remove(sender))
        {
            SocialStore.Save();
            Befriend(sender);
        }
    }

    public static void OnRemove(string sender) => Forget(sender);

    public static void OnAppeared(string name)
    {
        foreach (var notice in SocialStore.Outbox.Where(n => n.Target == name).ToArray())
        {
            Session.SendSocial(name, SocialOps.Pack(notice.Kind));
            SocialStore.Outbox.Remove(notice);
            SocialStore.Save();
        }
        if (Friends.Contains(name))
        {
            SocialOps.Event(168, new() { { 218, name }, { 240, true } });
        }
    }

    public static void OnGone(string name)
    {
        if (Friends.Contains(name))
        {
            SocialOps.Event(168, new() { { 218, name }, { 240, false } });
        }
    }

    public static void OnGuildTagChanged(string name, string tag)
    {
        if (Friends.Contains(name))
        {
            SocialOps.Event(154, new() { { 217, name }, { 255, tag } });
        }
    }

    // One entry per sender and kind: the client's list is keyed by sender.
    public static void AddToInbox(string sender, EventMessageType type, string message)
    {
        SocialStore.Inbox.RemoveAll(m => m.sender == sender && m.type == type);
        SocialStore.Inbox.Add(new SocialMessageData { sender = sender, receiver = SocialOps.Me, type = type, message = message, date = DateTime.UtcNow });
        SocialStore.Save();
        PushInbox();
    }

    // Event 167 replaces the client's whole list.
    public static void PushInbox() =>
        SocialOps.Event(167, new() { { 218, DataSerialization.ProtoSerializeObject(SocialStore.Inbox) } });

    private static SocialMessageData FindInvitation(string sender) =>
        SocialStore.Inbox.FirstOrDefault(m => m.sender == sender && m.type == EventMessageType.FriendInvitation);

    private static void Accept(string name)
    {
        SocialStore.Inbox.Remove(FindInvitation(name));
        SocialStore.SentFriendInvites.Remove(name);
        SocialStore.Outbox.RemoveAll(n => n.Target == name);
        SocialStore.Save();
        PushInbox();
        Befriend(name);
        Tell(name, SocialOps.FriendAccept);
    }

    // Event 171: the client adds the friend and asks for statuses again (op 183).
    private static void Befriend(string name)
    {
        if (Friends.Contains(name)) return;
        Friends.Add(name);
        PlayerState.Save();
        SocialOps.Event(171, new() { { 217, name }, { 255, SocialOps.CardOf(name).Guild } });
    }

    private static bool Forget(string name)
    {
        if (!Friends.Remove(name)) return false;
        PlayerState.Save();
        SocialOps.Event(170, new() { { 217, name } });
        return true;
    }

    // Now if they are here, otherwise the next time we meet.
    private static void Tell(string name, byte kind)
    {
        if (!Session.SendSocial(name, SocialOps.Pack(kind)))
        {
            SocialStore.Outbox.Add(new SocialStore.Notice { Target = name, Kind = kind });
            SocialStore.Save();
        }
    }
}
