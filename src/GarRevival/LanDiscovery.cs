using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace GarRevival
{
    // Finds games hosted on the local network. A host broadcasts a small UDP
    // beacon every two seconds; anyone with the Join screen open listens for
    // them. No configuration needed on a normal home network.
    internal static class LanDiscovery
    {
        private const int BeaconPort = 27016;
        private const string Magic = "GARREV1";
        private const float BeaconInterval = 2f;
        private const float Expiry = 6f;

        internal sealed class Game
        {
            public string Address;
            public int Port;
            public string Name;
            public int Players;
            public float Seen;
        }

        public static readonly List<Game> Games = new List<Game>();
        public static bool Listening;

        private static UdpClient _listener;
        private static UdpClient _sender;
        private static bool _listenFailed;
        private static float _nextBeacon;

        public static void Poll()
        {
            float now = Time.realtimeSinceStartup;
            if (Session.Role == Session.Roles.Host && Session.TcpPort > 0 && now >= _nextBeacon)
            {
                _nextBeacon = now + BeaconInterval;
                SendBeacon();
            }
            if (!Listening)
            {
                return;
            }
            if (_listener == null && !_listenFailed)
            {
                try
                {
                    _listener = new UdpClient();
                    // Lets two copies of the game on one PC both listen.
                    _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    _listener.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
                }
                catch (Exception ex)
                {
                    _listenFailed = true;
                    _listener = null;
                    RevivalLog.Error("LAN discovery cannot listen on UDP " + BeaconPort, ex);
                }
            }
            try
            {
                while (_listener != null && _listener.Available > 0)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    string[] parts = Encoding.UTF8.GetString(_listener.Receive(ref from)).Split('\t');
                    int port, players;
                    if (parts.Length == 4 && parts[0] == Magic && int.TryParse(parts[2], out port) && int.TryParse(parts[3], out players))
                    {
                        Found(from.Address.ToString(), port, parts[1], players, now);
                    }
                }
            }
            catch (Exception ex)
            {
                RevivalLog.Error("LAN discovery receive failed", ex);
            }
            Games.RemoveAll(g => now - g.Seen > Expiry);
        }

        private static void Found(string address, int port, string name, int players, float now)
        {
            // Not our own game.
            if (Session.Role == Session.Roles.Host && port == Session.TcpPort && name == Session.LocalDisplayName)
            {
                return;
            }
            Game game = Games.Find(g => g.Address == address && g.Port == port);
            if (game == null)
            {
                game = new Game { Address = address, Port = port };
                Games.Add(game);
                RevivalLog.Info("LAN game found: " + name + " at " + address + ":" + port);
            }
            game.Name = name;
            game.Players = players;
            game.Seen = now;
        }

        private static void SendBeacon()
        {
            try
            {
                if (_sender == null)
                {
                    _sender = new UdpClient();
                    _sender.EnableBroadcast = true;
                }
                byte[] beacon = Encoding.UTF8.GetBytes(Magic + "\t" + Session.LocalDisplayName + "\t" + Session.TcpPort + "\t" + Session.Roster.Count);
                _sender.Send(beacon, beacon.Length, new IPEndPoint(IPAddress.Broadcast, BeaconPort));
            }
            catch (Exception ex)
            {
                RevivalLog.Error("LAN beacon failed", ex);
                _sender = null;
            }
        }

        // This PC's addresses on the local network, for telling friends.
        public static List<string> LocalAddresses()
        {
            List<string> result = new List<string>();
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    {
                        result.Add(address.ToString());
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }
    }
}
