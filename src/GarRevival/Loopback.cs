using System;
using System.Collections.Generic;
using ExitGames.Client.Photon;

namespace GarRevival
{
    // Stands in for Photon's socket layer. Each PhotonPeer the game creates gets
    // a VirtualConnection; operations it "sends" go to GameServer, and the
    // answers are queued and handed back when the game polls the peer, exactly
    // as real network traffic would be.
    internal static class Loopback
    {
        internal sealed class Delivery
        {
            public string Label;
            public Func<bool> IsReady;
            public Action<IPhotonPeerListener> Deliver;
        }

        internal sealed class VirtualConnection
        {
            public PhotonPeer Peer;
            public PeerStateValue State = PeerStateValue.Disconnected;
            public string Address = string.Empty;
            public bool EncryptionReady;
            public readonly Queue<Delivery> Incoming = new Queue<Delivery>();
        }

        private static readonly Dictionary<PhotonPeer, VirtualConnection> Connections = new Dictionary<PhotonPeer, VirtualConnection>();

        public static VirtualConnection Get(PhotonPeer peer)
        {
            VirtualConnection conn;
            if (!Connections.TryGetValue(peer, out conn))
            {
                conn = new VirtualConnection { Peer = peer };
                Connections[peer] = conn;
            }
            return conn;
        }

        public static Delivery Enqueue(VirtualConnection conn, string label, Action<IPhotonPeerListener> deliver, Func<bool> isReady = null)
        {
            Delivery delivery = new Delivery { Label = label, Deliver = deliver, IsReady = isReady };
            conn.Incoming.Enqueue(delivery);
            return delivery;
        }

        public static bool Connect(PhotonPeer peer, string address)
        {
            VirtualConnection conn = Get(peer);
            if (conn.State != PeerStateValue.Disconnected)
            {
                RevivalLog.Info("Connect ignored, peer is " + conn.State);
                return false;
            }
            RevivalLog.Info(peer.GetType().Name + " connecting to " + address);
            conn.Incoming.Clear();
            conn.State = PeerStateValue.Connecting;
            conn.Address = address ?? string.Empty;
            conn.EncryptionReady = false;
            Enqueue(conn, "status Connect", listener =>
            {
                conn.State = PeerStateValue.Connected;
                listener.OnStatusChanged(StatusCode.Connect);
            });
            return true;
        }

        public static void Disconnect(PhotonPeer peer)
        {
            VirtualConnection conn = Get(peer);
            if (conn.State == PeerStateValue.Disconnected || conn.State == PeerStateValue.Disconnecting)
            {
                return;
            }
            RevivalLog.Info(peer.GetType().Name + " disconnecting from " + conn.Address);
            conn.Incoming.Clear();
            conn.State = PeerStateValue.Disconnecting;
            Enqueue(conn, "status Disconnect", listener =>
            {
                conn.State = PeerStateValue.Disconnected;
                conn.EncryptionReady = false;
                listener.OnStatusChanged(StatusCode.Disconnect);
            });
        }

        public static bool EstablishEncryption(PhotonPeer peer)
        {
            VirtualConnection conn = Get(peer);
            if (conn.State != PeerStateValue.Connected)
            {
                return false;
            }
            Enqueue(conn, "status EncryptionEstablished", listener =>
            {
                conn.EncryptionReady = true;
                listener.OnStatusChanged(StatusCode.EncryptionEstablished);
            });
            return true;
        }

        public static bool Send(PhotonPeer peer, byte opCode, Dictionary<byte, object> parameters)
        {
            VirtualConnection conn = Get(peer);
            if (conn.State != PeerStateValue.Connected)
            {
                RevivalLog.Info("Dropped op " + opCode + " while " + conn.State);
                return false;
            }
            try
            {
                GameServer.Handle(conn, opCode, parameters ?? new Dictionary<byte, object>());
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Server failed on op " + opCode, ex);
                OperationResponse failure = new OperationResponse { OperationCode = opCode, ReturnCode = -1, DebugMessage = "GN_InternalServerError", Parameters = new Dictionary<byte, object>() };
                Enqueue(conn, "op " + opCode + " failure", listener => listener.OnOperationResponse(failure));
            }
            return true;
        }

        // Delivers at most one queued message per call, like Photon does, and
        // returns whether more are waiting.
        public static bool Dispatch(PhotonPeer peer)
        {
            VirtualConnection conn;
            if (!Connections.TryGetValue(peer, out conn) || conn.Incoming.Count == 0)
            {
                return false;
            }
            Delivery next = conn.Incoming.Peek();
            if (next.IsReady != null && !next.IsReady())
            {
                return false;
            }
            conn.Incoming.Dequeue();
            DateTime started = DateTime.Now;
            RevivalLog.Info("deliver " + next.Label);
            try
            {
                next.Deliver(peer.Listener);
                RevivalLog.Info("delivered " + next.Label + " in " + (int)(DateTime.Now - started).TotalMilliseconds + " ms");
            }
            catch (Exception ex)
            {
                RevivalLog.Error("Game threw while handling " + next.Label, ex);
            }
            return conn.Incoming.Count > 0;
        }

        public static PeerStateValue StateOf(PhotonPeer peer)
        {
            VirtualConnection conn;
            return Connections.TryGetValue(peer, out conn) ? conn.State : PeerStateValue.Disconnected;
        }

        public static bool IsEncryptionReady(PhotonPeer peer)
        {
            VirtualConnection conn;
            return Connections.TryGetValue(peer, out conn) && conn.EncryptionReady;
        }
    }
}
