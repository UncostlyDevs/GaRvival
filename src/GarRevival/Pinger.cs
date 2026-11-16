using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace GarRevival;

// Round-trip time as the TCP connect time to a host's game port (the host silently drops a connection
// that never says hello). Polled from the main thread; nothing blocks.
internal static class Pinger
{
    public const int Pending = -2;
    public const int Unreachable = -1;

    private const float Timeout = 2f;
    private const float Keep = 30f;
    private const int MaxInFlight = 8;

    private sealed class Probe
    {
        public string Host;
        public int Port;
        public IAsyncResult Resolving;
        public TcpClient Client;
        public IAsyncResult Connecting;
        public readonly Stopwatch Watch = new();
        public volatile int ConnectedMs = -1; // written by the connect callback
        public float Started = -1f;
        public float Finished = -1f;
        public int Ms = Pending;
    }

    private static readonly Dictionary<string, Probe> Probes = new();

    // Milliseconds, Pending or Unreachable. Asking starts or refreshes a measurement.
    public static int Get(string host, int port)
    {
        string key = host + ":" + port;
        float now = Time.realtimeSinceStartup;
        if (!Probes.TryGetValue(key, out var probe) || (probe.Finished >= 0f && now - probe.Finished > Keep))
        {
            var fresh = new Probe { Host = host, Port = port, Ms = probe == null ? Pending : probe.Ms };
            Probes[key] = fresh;
            return fresh.Ms;
        }
        return probe.Ms;
    }

    public static void Poll()
    {
        float now = Time.realtimeSinceStartup;
        int inFlight = Probes.Values.Count(p => p.Started >= 0f && p.Finished < 0f);
        foreach (var p in Probes.Values.Where(p => p.Finished < 0f).ToList())
        {
            try
            {
                if (p.Started < 0f)
                {
                    if (inFlight < MaxInFlight)
                    {
                        inFlight++;
                        Begin(p, now);
                    }
                }
                else if (now - p.Started > Timeout)
                {
                    End(p, Unreachable, now);
                }
                else if (p.Resolving != null && p.Resolving.IsCompleted)
                {
                    var address = Dns.EndGetHostAddresses(p.Resolving).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    p.Resolving = null;
                    if (address == null)
                    {
                        End(p, Unreachable, now);
                    }
                    else
                    {
                        Connect(p, address);
                    }
                }
                else if (p.Connecting != null && p.Connecting.IsCompleted)
                {
                    p.Client.EndConnect(p.Connecting);
                    End(p, (int)Math.Max(1, p.ConnectedMs), now);
                }
            }
            catch (Exception)
            {
                End(p, Unreachable, now);
            }
        }
    }

    private static void Begin(Probe p, float now)
    {
        p.Started = now;
        p.Watch.Start();
        if (IPAddress.TryParse(p.Host, out var address))
        {
            Connect(p, address);
        }
        else
        {
            p.Resolving = Dns.BeginGetHostAddresses(p.Host, null, null);
        }
    }

    private static void Connect(Probe p, IPAddress address)
    {
        p.Client = new TcpClient(AddressFamily.InterNetwork);
        p.Connecting = p.Client.BeginConnect(address, p.Port, _ => p.ConnectedMs = (int)p.Watch.ElapsedMilliseconds, null);
    }

    private static void End(Probe p, int ms, float now)
    {
        p.Ms = ms;
        p.Finished = now;
        p.Resolving = null;
        p.Connecting = null;
        p.Client?.Close();
        p.Client = null;
    }

    public static string Label(int ms) => ms == Pending ? "..." : ms == Unreachable ? "-" : ms + " ms";
}
