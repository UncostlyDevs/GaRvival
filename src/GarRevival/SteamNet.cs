using System;
using System.Collections.Generic;
using Steamworks;

namespace GarRevival;

// Steam transport: a public lobby is the listing; Steam P2P carries the session (no port forwarding).
internal static class SteamNet
{
    // The game never uses Steam P2P; a channel of our own anyway.
    private const int Channel = 7;
    private const int MaxPlayers = 16;
    private const string LobbyTag = "garrevival";

    internal sealed class LobbyInfo
    {
        public CSteamID Id;
        public string Name;
        public int Players;
    }

    private sealed class SteamLink : Link
    {
        public CSteamID Remote;
        public readonly Queue<byte[]> Inbox = new();

        public override void Send(byte[] message)
        {
            if (!Closed && !SteamNetworking.SendP2PPacket(Remote, message, (uint)message.Length, EP2PSend.k_EP2PSendReliable, Channel))
            {
                RevivalLog.Info($"Steam link {Label}: send failed");
                Closed = true;
            }
        }

        public override byte[] Receive() => Inbox.Count > 0 ? Inbox.Dequeue() : null;

        public override void Close()
        {
            Closed = true;
            SteamNetworking.CloseP2PSessionWithUser(Remote);
            Links.Remove((ulong)Remote);
        }
    }

    private static readonly Dictionary<ulong, SteamLink> Links = new();
    private static Steamworks.Callback<P2PSessionRequest_t> _sessionRequest;
    private static Steamworks.Callback<P2PSessionConnectFail_t> _sessionFailed;
    private static Steamworks.Callback<GameLobbyJoinRequested_t> _joinRequested;
    private static CallResult<LobbyCreated_t> _created;
    private static CallResult<LobbyMatchList_t> _listed;
    private static CallResult<LobbyEnter_t> _entered;
    private static bool _ready;

    public static readonly List<LobbyInfo> Lobbies = [];
    public static string ListStatus = string.Empty;
    public static CSteamID Lobby = CSteamID.Nil;
    public static bool Hosting;

    public static bool Available => SteamBridge.Initialized();

    private static bool EnsureReady()
    {
        if (_ready) return true;
        if (!Available) return false;
        _sessionRequest = Steamworks.Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
        _sessionFailed = Steamworks.Callback<P2PSessionConnectFail_t>.Create(OnSessionFailed);
        _joinRequested = Steamworks.Callback<GameLobbyJoinRequested_t>.Create(request => Session.JoinSteam(request.m_steamIDLobby));
        _created = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        _listed = CallResult<LobbyMatchList_t>.Create(OnLobbyList);
        _entered = CallResult<LobbyEnter_t>.Create(OnLobbyEntered);
        _ready = true;
        return true;
    }

    // At startup, so Steam's "join game" on a friend works before the multiplayer window was opened.
    public static void Init() => EnsureReady();

    public static bool Host()
    {
        if (!EnsureReady()) return false;
        _created.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, MaxPlayers));
        return true;
    }

    private static void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
    {
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            Session.OnSteamHostResult(false, $"Steam could not create a lobby ({(ioFailure ? "no connection" : result.m_eResult.ToString())})");
            return;
        }
        Lobby = new CSteamID(result.m_ulSteamIDLobby);
        Hosting = true;
        SteamMatchmaking.SetLobbyData(Lobby, LobbyTag, "1");
        SteamMatchmaking.SetLobbyData(Lobby, "name", $"{Session.LocalDisplayName}'s garage");
        SteamMatchmaking.SetLobbyData(Lobby, "mode", "open");
        Session.OnSteamHostResult(true, null);
    }

    public static void RefreshLobbies()
    {
        if (!EnsureReady())
        {
            ListStatus = "Steam is not available";
            return;
        }
        ListStatus = "Searching...";
        SteamMatchmaking.AddRequestLobbyListStringFilter(LobbyTag, "1", ELobbyComparison.k_ELobbyComparisonEqual);
        SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
        _listed.Set(SteamMatchmaking.RequestLobbyList());
    }

    private static void OnLobbyList(LobbyMatchList_t result, bool ioFailure)
    {
        Lobbies.Clear();
        if (ioFailure)
        {
            ListStatus = "Steam search failed";
            return;
        }
        for (int i = 0; i < (int)result.m_nLobbiesMatching; i++)
        {
            var id = SteamMatchmaking.GetLobbyByIndex(i);
            Lobbies.Add(new LobbyInfo { Id = id, Name = SteamMatchmaking.GetLobbyData(id, "name"), Players = SteamMatchmaking.GetNumLobbyMembers(id) });
        }
        ListStatus = Lobbies.Count == 0 ? "No games found." : $"{Lobbies.Count} game(s)";
        RevivalLog.Info($"Steam lobby list: {ListStatus}");
    }

    public static bool Join(CSteamID lobby)
    {
        if (!EnsureReady()) return false;
        _entered.Set(SteamMatchmaking.JoinLobby(lobby));
        return true;
    }

    private static void OnLobbyEntered(LobbyEnter_t result, bool ioFailure)
    {
        // 1 = k_EChatRoomEnterResponseSuccess
        if (ioFailure || result.m_EChatRoomEnterResponse != 1)
        {
            Session.OnSteamJoinResult(null, "Could not enter the Steam lobby");
            return;
        }
        Lobby = new CSteamID(result.m_ulSteamIDLobby);
        Session.OnSteamJoinResult(LinkTo(SteamMatchmaking.GetLobbyOwner(Lobby)), null);
    }

    private static SteamLink LinkTo(CSteamID remote)
    {
        if (!Links.TryGetValue((ulong)remote, out var link))
        {
            link = new SteamLink { Remote = remote, Label = SteamFriends.GetFriendPersonaName(remote) };
            Links[(ulong)remote] = link;
        }
        return link;
    }

    private static void OnSessionRequest(P2PSessionRequest_t request)
    {
        // Accept only as host, or from the host we chose to join.
        if (Hosting || Links.ContainsKey((ulong)request.m_steamIDRemote))
        {
            SteamNetworking.AcceptP2PSessionWithUser(request.m_steamIDRemote);
        }
    }

    private static void OnSessionFailed(P2PSessionConnectFail_t failure)
    {
        if (Links.TryGetValue((ulong)failure.m_steamIDRemote, out var link))
        {
            RevivalLog.Info($"Steam link {link.Label}: connection failed ({failure.m_eP2PSessionError})");
            link.Closed = true;
        }
    }

    public static void Poll()
    {
        if (!_ready) return;
        while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
        {
            var data = new byte[size];
            if (!SteamNetworking.ReadP2PPacket(data, size, out size, out var sender, Channel)) break;
            if (!Links.TryGetValue((ulong)sender, out var link))
            {
                if (!Hosting) continue;
                link = LinkTo(sender);
                Session.AddIncoming(link);
            }
            link.Inbox.Enqueue(data);
        }
    }

    public static void Leave()
    {
        if (Lobby != CSteamID.Nil)
        {
            SteamMatchmaking.LeaveLobby(Lobby);
            Lobby = CSteamID.Nil;
        }
        Hosting = false;
        foreach (var link in new List<SteamLink>(Links.Values))
        {
            link.Close();
        }
        Links.Clear();
    }
}
